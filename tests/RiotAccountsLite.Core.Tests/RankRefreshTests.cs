using System.Net;
using RiotAccountsLite.Core;
using Xunit;

public sealed class RankRefreshTests
{
    private static RiotAccount Account(string label) => new(Guid.NewGuid(), label, label, "JP1", "JP1");

    private sealed class FakeProvider(Func<RiotAccount, Task>? onCall = null) : IRankProvider
    {
        public List<Guid> Calls { get; } = [];
        public async Task<RankSnapshot> RefreshRanksAsync(RiotAccount account, CancellationToken ct, IProgress<string>? progress = null)
        {
            Calls.Add(account.Id); progress?.Report("ranks");
            if (onCall != null) await onCall(account);
            return new(DateTimeOffset.UtcNow, []);
        }
    }

    private sealed class Reports : IProgress<string> { public List<string> Values { get; } = []; public void Report(string value) => Values.Add(value); }

    private static StoreFixture Seed(params RiotAccount[] accounts)
    {
        var fixture = new StoreFixture();
        foreach (var account in accounts) fixture.Store.Save(account);
        return fixture;
    }

    [Fact]
    public async Task RunsEveryAccountInRegistrationOrder()
    {
        var second = Account("Second"); var first = Account("First");
        using var fixture = Seed(second, first);
        var provider = new FakeProvider(); var progress = new Reports();
        var result = await new RankRefresh(fixture.Store, provider).RunAsync(progress, default);
        Assert.Equal(new[] { second.Id, first.Id }, provider.Calls);
        Assert.Equal(RefreshStop.None, result.Stop);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal("全2件のランクを更新しました。", result.Summary);
        Assert.Contains("ランクを更新中 1/2 — Second", progress.Values);
        Assert.Contains("ランクを更新中 2/2 — First: ranks", progress.Values);
    }

    [Fact]
    public async Task NotFoundAndShapeErrorsAreRecordedAndTheRunContinues()
    {
        var first = Account("First"); var second = Account("Second"); var third = Account("Third");
        using var fixture = Seed(first, second, third);
        var provider = new FakeProvider(account =>
            account.Id == first.Id ? throw new OpggException("OP.GGでアカウントが見つかりません。", HttpStatusCode.NotFound)
            : account.Id == second.Id ? throw new OpggException("OP.GGの応答形式が想定と異なります。")
            : Task.CompletedTask);
        var result = await new RankRefresh(fixture.Store, provider).RunAsync(new Reports(), default);
        Assert.Equal(3, provider.Calls.Count);
        Assert.Equal(RefreshStop.None, result.Stop);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal("OP.GGでアカウントが見つかりません。", result.Outcomes.Single(o => o.AccountId == first.Id).Message);
        Assert.Equal("1/3件のランクを更新しました。取得できなかったアカウント：First、Second", result.Summary);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ServerErrorsDoNotStopTheRun(HttpStatusCode status)
    {
        using var fixture = Seed(Account("First"), Account("Second"));
        var provider = new FakeProvider(_ => throw new OpggException("拒否", status));
        var result = await new RankRefresh(fixture.Store, provider).RunAsync(new Reports(), default);
        Assert.Equal(RefreshStop.None, result.Stop);
        Assert.Equal(2, provider.Calls.Count);
        Assert.All(result.Outcomes, o => Assert.False(o.Ok));
    }

    [Fact]
    public async Task RateLimitExhaustionStopsTheRun()
    {
        using var fixture = Seed(Account("First"), Account("Second"));
        var provider = new FakeProvider(_ => throw new OpggException("OP.GGの取得制限が続いています。", HttpStatusCode.TooManyRequests));
        var result = await new RankRefresh(fixture.Store, provider).RunAsync(new Reports(), default);
        Assert.Equal(RefreshStop.RateLimit, result.Stop);
        Assert.Single(provider.Calls);
        Assert.False(Assert.Single(result.Outcomes).Ok);
        Assert.StartsWith("OP.GGの取得制限が続いたため中断しました（0/2件を更新）", result.Summary);
    }

    [Fact]
    public async Task LostConnectionOrTimeoutStopsTheRun()
    {
        using var fixture = Seed(Account("First"), Account("Second"));
        var network = await new RankRefresh(fixture.Store, new FakeProvider(_ => throw new HttpRequestException("down"))).RunAsync(new Reports(), default);
        Assert.Equal(RefreshStop.Network, network.Stop);
        Assert.Equal("OP.GGに接続できませんでした。", Assert.Single(network.Outcomes).Message);
        var timeout = await new RankRefresh(fixture.Store, new FakeProvider(_ => throw new TaskCanceledException("timeout"))).RunAsync(new Reports(), default);
        Assert.Equal(RefreshStop.Network, timeout.Stop);
        Assert.Equal("OP.GGの応答がタイムアウトしました。", Assert.Single(timeout.Outcomes).Message);
        Assert.StartsWith("OP.GGに接続できないため中断しました（0/2件を更新）", timeout.Summary);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var fixture = Seed(Account("First"), Account("Second"));
        using var cancel = new CancellationTokenSource();
        var provider = new FakeProvider(_ => { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RankRefresh(fixture.Store, provider).RunAsync(new Reports(), cancel.Token));
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task UnexpectedErrorsPropagate()
    {
        using var fixture = Seed(Account("First"), Account("Second"));
        var provider = new FakeProvider(_ => throw new InvalidOperationException("bug"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RankRefresh(fixture.Store, provider).RunAsync(new Reports(), default));
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task AccountsDeletedOrRenamedDuringTheRunArePickedUp()
    {
        var first = Account("First"); var second = Account("Second"); var third = Account("Third");
        using var fixture = Seed(first, second, third);
        var provider = new FakeProvider(account =>
        {
            if (account.Id == first.Id)
            {
                fixture.Store.Delete(second.Id);
                fixture.Store.Save(third with { Label = "Renamed" });
            }
            return Task.CompletedTask;
        });
        var result = await new RankRefresh(fixture.Store, provider).RunAsync(new Reports(), default);
        Assert.Equal(new[] { first.Id, third.Id }, provider.Calls);
        Assert.Equal("Renamed", result.Outcomes.Single(o => o.AccountId == third.Id).Account);
        Assert.Equal("全2件のランクを更新しました。", result.Summary);
    }

    [Fact]
    public async Task NoAccountsIsAnEmptyRun()
    {
        using var fixture = Seed();
        var result = await new RankRefresh(fixture.Store, new FakeProvider()).RunAsync(new Reports(), default);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Outcomes);
    }
}
