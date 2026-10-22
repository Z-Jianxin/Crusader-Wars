using CrusaderWars.StatFidelity;
using Xunit;

public class BattleReportEvidenceTests
{
    private const string Levy = "0_attacker_army24_TYPELevy_135_CULTURE73_0";
    private const string Generic = "0_defender_army30_TYPEcommandercc_untracked_30_127_CULTURE_0";
    private static DeployedFormation[] Roster() => [new(Levy, 500), new(Generic, 2)];
    private static string Block(int levy = 500, int general = 2) => $"{Levy}-{levy}\r\n{Generic}-{general}\r\n";
    private static string Kills(int levy = 0, int general = 0) => $"kills_{Levy}-{levy}\nkills_{Generic}-{general}\n";

    [Fact]
    public void BindsAllPhasesExactlyIncludingEmptyCultureAndReturnsImmutableSnapshots()
    {
        var roster = Roster();
        var blocks = new[] { "\r\n" + Block(450), Block(0, 1) };
        var result = BattleReportEvidence.Validate(roster, blocks, [Kills(7), Kills(int.MaxValue, 100)]);
        Assert.Equal(450, result.SurvivorPhases[0][Levy]);
        Assert.Equal(0, result.FinalSurvivors[Levy]);
        Assert.Equal(1, result.FinalSurvivors[Generic]);
        Assert.Equal(int.MaxValue, result.FinalKills![Levy]);
        roster[0] = new(Levy, 1); blocks[1] = Block();
        Assert.Equal(0, result.FinalSurvivors[Levy]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, int>)result.FinalSurvivors)[Levy] = 123);
        Assert.Throws<NotSupportedException>(() => ((IList<IReadOnlyDictionary<string, int>>)result.SurvivorPhases).Clear());
    }

    [Fact]
    public void KillEvidenceMayBeAbsentAndNamesAreNotSplitIntoSemanticTokens()
    {
        const string unusual = "unit-with-hyphens_CULTURE_0";
        var result = BattleReportEvidence.Validate([new(unusual, 0)], [$"{unusual}-0"]);
        Assert.Equal(0, result.FinalSurvivors[unusual]);
        Assert.Empty(result.KillPhases);
        Assert.Null(result.FinalKills);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("case")]
    [InlineData("culture-alias")]
    [InlineData("header")]
    public void IncompleteAmbiguousOrUnboundSurvivorPhasesFail(string fault)
    {
        string block = fault switch {
            "missing" => $"{Levy}-500\n",
            "duplicate" => Block() + $"{Levy}-500\n",
            "unknown" => Block() + "other-0\n",
            "case" => Block().Replace("TYPELevy", "typelevy"),
            "culture-alias" => Block().Replace("_CULTURE_0", "_CULTURE231_0"),
            "header" => "-----REMAINING SOLDIERS-----!!\n" + Block(),
            _ => fault
        };
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [block]));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("1e2")]
    [InlineData("2147483648")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("")]
    public void MalformedCountsFailForBothSurvivorsAndKills(string count)
    {
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [$"{Levy}-{count}\n{Generic}-2"]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [Block()], [$"kills_{Levy}-{count}\nkills_{Generic}-0"]));
    }

    [Fact]
    public void SurvivorsCannotExceedDeploymentOrIncreaseBetweenAnyPhases()
    {
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [Block(501)]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [Block(400), Block(401)]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [Block(400), Block(350), Block(360)]));
        Assert.Equal(350, BattleReportEvidence.Validate(Roster(), [Block(400), Block(350), Block(350)]).FinalSurvivors[Levy]);
    }

    [Fact]
    public void SuppliedKillsMustCoverEveryFormationAndPhaseWithoutInventedCasualtyCap()
    {
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [Block()], []));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [Block(), Block()], [Kills()]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [Block()], [$"kills_{Levy}-0"]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [Block()], [Kills() + $"kills_{Levy}-1\n"]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [Block()], [Block()]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [Block()], [Kills() + "kills_other-1\n"]));
        Assert.Equal(100000, BattleReportEvidence.Validate(Roster(), [Block()], [Kills(100000)]).FinalKills![Levy]);
    }

    [Fact]
    public void InvalidRosterAndEmptyPhaseCollectionsFail()
    {
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate([], [Block()]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate([new(Levy, -1)], [$"{Levy}-0"]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate([new(Levy, 1), new(Levy, 2)], [$"{Levy}-0"]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate([new("broken\nname", 1)], ["broken\nname-0"]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), []));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [null!]));
    }

    [Fact]
    public void BoundedInputsRejectOversizedRostersNamesPhasesAndBlocks()
    {
        var excessiveRoster = Enumerable.Range(0, BattleReportEvidence.MaximumFormations + 1).Select(i => new DeployedFormation("unit" + i, 1)).ToArray();
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(excessiveRoster, [Block()]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate([new(new string('x', BattleReportEvidence.MaximumScriptNameLength + 1), 1)], ["x-1"]));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), Enumerable.Repeat(Block(), BattleReportEvidence.MaximumPhases + 1).ToArray()));
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), [new string(' ', BattleReportEvidence.MaximumBlockCharacters + 1)]));
        string maxBlock = new(' ', BattleReportEvidence.MaximumBlockCharacters);
        Assert.Throws<InvalidDataException>(() => BattleReportEvidence.Validate(Roster(), Enumerable.Repeat(maxBlock, 5).ToArray()));
    }
}
