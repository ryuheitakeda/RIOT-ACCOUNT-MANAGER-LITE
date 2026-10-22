using System.Net;

namespace RiotAccountsLite.Core;

public interface IRankProvider
{
    Task<RankSnapshot> RefreshRanksAsync(RiotAccount account, CancellationToken cancellationToken, IProgress<string>? progress = null);
}

// Why a run ended early: every later account would fail the same way, so the run stops instead of hammering OP.GG.
public enum RefreshStop { None, RateLimit, Network }

// Messages are user-facing and never contain credentials or OP.GG ids.
public sealed record RefreshOutcome(Guid AccountId, string Account, bool Ok, string Message);

// Total counts the accounts at the start, minus those deleted before their turn.
public sealed record RefreshResult(int Total, RefreshStop Stop, IReadOnlyList<RefreshOutcome> Outcomes)
{
    public int Succeeded => Outcomes.Count(o => o.Ok);
    public string Summary
    {
        get
        {
            var failed = Outcomes.Where(o => !o.Ok).Select(o => o.Account).ToList();
            return Stop switch
            {
                RefreshStop.RateLimit => $"OP.GGの取得制限が続いたため中断しました（{Succeeded}/{Total}件を更新）。時間をおいて再試行してください。",
                RefreshStop.Network => $"OP.GGに接続できないため中断しました（{Succeeded}/{Total}件を更新）。接続を確認してください。",
                _ when failed.Count == 0 => $"全{Total}件のランクを更新しました。",
                _ => $"{Succeeded}/{Total}件のランクを更新しました。取得できなかったアカウント：{string.Join("、", failed)}"
            };
        }
    }
}

/// <summary>
/// Refreshes every account's rank one at a time in list order. A failing account is recorded and the run moves on;
/// an exhausted rate-limit retry or a lost connection stops the run, and cancellation propagates.
/// </summary>
public sealed class RankRefresh(Store store, IRankProvider provider)
{
    public async Task<RefreshResult> RunAsync(IProgress<string> progress, CancellationToken ct)
    {
        var accounts = store.Accounts();
        var outcomes = new List<RefreshOutcome>();
        var deleted = 0;
        for (var i = 0; i < accounts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Rate-limit waits can make a run long: pick up edits and skip an account deleted meanwhile.
            var account = store.Accounts().SingleOrDefault(a => a.Id == accounts[i].Id);
            if (account == null) { deleted++; continue; }
            var prefix = $"ランクを更新中 {i + 1}/{accounts.Count} — {account.Label}";
            progress.Report(prefix);
            var relay = new Relay(message => progress.Report($"{prefix}: {message}"));
            RefreshStop stop;
            string message;
            try
            {
                await provider.RefreshRanksAsync(account, ct, relay);
                outcomes.Add(new(account.Id, account.Label, true, "更新しました。"));
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OpggException error) { stop = error.StatusCode == HttpStatusCode.TooManyRequests ? RefreshStop.RateLimit : RefreshStop.None; message = error.Message; }
            catch (HttpRequestException) { stop = RefreshStop.Network; message = "OP.GGに接続できませんでした。"; }
            catch (TaskCanceledException) { stop = RefreshStop.Network; message = "OP.GGの応答がタイムアウトしました。"; }
            outcomes.Add(new(account.Id, account.Label, false, message));
            if (stop != RefreshStop.None) return new(accounts.Count - deleted, stop, outcomes);
        }
        return new(accounts.Count - deleted, RefreshStop.None, outcomes);
    }

    private sealed class Relay(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
