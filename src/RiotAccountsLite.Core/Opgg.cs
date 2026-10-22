using System.Globalization;
using System.Net;
using System.Text.Json;

namespace RiotAccountsLite.Core;

// The error every OP.GG failure is reported as. Messages are user-facing and never contain Riot IDs or OP.GG ids.
public sealed class OpggException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

// OP.GG's web endpoints are unofficial and undocumented; they can change or be blocked without notice.
public sealed class OpggApi(HttpClient http, TimeSpan? minimumInterval = null, DiagnosticLog? log = null) : IDisposable
{
    // The only host this app ever calls; it never talks to the Riot API or any other server.
    public const string SummonerHost = "lol-api-summoner.op.gg";
    private const string UserAgent = "RiotAccountsLite (personal account manager)";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan interval = minimumInterval ?? TimeSpan.FromSeconds(1);
    private DateTimeOffset notBefore;

    public async Task<JsonDocument> GetAsync(string host, string pathAndQuery, CancellationToken ct, IProgress<string>? progress = null)
    {
        if (host != SummonerHost) throw new ArgumentException("OP.GGの接続先が不正です。", nameof(host));
        if (!pathAndQuery.StartsWith("/api/", StringComparison.Ordinal) || pathAndQuery.Contains('\\') || pathAndQuery.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("OP.GGのパスが不正です。", nameof(pathAndQuery));
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var wait = notBefore - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{host}{pathAndQuery}"));
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using var response = await Send(request, host, ApiRoute.Redact(pathAndQuery), ct);
                notBefore = DateTimeOffset.UtcNow + interval;
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                    if (retry < TimeSpan.FromSeconds(1)) retry = TimeSpan.FromSeconds(1);
                    if (retry > TimeSpan.FromMinutes(2)) break;
                    notBefore = DateTimeOffset.UtcNow + retry;
                    progress?.Report($"OP.GGの取得制限です。{Math.Ceiling(retry.TotalSeconds)}秒後に再試行します。");
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new OpggException("OP.GGに対象データが見つかりません。Riot ID・タグ・サーバーを確認してください。", response.StatusCode);
                if (!response.IsSuccessStatusCode)
                    throw new OpggException($"OP.GGから取得できませんでした（HTTP {(int)response.StatusCode}）。OP.GG側の仕様変更や一時的な障害の可能性があります。時間をおいて再試行してください。", response.StatusCode);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            throw new OpggException("OP.GGの取得制限が続いています。時間をおいて更新してください。", HttpStatusCode.TooManyRequests);
        }
        catch (JsonException) { throw new OpggException("OP.GGの応答を読み取れませんでした。保存済みのランクは保持しています。"); }
        finally { gate.Release(); }
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, string host, string route, CancellationToken ct)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await http.SendAsync(request, ct);
            log?.Write("OP.GG", $"{host} {route} -> HTTP {(int)response.StatusCode} ({timer.ElapsedMilliseconds}ms)");
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            log?.Write("OP.GG", $"{host} {route} -> {(ct.IsCancellationRequested ? "中止" : "通信失敗 " + ex.GetType().Name)} ({timer.ElapsedMilliseconds}ms)");
            throw;
        }
    }

    public void Dispose() => gate.Dispose();
}

public static class OpggRegions
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["JP1"]="jp", ["KR"]="kr", ["NA1"]="na", ["BR1"]="br", ["LA1"]="lan", ["LA2"]="las", ["EUW1"]="euw", ["EUN1"]="eune",
        ["TR1"]="tr", ["RU"]="ru", ["ME1"]="me", ["OC1"]="oce", ["SG2"]="sg", ["TW2"]="tw", ["VN2"]="vn"
    };
    public static string Region(string platform) => Map.TryGetValue(platform, out var value) ? value : throw new ArgumentException("OP.GGが対応していないサーバーです。");
    public static Uri ProfileUrl(RiotAccount account) =>
        new($"https://op.gg/lol/summoners/{Region(account.Platform)}/{Uri.EscapeDataString(account.GameName)}-{Uri.EscapeDataString(account.TagLine)}");
}

/// <summary>The account as OP.GG knows it, cached so later refreshes skip the search.</summary>
public sealed record OpggIdentity(string Region, string GameName, string TagLine, string SummonerId);

public sealed class OpggRankProvider(Store store, OpggApi api) : IRankProvider
{
    public static OpggIdentity? Identity(Store store, Guid accountId) => store.Read<OpggIdentity>("opgg", accountId.ToString());

    // Searches only when nothing is cached for this Riot ID and server; Searched tells the caller the summoner id is fresh.
    private async Task<(OpggIdentity Identity, bool Searched)> Resolve(RiotAccount account, CancellationToken ct, IProgress<string>? progress)
    {
        ct.ThrowIfCancellationRequested();
        var region = OpggRegions.Region(account.Platform);
        var identity = Identity(store, account.Id);
        if (identity is not null && identity.Region == region &&
            string.Equals(identity.GameName, account.GameName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(identity.TagLine, account.TagLine, StringComparison.OrdinalIgnoreCase))
            return (identity, false);
        var riotId = Uri.EscapeDataString($"{account.GameName}#{account.TagLine}");
        using var json = await api.GetAsync(OpggApi.SummonerHost, $"/api/v3/{region}/summoners?riot_id={riotId}&hl=ja_JP", ct, progress);
        JsonElement match;
        try
        {
            // The search is fuzzy, so only an exact Riot ID match identifies the account.
            match = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(s =>
                string.Equals(Str(s, "game_name"), account.GameName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Str(s, "tagline"), account.TagLine, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (IsShapeError(error)) { throw ShapeError(); }
        if (match.ValueKind != JsonValueKind.Object || Str(match, "summoner_id").Length == 0)
            throw new OpggException("OP.GGでアカウントが見つかりません。Riot ID・タグ・サーバーを確認してください。", HttpStatusCode.NotFound);
        identity = new(region, account.GameName, account.TagLine, Str(match, "summoner_id"));
        ct.ThrowIfCancellationRequested();
        store.Write("opgg", account.Id.ToString(), identity);
        return (identity, true);
    }

    public async Task<RankSnapshot> RefreshRanksAsync(RiotAccount account, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        var (identity, searched) = await Resolve(account, cancellationToken, progress);
        JsonDocument json;
        try { json = await Summary(identity, cancellationToken, progress); }
        catch (OpggException error) when (error.StatusCode == HttpStatusCode.NotFound && !searched)
        {
            // OP.GG no longer knows the cached summoner id: forget it (even if the search fails) and search once more.
            store.Remove("opgg", account.Id.ToString());
            (identity, _) = await Resolve(account, cancellationToken, progress);
            json = await Summary(identity, cancellationToken, progress);
        }
        RankSnapshot snapshot;
        using (json)
        {
            try { snapshot = new(DateTimeOffset.UtcNow, ParseRanks(json.RootElement), ParseUpdatedAt(json.RootElement)); }
            catch (Exception error) when (IsShapeError(error)) { throw ShapeError(); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        store.SaveRanks(account.Id, snapshot);
        return snapshot;
    }

    private Task<JsonDocument> Summary(OpggIdentity identity, CancellationToken ct, IProgress<string>? progress) =>
        api.GetAsync(OpggApi.SummonerHost, $"/api/{identity.Region}/summoners/{Uri.EscapeDataString(identity.SummonerId)}/summary?hl=ja_JP", ct, progress);

    public static List<RankEntry> ParseRanks(JsonElement root)
    {
        var ranks = new List<RankEntry>();
        foreach (var stat in root.GetProperty("data").GetProperty("summoner").GetProperty("league_stats").EnumerateArray())
        {
            var queue = Str(stat, "game_type") switch { "SOLORANKED" => Queues.Solo, "FLEXRANKED" => Queues.Flex, _ => null };
            if (queue is null || !stat.TryGetProperty("tier_info", out var tier) || Str(tier, "tier").Length == 0) continue;
            var division = Num(tier, "division") switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", _ => "" };
            ranks.Add(new(queue, Str(tier, "tier").ToUpperInvariant(), division, Num(tier, "lp"), Num(stat, "win"), Num(stat, "lose")));
        }
        return ranks.DistinctBy(r => r.QueueType).ToList();
    }

    // When OP.GG last updated the profile (this app never asks it to); null when missing or unreadable, never an error.
    public static DateTimeOffset? ParseUpdatedAt(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty("summoner", out var summoner) && summoner.ValueKind == JsonValueKind.Object &&
        DateTimeOffset.TryParse(Str(summoner, "updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    private static bool IsShapeError(Exception error) =>
        error is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException;
    private static OpggException ShapeError() => new("OP.GGの応答形式が想定と異なります。OP.GG側の仕様変更の可能性があります。保存済みのランクは保持しています。");
    private static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Num(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
