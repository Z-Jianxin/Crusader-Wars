using System.Reflection;
using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.save_file;
using CrusaderWars.data.battle_results;
using CrusaderWars.StatFidelity;
using CrusaderWars.twbattle;
using Xunit;

public sealed class NativeCombatParticipationTests : IDisposable
{
    private readonly string previous = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-combat-participation-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private readonly string? previousMode;
    private readonly bool previousSiege = BattleState.IsSiegeBattle;
    private readonly FieldInfo snapshotField = typeof(ArmiesReader).GetField("nativeCombatParticipation", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? previousSnapshot;

    public NativeCombatParticipationTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data", "save_file_data", "temp")); Environment.CurrentDirectory = scratch;
        options.TryGetValue("StatFidelityMode", out previousMode); options["StatFidelityMode"] = "Enabled";
        previousSnapshot = snapshotField.GetValue(null); BattleState.IsSiegeBattle = false;
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|none|0|0.05\nCCSTAT|v1|END|snapshot-1");
        StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={data={ {flag=ccstat_snapshot_serial data={type=value identity=100000}} {flag=ccstat_combat data={type=com}} }}\n}\n}"), "42", "0");
        ArmiesReader.ResetSiegeParticipationReport();
    }

    [Fact]
    public void ExactZeroCombatCurrentPreservesLiveSourceBudgetAsReserve()
    {
        var (army, unit, source) = Source(20);
        SetSnapshot("starting=20 current=0 soft_casualties=0");
        StatFidelitySession.RegisterSiegeSource(army, unit);
        Assert.Equal(0, ArmiesReader.GetFieldSiegeActiveMachines(army, unit));
        var report = Assert.Single(ArmiesReader.FieldSiegeParticipation);
        Assert.Equal(20, report.NativeStock); Assert.Equal(0, report.ActiveMachines); Assert.Equal(20, report.ReservedMachines);
        Assert.Equal(20, Assert.Single(StatFidelitySession.SiegeSources).Value.OriginalMachines);
        Assert.Equal(20, SiegeCasualtyAccounting.Survivors(20, Array.Empty<SiegeCrewResult>()));
        Assert.Equal("20", source.CurrentNum); Assert.Equal(20, army.ArmyRegiments[0].CurrentNum);
    }

    [Fact]
    public void PartialEquipmentUsesBoundedStableAllocationAcrossExactChunks()
    {
        var (army, unit, _) = Source(20);
        var other = new Regiment("101", "0"); other.SetSoldiers("10"); other.SetMax("10"); other.SetOwner("42");
        army.ArmyRegiments[0].Regiments.Add(other); army.ArmyRegiments[0].CurrentNum = 30;
        SetSnapshot("starting=30 current=15 soft_casualties=0");
        Assert.Equal(10, ArmiesReader.GetFieldSiegeActiveMachines(army, unit));
        var otherUnit = new Unit("onager", 10, null, RegimentType.MenAtArms);
        otherUnit.SetSourceIdentity("1", "10", "101", "0", "42");
        Assert.Equal(5, ArmiesReader.GetFieldSiegeActiveMachines(army, otherUnit));
        Assert.Equal(15, ArmiesReader.FieldSiegeParticipation.Sum(r => r.ActiveMachines));
        Assert.Equal(15, ArmiesReader.FieldSiegeParticipation.Sum(r => r.ReservedMachines));
        Assert.Equal(new[] { 4, 3 }, NativeCombatParticipation.Parse(Combat("starting=10 current=7"))
            .AllocateEquipment("attacker", "1", "10", new[] {5, 5}));
    }

    [Theory]
    [InlineData("starting=20")]
    [InlineData("starting=20 current=0.5")]
    [InlineData("starting=30 current=21")]
    public void MissingFractionalOrExcessiveSiegeParticipationCannotInventStrength(string fields)
    {
        var (army, unit, source) = Source(20); SetSnapshot(fields);
        Assert.Throws<InvalidDataException>(() => ArmiesReader.GetFieldSiegeActiveMachines(army, unit));
        Assert.Equal("20", source.CurrentNum); Assert.Empty(ArmiesReader.FieldSiegeParticipation);
    }

    [Fact]
    public void MissingWrongSideOrWrongArmyIdentityCannotBorrowAnotherRecord()
    {
        var snapshot = NativeCombatParticipation.Parse(Combat("starting=20 current=0"));
        Assert.Throws<InvalidDataException>(() => snapshot.Require("attacker", "1", "11"));
        Assert.Throws<InvalidDataException>(() => snapshot.Require("defender", "2", "10"));
        Assert.Throws<InvalidDataException>(() => snapshot.Require("attacker", "9", "10"));
    }

    [Fact]
    public void OrdinaryTroopFractionIsReadWithoutApplyingWholeEquipmentRestrictions()
    {
        var snapshot = NativeCombatParticipation.Parse(Combat("starting=100 current=99.68119 soft_casualties=0.18013"));
        var record = snapshot.Require("attacker", "1", "10");
        Assert.Equal(99.68119m, record.Current); Assert.Equal(0.18013m, record.SoftCasualties);
    }

    [Fact]
    public void CachedSourceOrOwnerMismatchIsNotSilentlyReconciled()
    {
        var (army, unit, source) = Source(20); SetSnapshot("starting=20 current=0");
        army.ArmyRegiments[0].CurrentNum = 19;
        Assert.Throws<InvalidDataException>(() => ArmiesReader.GetFieldSiegeActiveMachines(army, unit));
        army.ArmyRegiments[0].CurrentNum = 20; source.SetOwner("99");
        Assert.Throws<InvalidDataException>(() => ArmiesReader.GetFieldSiegeActiveMachines(army, unit));
        Assert.Empty(ArmiesReader.FieldSiegeParticipation);
    }

    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(10, 14, 4)]
    public void CombatWritebackExcludesReservedStockAndPreservesHistoricalCounts(int active, int returnedStock, int returnedActive)
    {
        var (army, unit, source) = Source(20); SetSnapshot("starting=20 current=" + active);
        Assert.Equal(active, ArmiesReader.GetFieldSiegeActiveMachines(army, unit));
        army.ArmyRegiments[0].CurrentNum = returnedStock; source.SetSoldiers(returnedStock.ToString());
        string original = "\t\t0={\n\t\t\tphase=main\n\t\t\tdays=1\n\t\t\tbase_combat_width=50\n" +
            "\t\t\tattacker={\n\t\t\t\tarmies={1}\n\t\t\t\tinitial_men=20\n\t\t\t\ttotal_fighting_men=" + active + "\n\t\t\t\ttotal_levy_men=0\n" +
            "\t\t\t\tmen_at_arms={ { regiment=10 starting=20 current=" + active + " soft_casualties=0 } }\n\t\t\t}\n" +
            "\t\t\tdefender={\n\t\t\t\tarmies={2}\n\t\t\t\tinitial_men=1\n\t\t\t\ttotal_fighting_men=1\n\t\t\t\ttotal_levy_men=1\n\t\t\t}\n\t\t}\n";
        BattleResult.Player_Combat = original; BattleResult.Original_Player_Combat = original; BattleResult.IsAttackerVictorious = true;
        File.WriteAllText(Writter.DataFilesPaths.Combats_Path(), "combats={\n" + original + "}\n");
        BattleResult.EditCombatFile(new() {army}, new());
        string result = File.ReadAllText(Writter.DataTEMPFilesPaths.Combats_Path());
        Assert.Contains("regiment=10 starting=20 current=" + returnedActive + " soft_casualties=0", result);
        Assert.Contains("initial_men=20\n\t\t\t\ttotal_fighting_men=" + returnedActive, result);
        Assert.Equal(returnedStock.ToString(), source.CurrentNum); Assert.Equal(returnedStock, army.ArmyRegiments[0].CurrentNum);
        Assert.Equal(20, army.ArmyRegiments[0].StartingNum);
    }

    [Theory]
    [InlineData("current=-1")]
    [InlineData("current=NaN")]
    [InlineData("current=0 current=1")]
    [InlineData("current={ value=0 }")]
    public void MalformedOrDuplicateCurrentFailsClosed(string fields) =>
        Assert.Throws<InvalidDataException>(() => NativeCombatParticipation.Parse(Combat(fields)));

    [Fact]
    public void BoundedParserIgnoresQuotedBracesAndRejectsDuplicateOrTruncatedRecords()
    {
        var withText = Combat("starting=20 current=0 note=\"brace } and \\\"quote\\\" {\"");
        Assert.Equal(0m, NativeCombatParticipation.Parse(withText).Require("attacker", "1", "10").Current);
        Assert.Throws<InvalidDataException>(() => NativeCombatParticipation.Parse(Combat("current=0") + " attacker={ armies={1} }"));
        Assert.Throws<InvalidDataException>(() => NativeCombatParticipation.Parse(Combat("current=0").Replace("regiment=10 current=0", "regiment=10 current=0 } { regiment=10 current=0")));
        Assert.Throws<InvalidDataException>(() => NativeCombatParticipation.Parse(Combat("current=0")[..^1]));
        Assert.Throws<InvalidDataException>(() => NativeCombatParticipation.Parse(new string(' ', 16 * 1024 * 1024 + 1)));
    }

    private void SetSnapshot(string fields) => snapshotField.SetValue(null, NativeCombatParticipation.Parse(Combat(fields)));
    private static string Combat(string fields) => "0={ attacker={ armies={1} men_at_arms={ { regiment=10 " + fields + " } } } defender={ armies={2} } }";
    private static (Army, Unit, Regiment) Source(int current)
    {
        var army = new Army("1", "attacker", true);
        var source = new Regiment("100", "0"); source.SetSoldiers(current.ToString()); source.SetMax(current.ToString()); source.SetOwner("42");
        var regiment = new ArmyRegiment("10") {CurrentNum = current, StartingNum = current}; regiment.SetType(RegimentType.MenAtArms, "onager"); regiment.SetRegiments(new() {source});
        army.SetArmyRegiments(new() {regiment});
        var unit = new Unit("onager", current, null, RegimentType.MenAtArms); unit.SetIsSiege(true); unit.SetSourceIdentity("1", "10", "100", "0", "42");
        return (army, unit, source);
    }
    public void Dispose()
    {
        snapshotField.SetValue(null, previousSnapshot); ArmiesReader.ResetSiegeParticipationReport();
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin("");
        if (previousMode == null) options.Remove("StatFidelityMode"); else options["StatFidelityMode"] = previousMode;
        BattleState.IsSiegeBattle = previousSiege; Environment.CurrentDirectory = previous;
        string resolved = Path.GetFullPath(scratch);
        if (Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(resolved, true);
    }
}
