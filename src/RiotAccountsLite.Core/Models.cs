using System.Text.Json.Serialization;

namespace RiotAccountsLite.Core;

// Note is stored in plain text like the label; it must never hold credentials.
public sealed record RiotAccount(Guid Id, string Label, string GameName, string TagLine, string Platform, string? Note = null)
{
    [JsonIgnore] public string RiotId => $"{GameName}#{TagLine}";
}
public sealed record Credentials(string Username, string Password);
public sealed record RankEntry(string QueueType, string Tier, string Rank, int LeaguePoints, int Wins, int Losses)
{
    // Master and above have no division, although OP.GG reports division 1 for them.
    [JsonIgnore] public string Display => Rank.Length == 0 || Tier is "MASTER" or "GRANDMASTER" or "CHALLENGER"
        ? $"{Tier}  {LeaguePoints} LP" : $"{Tier} {Rank}  {LeaguePoints} LP";
    [JsonIgnore] public string Record => $"{Wins}勝 {Losses}敗  /  {(Wins + Losses == 0 ? 0 : 100.0 * Wins / (Wins + Losses)):F1}%";
}
// OpggUpdatedAt is when OP.GG itself last updated the profile; null when OP.GG did not say.
public sealed record RankSnapshot(DateTimeOffset ObservedAt, List<RankEntry> Entries, DateTimeOffset? OpggUpdatedAt = null);
public static class Queues
{
    public const string Solo = "RANKED_SOLO_5x5";
    public const string Flex = "RANKED_FLEX_SR";
}
public static class Regions
{
    // The servers OP.GG supports, in the order the account dialog lists them.
    public static readonly IReadOnlyList<string> Platforms = Array.AsReadOnly(new[]
    {
        "JP1", "KR", "NA1", "BR1", "LA1", "LA2", "EUW1", "EUN1", "TR1", "RU", "ME1", "OC1", "SG2", "TW2", "VN2"
    });
    public static void Validate(string platform)
    {
        if (!Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("サーバーを選択してください。");
    }
}
public static class RankText
{
    // Built from the saved observation only; showing the list never triggers a fetch.
    public static string Summary(RankSnapshot? ranks) => ranks == null ? "ランク未取得"
        : ranks.Entries.FirstOrDefault(e => e.QueueType == Queues.Solo) is { } solo ? $"Solo {solo.Display}"
        : ranks.Entries.FirstOrDefault(e => e.QueueType == Queues.Flex) is { } flex ? $"Flex {flex.Display}"
        : "UNRANKED";

    public static (string Title, string Record) Queue(RankSnapshot? ranks, string queue) => ranks == null ? ("未取得", "")
        : ranks.Entries.FirstOrDefault(e => e.QueueType == queue) is { } entry ? (entry.Display, entry.Record)
        : ("UNRANKED", "このキューのランク情報はありません。");
}
