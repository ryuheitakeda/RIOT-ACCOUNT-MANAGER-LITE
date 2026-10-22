using System.Net;
using System.Text;
using System.Text.Json;
using RiotAccountsLite.Core;
using Xunit;

// All OP.GG payloads here are synthetic; they mirror only the fields the provider reads.
public sealed class OpggTests
{
    private static RiotAccount Account() => new(Guid.NewGuid(), "Main", "Player", "JP1", "JP1");
    private static OpggApi Api(FakeOpgg handler, DiagnosticLog? log = null) => new(new HttpClient(handler), TimeSpan.Zero, log);

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private static object Summary(string tier = "GOLD", int division = 2, int lp = 45, string? updatedAt = "2026-10-22T09:00:00+09:00") => new
    {
        data = new { summoner = new { updated_at = updatedAt, league_stats = new object[]
        {
            new { game_type = "SOLORANKED", win = 30, lose = 25, tier_info = new { tier, division, lp } },
            new { game_type = "FLEXRANKED", win = (int?)null, lose = (int?)null, tier_info = new { tier = (string?)null, division = (int?)null, lp = (int?)null } },
            new { game_type = "ARENA", win = 1, lose = 1, tier_info = new { tier = "GOLD", division = 1, lp = 0 } }
        } } }
    };

    private sealed class FakeOpgg : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public string[] Paths => Requests.Select(r => r.RequestUri!.AbsolutePath).ToArray();
        public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; init; }
        public object[] SearchResults { get; set; } = [new { game_name = "player", tagline = "jp1", summoner_id = "sid-1" }];
        // Summoner ids OP.GG answers 404 for, like an id it no longer knows.
        public HashSet<string> UnknownSummoners { get; } = [];
        public object SummaryBody { get; set; } = Summary();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Intercept?.Invoke(request) is { } intercepted) return Task.FromResult(intercepted);
            var uri = request.RequestUri!;
            Assert.Equal(OpggApi.SummonerHost, uri.Host);
            if (uri.AbsolutePath == "/api/v3/jp/summoners")
            {
                Assert.StartsWith("?riot_id=", uri.Query);
                return Task.FromResult(Json(new { data = SearchResults }));
            }
            var parts = uri.AbsolutePath.Split('/');
            Assert.Equal(new[] { "", "api", "jp", "summoners", parts[4], "summary" }, parts);
            return Task.FromResult(UnknownSummoners.Contains(Uri.UnescapeDataString(parts[4]))
                ? Json(new { message = "data not found." }, HttpStatusCode.NotFound) : Json(SummaryBody));
        }
    }

    [Fact]
    public void ProfileUrlUsesRegionAndEscapesRiotId()
    {
        Assert.Equal("https://op.gg/lol/summoners/jp/Player-JP1", OpggRegions.ProfileUrl(Account()).AbsoluteUri);
        Assert.Equal("https://op.gg/lol/summoners/euw/%E3%81%82%20b-%23%2F1",
            OpggRegions.ProfileUrl(Account() with { GameName = "あ b", TagLine = "#/1", Platform = "EUW1" }).AbsoluteUri);
        Assert.Throws<ArgumentException>(() => OpggRegions.ProfileUrl(Account() with { Platform = "PBE1" }));
    }

    [Fact]
    public void RegionsMapEveryServerTheDialogOffers()
    {
        Assert.Equal("jp", OpggRegions.Region("JP1"));
        Assert.Equal("eune", OpggRegions.Region("EUN1"));
        Assert.Equal("oce", OpggRegions.Region("oc1"));
        Assert.All(Regions.Platforms, platform => OpggRegions.Region(platform));
        Assert.Throws<ArgumentException>(() => OpggRegions.Region("PBE1"));
    }

    [Theory]
    [InlineData("lol-api-champion.op.gg")]
    [InlineData("op.gg")]
    [InlineData("www.op.gg")]
    [InlineData("example.com")]
    [InlineData("LOL-API-SUMMONER.OP.GG")]
    public async Task OnlyTheSummonerHostIsAllowed(string host)
    {
        var handler = new FakeOpgg(); using var api = Api(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => api.GetAsync(host, "/api/v3/jp/summoners?riot_id=x", default));
        Assert.Empty(handler.Requests);
        Assert.Equal("lol-api-summoner.op.gg", OpggApi.SummonerHost);
    }

    [Fact]
    public async Task RanksAreMappedAndUnrankedQueuesSkipped()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var handler = new FakeOpgg(); using var api = Api(handler);
        var snapshot = await new OpggRankProvider(fixture.Store, api).RefreshRanksAsync(account, default);
        var saved = fixture.Store.LatestRanks(account.Id);
        Assert.NotNull(saved);
        Assert.Equal(new RankEntry(Queues.Solo, "GOLD", "II", 45, 30, 25), Assert.Single(saved.Entries));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), snapshot.OpggUpdatedAt);
        Assert.Equal(snapshot.ObservedAt, saved.ObservedAt);
        Assert.Equal(account, Assert.Single(fixture.Store.Accounts()));
        Assert.Equal(new OpggIdentity("jp", "Player", "JP1", "sid-1"), OpggRankProvider.Identity(fixture.Store, account.Id));
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("https", r.RequestUri!.Scheme);
            Assert.Contains("RiotAccountsLite", r.Headers.UserAgent.ToString());
        });
    }

    [Fact]
    public async Task ApexTierShowsNoDivisionAndMissingUpdateTimeIsNull()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var handler = new FakeOpgg { SummaryBody = Summary("challenger", 1, 1234, null) }; using var api = Api(handler);
        var snapshot = await new OpggRankProvider(fixture.Store, api).RefreshRanksAsync(account, default);
        Assert.Equal("CHALLENGER  1234 LP", Assert.Single(snapshot.Entries).Display);
        Assert.Null(snapshot.OpggUpdatedAt);
    }

    [Fact]
    public async Task IdentityIsReusedUntilRiotIdOrServerChanges()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var handler = new FakeOpgg(); using var api = Api(handler);
        var provider = new OpggRankProvider(fixture.Store, api);
        await provider.RefreshRanksAsync(account, default);
        await provider.RefreshRanksAsync(account, default);
        var renamed = account with { TagLine = "JP2" };
        fixture.Store.Save(renamed);
        handler.SearchResults = [new { game_name = "Player", tagline = "JP2", summoner_id = "sid-2" }];
        await provider.RefreshRanksAsync(renamed, default);
        Assert.Equal(new[]
        {
            "/api/v3/jp/summoners", "/api/jp/summoners/sid-1/summary", "/api/jp/summoners/sid-1/summary",
            "/api/v3/jp/summoners", "/api/jp/summoners/sid-2/summary"
        }, handler.Paths);
    }

    [Fact]
    public async Task StaleCachedSummonerIdIsSearchedAgainOnce()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        fixture.Store.Write("opgg", account.Id.ToString(), new OpggIdentity("jp", "Player", "JP1", "sid-old"));
        var handler = new FakeOpgg(); handler.UnknownSummoners.Add("sid-old");
        using var api = Api(handler);
        await new OpggRankProvider(fixture.Store, api).RefreshRanksAsync(account, default);
        Assert.Equal(new[] { "/api/jp/summoners/sid-old/summary", "/api/v3/jp/summoners", "/api/jp/summoners/sid-1/summary" }, handler.Paths);
        Assert.Equal("sid-1", OpggRankProvider.Identity(fixture.Store, account.Id)?.SummonerId);
        Assert.NotNull(fixture.Store.LatestRanks(account.Id));
    }

    [Fact]
    public async Task NotFoundRightAfterASearchIsNotRetried()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var handler = new FakeOpgg(); handler.UnknownSummoners.Add("sid-1");
        using var api = Api(handler);
        var error = await Assert.ThrowsAsync<OpggException>(() => new OpggRankProvider(fixture.Store, api).RefreshRanksAsync(account, default));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(new[] { "/api/v3/jp/summoners", "/api/jp/summoners/sid-1/summary" }, handler.Paths);
    }

    [Fact]
    public async Task StaleIdThatIsStillUnknownAfterTheSearchFailsWithoutLooping()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        fixture.Store.Write("opgg", account.Id.ToString(), new OpggIdentity("jp", "Player", "JP1", "sid-1"));
        var handler = new FakeOpgg(); handler.UnknownSummoners.Add("sid-1");
        using var api = Api(handler);
        var error = await Assert.ThrowsAsync<OpggException>(() => new OpggRankProvider(fixture.Store, api).RefreshRanksAsync(account, default));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(new[] { "/api/jp/summoners/sid-1/summary", "/api/v3/jp/summoners", "/api/jp/summoners/sid-1/summary" }, handler.Paths);
        Assert.Null(fixture.Store.LatestRanks(account.Id));
    }

    [Fact]
    public async Task StaleIdentityIsForgottenWhenOpggNoLongerFindsTheAccount()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        fixture.Store.Write("opgg", account.Id.ToString(), new OpggIdentity("jp", "Player", "JP1", "sid-old"));
        var handler = new FakeOpgg { SearchResults = [] }; handler.UnknownSummoners.Add("sid-old");
        using var api = Api(handler);
        var error = await Assert.ThrowsAsync<OpggException>(() => new OpggRankProvider(fixture.Store, api).RefreshRanksAsync(account, default));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Contains("見つかりません", error.Message);
        Assert.Null(OpggRankProvider.Identity(fixture.Store, account.Id));
    }

    [Fact]
    public async Task FuzzySearchResultIsNotAcceptedAsTheAccount()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var handler = new FakeOpgg { SearchResults = [new { game_name = "Player2", tagline = "JP1", summoner_id = "other" }] };
        using var api = Api(handler);
        var error = await Assert.ThrowsAsync<OpggException>(() => new OpggRankProvider(fixture.Store, api).RefreshRanksAsync(account, default));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Null(OpggRankProvider.Identity(fixture.Store, account.Id));
        Assert.Null(fixture.Store.LatestRanks(account.Id));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RateLimitIsRetriedAndUnexpectedShapeIsReported()
    {
        using var fixture = new StoreFixture();
        var account = Account(); fixture.Store.Save(account);
        var limited = 0;
        var handler = new FakeOpgg
        {
            Intercept = r =>
            {
                if (!r.RequestUri!.AbsolutePath.EndsWith("/summary", StringComparison.Ordinal)) return null;
                if (limited++ == 0)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
                    return response;
                }
                return Json(new { data = new { unexpected = true } });
            }
        };
        using var api = Api(handler);
        var messages = new List<string>();
        var error = await Assert.ThrowsAsync<OpggException>(() =>
            new OpggRankProvider(fixture.Store, api).RefreshRanksAsync(account, default, new InlineProgress(messages.Add)));
        Assert.Contains("応答形式", error.Message);
        Assert.Equal(2, limited);
        Assert.Contains(messages, m => m.Contains("取得制限", StringComparison.Ordinal));
        Assert.Null(fixture.Store.LatestRanks(account.Id));
    }

    [Fact]
    public async Task LongRateLimitIsNotWaitedOut()
    {
        var handler = new FakeOpgg
        {
            Intercept = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new(TimeSpan.FromMinutes(5));
                return response;
            }
        };
        using var api = Api(handler);
        var error = await Assert.ThrowsAsync<OpggException>(() => api.GetAsync(OpggApi.SummonerHost, "/api/v3/jp/summoners?riot_id=x", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ServerErrorsAndBadPathsFailClosed()
    {
        var handler = new FakeOpgg { Intercept = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) };
        using var api = Api(handler);
        var error = await Assert.ThrowsAsync<OpggException>(() => api.GetAsync(OpggApi.SummonerHost, "/api/meta/seasons", default));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain("Riot API", error.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => api.GetAsync(OpggApi.SummonerHost, "/api/../x", default));
        await Assert.ThrowsAsync<ArgumentException>(() => api.GetAsync(OpggApi.SummonerHost, "/other/x", default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RequestsAreLoggedWithoutRiotIdsOrOpggIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "riot-lite-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticLog(Path.Combine(directory, "diagnostics.log"));
            using var fixture = new StoreFixture();
            var account = Account(); fixture.Store.Save(account);
            using var api = Api(new FakeOpgg(), log);
            await new OpggRankProvider(fixture.Store, api).RefreshRanksAsync(account, default);
            var text = File.ReadAllText(log.FilePath);
            Assert.Contains("lol-api-summoner.op.gg /api/v3/jp/summoners -> HTTP 200", text);
            Assert.Contains("lol-api-summoner.op.gg /api/jp/summoners/{id}/summary -> HTTP 200", text);
            Assert.DoesNotContain("Player", text);
            Assert.DoesNotContain("sid-1", text);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
