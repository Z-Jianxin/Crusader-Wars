using CrusaderWars;
using CrusaderWars.data.save_file;
using CrusaderWars.data.battle_results;
using CrusaderWars.client;
using CrusaderWars.StatFidelity;
using System.Reflection;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class SourceIdentityTests : IDisposable
{
    private readonly string originalDirectory = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-stat-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly Culture Culture = new Culture("7", "test", "test");

    public SourceIdentityTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data"));
        Environment.CurrentDirectory = scratch;
    }

    [Fact]
    public void DistinctSameTypeSourcesDoNotMergeButLegacyUnitsStillDo()
    {
        var a = Unit("100", "a", 100);
        var b = Unit("101", "b", 100);
        Assert.Equal(2, Merge(new List<Unit> { a, b }).Count);
        Assert.Single(Merge(new List<Unit> {
            new Unit("bowmen", 100, Culture, RegimentType.MenAtArms),
            new Unit("bowmen", 100, Culture, RegimentType.MenAtArms)
        }));
    }

    [Fact]
    public void SplittingCopiesSaveIdentityAndOriginalMapping()
    {
        var original = Unit("100", "a", 250);
        original.SetOriginalAttilaKey("base_bowmen");
        var split = new Unit("bowmen", 125, Culture, RegimentType.MenAtArms);
        split.CopySourceIdentity(original);
        Assert.Equal(original.SourceIdentity, split.SourceIdentity);
        Assert.Equal("base_bowmen", split.OriginalAttilaKey);
        Assert.NotEqual(original.UniqueID, split.UniqueID);
    }

    [Fact]
    public void AllSplitsFeedOnlyTheirExactRegiment()
    {
        var (army, a, b) = Battle();
        var a1 = Unit("100", "a", 50); var a2 = Unit("100", "a", 50); var b1 = Unit("101", "b", 100);
        army.SetUnits(new List<Unit> { a1, a2, b1 });
        army.SetCasualitiesReport(new List<UnitCasualitiesReport> { Report(a1, 50, 25), Report(a2, 50, 25), Report(b1, 100, 100) });
        ApplyCasualties(army);
        Assert.Equal("50", a.CurrentNum);
        Assert.Equal("100", b.CurrentNum);
        Assert.Equal(50, army.ArmyRegiments[0].CurrentNum);
        Assert.Equal(100, army.ArmyRegiments[1].CurrentNum);
    }

    [Fact]
    public void MissingSplitReportAbortsRatherThanBorrowingSameTypeReport()
    {
        var (army, a, b) = Battle();
        var a1 = Unit("100", "a", 50); var a2 = Unit("100", "a", 50); var b1 = Unit("101", "b", 100);
        army.SetUnits(new List<Unit> { a1, a2, b1 });
        army.SetCasualitiesReport(new List<UnitCasualitiesReport> { Report(a1, 50, 25), Report(b1, 100, 100) });
        var ex = Assert.Throws<TargetInvocationException>(() => ApplyCasualties(army));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("100", a.CurrentNum);
        Assert.Equal("100", b.CurrentNum);
    }

    [Fact]
    public void SourceStartingAndSurvivorCountsUseSameBattleScale()
    {
        var options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        options.TryGetValue("BattleScale", out string? previous);
        try
        {
            options["BattleScale"] = "50%";
            var (army, _, _) = Battle();
            var unit = Unit("100", "a", 50);
            army.SetUnits(new List<Unit> { unit });
            army.UnitsResults = new UnitsResults();
            army.UnitsResults.SetAliveMainPhase(new List<(string, string, string, string)> { (Script(unit), "bowmen_" + unit.UniqueID, "7", "25") });
            army.UnitsResults.ScaleTo100Porcent();
            CreateReports(army);
            var report = Assert.Single(army.CasualitiesReports);
            Assert.Equal(100, report.GetStarting());
            Assert.Equal(50, report.GetAliveBeforePursuit());
        }
        finally
        {
            if (previous == null) options.Remove("BattleScale");
            else options["BattleScale"] = previous;
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("-1")]
    public void InvalidExactSurvivorCountCannotBecomeZeroCasualtyEvidence(string remaining)
    {
        var (army, _, _) = Battle();
        var unit = Unit("100", "a", 100); army.SetUnits(new List<Unit> { unit });
        army.UnitsResults = new UnitsResults();
        army.UnitsResults.SetAliveMainPhase(new List<(string, string, string, string)> { (Script(unit), "bowmen_" + unit.UniqueID, "7", remaining) });
        var ex = Assert.Throws<TargetInvocationException>(() => CreateReports(army));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void ExplicitEnabledModeRefusesMissingExportAndWritesActionableCoverage()
    {
        WithMode("Enabled", () =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => StatFidelitySession.Begin("old legacy log"));
            Assert.Contains("Disable CK3 combat stats", error.Message);
            Assert.True(File.Exists(Path.Combine(scratch, "data", "stat_fidelity_coverage.json")));
            Assert.Empty(StatFidelitySession.PendingProfiles);
        });
    }

    [Fact]
    public void DisabledModeLeavesLegacyLogUntouched()
    {
        WithMode("Disabled", () =>
        {
            StatFidelitySession.Begin("old legacy log");
            Assert.False(StatFidelitySession.Enabled);
            Assert.Empty(StatFidelitySession.PendingProfiles);
        });
    }

    [Fact]
    public void OneSidedCommanderEvidenceCannotGenerateAsymmetricBattle()
    {
        WithMode("Enabled", () =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => StatFidelitySession.Begin(
                "CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|10|40|0.05\nCCSTAT|v1|END|snapshot-1"));
            Assert.Contains("both attacker and defender", error.Message);
        });
    }

    [Fact]
    public void ExplicitDevelopmentPartialModeCanRetainLegacyWithDiagnostic()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "settings"));
        File.WriteAllText(Path.Combine(scratch, "settings", "StatFidelity.json"), "{\"RequireCompleteExport\":false,\"RequireCompleteRegimentCoverage\":false}");
        WithMode("Enabled", () =>
        {
            StatFidelitySession.Begin("old legacy log");
            Assert.True(StatFidelitySession.Enabled);
            Assert.False(StatFidelitySession.Export.Complete);
            Assert.NotEmpty(StatFidelitySession.Export.Diagnostics);
            Assert.Empty(StatFidelitySession.PendingProfiles);
        });
    }

    [Fact]
    public void ExactSourceRowsProduceSeparateProfilesAndKeepTemplateKey()
    {
        WithMode("Enabled", () =>
        {
            StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\n" +
                "CCSTAT|v1|COMMANDER|attacker|10|40|0.05\nCCSTAT|v1|COMMANDER|defender|20|16|0.05\n" +
                "CCSTAT|v1|REGIMENT|attacker|none|a|42|source|21|40|30|0|10|20|10\n" +
                "CCSTAT|v1|REGIMENT|attacker|5|b|42|source|22|100|10|0|10|20|10\n" +
                "CCSTAT|v1|END|snapshot-1");
            StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=combat identity=123} } } }\n}\n}"), "42", "123");
            var (army, _, _) = Battle();
            var a = Unit("100", "a", 100); var b = Unit("101", "b", 100);
            a.SetUnitKey("attila_bowmen"); b.SetUnitKey("attila_bowmen");
            var aProfile = StatFidelitySession.Apply(a, army, a.GetAttilaUnitKey());
            var bProfile = StatFidelitySession.Apply(b, army, b.GetAttilaUnitKey());
            Assert.True(aProfile.RegimentApplied && aProfile.CommanderApplied);
            Assert.NotEqual(aProfile.UnitKey, bProfile.UnitKey);
            Assert.Equal("attila_bowmen", a.GetAttilaUnitKey());
            var generated = StatFidelitySession.PendingProfiles.OrderBy(p => p.DamageMultiplier).ToArray();
            Assert.Equal(4.4, generated[0].DamageMultiplier, 10);
            Assert.Equal(3, generated[0].HitPointMultiplier);
            Assert.Equal(11, generated[1].DamageMultiplier, 10);
            Assert.Equal(1, generated[1].HitPointMultiplier);
        });
    }

    [Fact]
    public void ProductionLogSanitizerPreservesFramedStatRecords()
    {
        const string log = "CCSTAT|v1|BEGIN|snapshot-1\n" +
            "CCSTAT|v1|COMMANDER|attacker|10|-5.25|0.05\nCCSTAT|v1|COMMANDER|defender|20|16|0.05\n" +
            "CCSTAT|v1|END|snapshot-1";
        var export = StatExportParser.Parse(HomePage.RemoveASCII(log));
        Assert.True(export.Complete);
        Assert.Empty(export.Diagnostics);
        Assert.Equal(-5.25, export.Commanders["attacker"].Advantage);
    }

    [Fact]
    public void VerifiedFightingCommanderGetsOneKnightBudgetAcrossRegeneration()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "settings"));
        File.WriteAllText(Path.Combine(scratch, "settings", "StatFidelity.json"), "{\"EnableKnightCalibration\":true}");
        WithMode("Enabled", () =>
        {
            StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\n" +
                "CCSTAT|v1|COMMANDER|attacker|10|0|0.05\nCCSTAT|v1|COMMANDER|defender|20|0|0.05\n" +
                "CCSTAT|v1|KNIGHT|attacker|10|20|2|50|10\nCCSTAT|v1|KNIGHT|attacker|11|10|1|50|10\n" +
                "CCSTAT|v1|END|snapshot-1");
            StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=combat identity=123} } } }\n}\n}"), "42", "123");
            var commanderRegiment = new ArmyRegiment("900"); commanderRegiment.SetType(RegimentType.Knight, "10");
            var knightRegiment = new ArmyRegiment("901"); knightRegiment.SetType(RegimentType.Knight, "11");
            var army = new Army("5", "attacker", true) { CommanderID = "10" };
            army.SetArmyRegiments(new List<ArmyRegiment> { commanderRegiment, knightRegiment });
            StatFidelitySession.CaptureCommanderKnightBudget(army);
            // The real reader removes the commander from the knight roster after capture.
            army.ArmyRegiments.Remove(commanderRegiment);
            var remainingKnight = (Knight)Activator.CreateInstance(typeof(Knight), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { "Knight", "11", Culture, 10, 4 }, null)!;
            army.SetKnights(new KnightSystem(new List<Knight> { remainingKnight }, 100));
            var general = new Unit("General", 2, Culture, RegimentType.Commander);
            Assert.True(StatFidelitySession.Apply(general, army, "base_general").KnightApplied);
            Assert.True(StatFidelitySession.PrepareKnightCalibration(army));
            Assert.True(StatFidelitySession.TryApplyKnights(army, "base_knights", out var profile, out int soldiers));
            Assert.True(profile.KnightApplied);
            Assert.Equal(4, soldiers);
            Assert.Single(army.Knights.GetKnightsList());
            var profiles = StatFidelitySession.PendingProfiles.ToArray();
            Assert.Equal(8, profiles.Single(p => p.BaseUnitKey == "base_general").DamageMultiplier);
            Assert.Equal(1, profiles.Single(p => p.BaseUnitKey == "base_knights").DamageMultiplier);
            StatFidelitySession.ResetGeneration();
            Assert.True(StatFidelitySession.Apply(general, army, "base_general").KnightApplied);
            Assert.Equal(8, Assert.Single(StatFidelitySession.PendingProfiles).DamageMultiplier);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void MissingOrDuplicateCalibratedKnightReportsAbortBeforeFateProcessing(int count)
    {
        var army = new Army("5", "attacker", true);
        var unit = new Unit("Knight", 4, Culture, RegimentType.Knight);
        army.SetUnits(new List<Unit> { unit });
        StatFidelitySession.RegisterCalibratedKnightUnit(unit.UniqueID);
        try
        {
            army.UnitsResults = new UnitsResults();
            string script = $"0_attacker_army5_TYPEknights_{unit.UniqueID}_CULTURE7_0";
            army.UnitsResults.SetAliveMainPhase(Enumerable.Range(0, count)
                .Select(_ => (script, "knights_" + unit.UniqueID, "7", "4")).ToList());
            var error = Assert.Throws<TargetInvocationException>(() => CreateReports(army));
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Empty(army.CasualitiesReports);
        }
        finally { StatFidelitySession.ResetGeneration(); }
    }

    [Fact]
    public void StrictCoverageCannotDropAnUnmappedSourceRegiment()
    {
        WithMode("Enabled", () =>
        {
            StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\n" +
                "CCSTAT|v1|COMMANDER|attacker|10|0|0.05\nCCSTAT|v1|COMMANDER|defender|20|0|0.05\n" +
                "CCSTAT|v1|REGIMENT|attacker|none|a|42|source|21|40|30|0|10|20|10\n" +
                "CCSTAT|v1|END|snapshot-1");
            StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=combat identity=123} } } }\n}\n}"), "42", "123");
            var (army, _, _) = Battle();
            var error = Assert.Throws<InvalidOperationException>(() => StatFidelitySession.Apply(Unit("100", "a", 100), army, ""));
            Assert.Contains("cannot drop a source regiment", error.Message);
            Assert.Empty(StatFidelitySession.PendingProfiles);
        });
    }

    private static Unit Unit(string armyRegiment, string source, int soldiers)
    {
        var unit = new Unit("bowmen", soldiers, Culture, RegimentType.MenAtArms);
        unit.SetSourceIdentity("5", armyRegiment, source, "0", "42");
        return unit;
    }
    private static List<Unit> Merge(List<Unit> units)
    {
        var type = typeof(Unit).Assembly.GetType("CrusaderWars.data.save_file.Armies_Functions")!;
        return (List<Unit>)type.GetMethod("OrganizeUnitsIntoCultures", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object?[] { units, new Owner("42") })!;
    }
    private static (Army army, Regiment a, Regiment b) Battle()
    {
        var a = new Regiment("a", "0"); a.SetCulture("7"); a.SetSoldiers("100"); a.SetMax("100");
        var b = new Regiment("b", "0"); b.SetCulture("7"); b.SetSoldiers("100"); b.SetMax("100");
        var arA = new ArmyRegiment("100"); arA.SetType(RegimentType.MenAtArms, "bowmen"); arA.SetRegiments(new List<Regiment> { a });
        var arB = new ArmyRegiment("101"); arB.SetType(RegimentType.MenAtArms, "bowmen"); arB.SetRegiments(new List<Regiment> { b });
        var army = new Army("5", "attacker", true); army.SetArmyRegiments(new List<ArmyRegiment> { arA, arB });
        return (army, a, b);
    }
    private static UnitCasualitiesReport Report(Unit unit, int start, int remaining) =>
        new UnitCasualitiesReport(RegimentType.MenAtArms, "bowmen", Culture, start, remaining, 0, "faction", Script(unit));
    private static string Script(Unit unit) => $"0_attacker_army5_TYPEbowmen_{unit.UniqueID}_CULTURE7_0";
    private static void ApplyCasualties(Army army) => typeof(CasualtyProcessor).GetMethod("ChangeRegimentsSoldiers", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { army });
    private static void CreateReports(Army army) => typeof(CasualtyProcessor).GetMethod("CreateUnitsReports", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { army });
    private static void WithMode(string mode, Action action)
    {
        var options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        options.TryGetValue("StatFidelityMode", out string? previous);
        options["StatFidelityMode"] = mode;
        try { action(); }
        finally
        {
            options["StatFidelityMode"] = "Disabled";
            StatFidelitySession.Begin("");
            if (previous == null) options.Remove("StatFidelityMode");
            else options["StatFidelityMode"] = previous;
        }
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = originalDirectory;
        // This test owns a GUID-named directory under the system temp directory only.
        if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
    }
}
