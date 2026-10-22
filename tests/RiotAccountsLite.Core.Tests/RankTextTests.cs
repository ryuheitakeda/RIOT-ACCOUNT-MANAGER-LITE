using RiotAccountsLite.Core;
using Xunit;

public sealed class RankTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static RankEntry Entry(string queue, string tier, int lp) => new(queue, tier, "II", lp, 10, 8);

    [Fact]
    public void NothingSavedShowsNotFetched()
    {
        Assert.Equal("ランク未取得", RankText.Summary(null));
        Assert.Equal(("未取得", ""), RankText.Queue(null, Queues.Solo));
    }

    [Fact]
    public void SummaryPrefersSoloThenFlexThenUnranked()
    {
        Assert.Equal("Solo GOLD II  45 LP", RankText.Summary(new(Now, [Entry(Queues.Flex, "PLATINUM", 5), Entry(Queues.Solo, "GOLD", 45)])));
        Assert.Equal("Flex PLATINUM II  5 LP", RankText.Summary(new(Now, [Entry(Queues.Flex, "PLATINUM", 5)])));
        Assert.Equal("UNRANKED", RankText.Summary(new(Now, [])));
    }

    [Fact]
    public void QueueShowsRankAndRecordOrUnranked()
    {
        var ranks = new RankSnapshot(Now, [Entry(Queues.Solo, "GOLD", 45)]);
        Assert.Equal(("GOLD II  45 LP", $"10勝 8敗  /  {55.6:F1}%"), RankText.Queue(ranks, Queues.Solo));
        Assert.Equal(("UNRANKED", "このキューのランク情報はありません。"), RankText.Queue(ranks, Queues.Flex));
    }

    [Theory]
    [InlineData("MASTER")]
    [InlineData("GRANDMASTER")]
    [InlineData("CHALLENGER")]
    public void ApexTiersHaveNoDivision(string tier) =>
        Assert.Equal($"{tier}  321 LP", new RankEntry(Queues.Solo, tier, "I", 321, 1, 1).Display);

    [Fact]
    public void RecordWithoutGamesIsZeroPercent() =>
        Assert.Equal($"0勝 0敗  /  {0.0:F1}%", new RankEntry(Queues.Solo, "GOLD", "IV", 0, 0, 0).Record);
}
