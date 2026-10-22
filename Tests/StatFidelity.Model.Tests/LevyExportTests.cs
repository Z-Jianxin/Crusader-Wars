using CrusaderWars.StatFidelity;
using Xunit;

public sealed class LevyExportTests
{
    private static string Frame(params string[] records) => "CCSTAT|v1|BEGIN|snapshot-1\n" + string.Join("\n", records) + "\nCCSTAT|v1|END|snapshot-1\n";
    private static string Levy(string side = "defender", string owner = "42", string damage = "10", string toughness = "11.1", string baseToughness = "10") =>
        $"CCSTAT|v1|LEVY|{side}|{owner}|{damage}|{toughness}|10|{baseToughness}";

    [Fact]
    public void OptionalLevyRowsKeepEachOwnerAndSideDistinct()
    {
        var parsed = StatExportParser.Parse(Frame(Levy(), Levy(owner: "43", toughness: "5.55"), Levy(side: "attacker", toughness: "10")));
        Assert.True(parsed.Complete);
        Assert.Equal(3, parsed.Levies.Count);
        Assert.Equal(11.1, parsed.Levies["defender:42"].Toughness);
        Assert.Equal(5.55, parsed.Levies["defender:43"].Toughness);
        Assert.Equal(10, parsed.Levies["attacker:42"].Toughness);
        Assert.Empty(StatExportParser.Parse(Frame()).Levies);
    }

    [Theory]
    [InlineData("NaN", "10", "10")]
    [InlineData("10", "-1", "10")]
    [InlineData("10", "10", "0")]
    public void InvalidLevyStatsCannotProduceAnOwnerProfile(string damage, string toughness, string baseline)
    {
        var parsed = StatExportParser.Parse(Frame(Levy(damage: damage, toughness: toughness, baseToughness: baseline)));
        Assert.Empty(parsed.Levies);
        Assert.NotEmpty(parsed.Diagnostics);
    }

    [Fact]
    public void DuplicateOwnerSideDiscardsEveryConflictingRow()
    {
        var parsed = StatExportParser.Parse(Frame(Levy(), Levy(toughness: "5.55"), Levy(toughness: "10")));
        Assert.Empty(parsed.Levies);
        Assert.Contains(parsed.Diagnostics, message => message.Contains("Duplicate levy"));
    }

    [Fact]
    public void TruncatedLatestFrameCannotReuseOlderLevyEvidence()
    {
        var parsed = StatExportParser.Parse(Frame(Levy()) + "CCSTAT|v1|BEGIN|snapshot-2\n" + Levy());
        Assert.False(parsed.Complete);
        Assert.Empty(parsed.Levies);
    }
}
