using CrusaderWars.StatFidelity;
using Xunit;

public class StatExportTests
{
    private static string Regiment(string id = "101", string damage = "40", string toughness = "30", string baseDamage = "20") =>
        $"CCSTAT|v1|REGIMENT|attacker|5|{id}|42|bowmen|21|{damage}|{toughness}|0|10|{baseDamage}|10";
    private static string Frame(params string[] rows) => "[engine] CCSTAT|v1|BEGIN|battle42\n" + string.Join("\n", rows) + "\n[engine] CCSTAT|v1|END|battle42\n";

    [Fact]
    public void SameTypeRegimentsKeepIndependentStatsAndIdentity()
    {
        var result = StatExportParser.Parse(Frame(Regiment(), Regiment("102", "100", "10")));
        Assert.True(result.Complete);
        Assert.Equal(2, result.Regiments.Count);
        Assert.NotEqual(result.Regiments[0].Identity, result.Regiments[1].Identity);
        Assert.Equal((2d, 3d), StatScaling.Regiment(result.Regiments[0], 1));
        Assert.Equal((5d, 1d), StatScaling.Regiment(result.Regiments[1], 1));
    }

    [Fact]
    public void TruncatedLatestFrameCannotReusePreviousBattle()
    {
        var result = StatExportParser.Parse(Frame(Regiment()) + "CCSTAT|v1|BEGIN|battle43\n" + Regiment());
        Assert.False(result.Complete);
        Assert.Empty(result.Regiments);
        Assert.Equal("battle43", result.BattleId);
    }

    [Fact]
    public void MismatchedEndRejectsWholeFrame()
    {
        var result = StatExportParser.Parse(Frame(Regiment()).Replace("END|battle42", "END|battle43"));
        Assert.False(result.Complete);
        Assert.Empty(result.Regiments);
    }

    [Fact]
    public void UnsupportedVersionCannotReuseEarlierExport()
    {
        var result = StatExportParser.Parse(Frame(Regiment()) + Frame(Regiment()).Replace("v1", "v2"));
        Assert.False(result.Complete);
        Assert.Empty(result.Regiments);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,000")]
    [InlineData("1,5")]
    [InlineData("unknown")]
    [InlineData("-10")]
    [InlineData("1000001")]
    public void InvalidNumericEvidenceNeverProducesProfile(string damage)
    {
        var result = StatExportParser.Parse(Frame(Regiment(damage: damage)));
        Assert.True(result.Complete);
        Assert.Empty(result.Regiments);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void ZeroBaselineNeverDividesOrInventsStats()
    {
        var result = StatExportParser.Parse(Frame(Regiment(baseDamage: "0")));
        var stats = Assert.Single(result.Regiments); // Legal for siege progress units, never a combat ratio.
        Assert.Throws<InvalidOperationException>(() => StatScaling.Regiment(stats, 1));
    }

    [Fact]
    public void DuplicateIdentityInvalidatesAllConflictingRows()
    {
        var result = StatExportParser.Parse(Frame(Regiment(), Regiment(damage: "60"), Regiment(damage: "80")));
        Assert.Empty(result.Regiments);
        Assert.Contains(result.Diagnostics, m => m.Contains("Duplicate regiment"));
    }

    [Fact]
    public void OverlongRecordRejectsFrame()
    {
        var result = StatExportParser.Parse(Frame(Regiment(), "CCSTAT|v1|UNKNOWN|" + new string('x', StatExportParser.MaxRecordCharacters)));
        Assert.False(result.Complete);
        Assert.Empty(result.Regiments);
    }

    [Fact]
    public void ActiveAdvantageCoefficientAndRelativeDifferenceAreUsedOnce()
    {
        var result = StatExportParser.Parse(Frame("CCSTAT|v1|COMMANDER|attacker|10|40|0.05", "CCSTAT|v1|COMMANDER|defender|20|16|0.05"));
        Assert.True(StatScaling.TryCommanderMultiplier(result, "attacker", out double attack));
        Assert.True(StatScaling.TryCommanderMultiplier(result, "defender", out double defend));
        Assert.Equal(2.2, attack, 10);
        Assert.Equal(1, defend);
    }

    [Fact]
    public void MissingOpponentOrConflictingRuleCannotScaleCommander()
    {
        var oneSide = StatExportParser.Parse(Frame("CCSTAT|v1|COMMANDER|attacker|10|40|0.05"));
        Assert.False(StatScaling.TryCommanderMultiplier(oneSide, "attacker", out _));
        var differentRules = StatExportParser.Parse(Frame("CCSTAT|v1|COMMANDER|attacker|10|40|0.05", "CCSTAT|v1|COMMANDER|defender|20|16|0.02"));
        Assert.False(StatScaling.TryCommanderMultiplier(differentRules, "attacker", out _));
    }

    [Fact]
    public void KnightTotalEffectivenessAndCurrentDamageDefineAreRetained()
    {
        var result = StatExportParser.Parse(Frame("CCSTAT|v1|KNIGHT|attacker|88|20|2.5|50|10"));
        var knight = Assert.Single(result.Knights).Value;
        Assert.Equal(2500, knight.Prowess * knight.Effectiveness * knight.DamagePerProwess);
        Assert.Equal(500, knight.Prowess * knight.Effectiveness * knight.ToughnessPerProwess);
    }

    [Fact]
    public void ConfiguredBoundsRejectRatherThanClamp()
    {
        Assert.Equal(40, StatScaling.RequireMultiplier(40, 0.01, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => StatScaling.RequireMultiplier(40, 0.1, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => StatScaling.RequireMultiplier(0, 0.1, 20));
    }

    [Fact]
    public void SourceCasualtiesAggregateSplitUnitsWithoutSharingTypeLosses()
    {
        Assert.Equal(125, StatScaling.SourceSurvivors(250, 100 + 150, 50 + 75));
        Assert.Equal(250, StatScaling.SourceSurvivors(250, 250, 250));
        Assert.Equal(0, StatScaling.SourceSurvivors(250, 250, 0));
        Assert.Equal(250, StatScaling.SourceSurvivors(250, 250, 251));
        Assert.Throws<ArgumentOutOfRangeException>(() => StatScaling.SourceSurvivors(250, 0, 0));
    }
}
