using CrusaderWars;
using CrusaderWars.data.save_file;
using CrusaderWars.data.battle_results;
using CrusaderWars.client;
using CrusaderWars.armies;
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
        var original = Unit("100", "a", 250, "21");
        original.SetOriginalAttilaKey("base_bowmen");
        var split = new Unit("bowmen", 125, Culture, RegimentType.MenAtArms);
        split.CopySourceIdentity(original);
        Assert.Equal(original.SourceIdentity, split.SourceIdentity);
        Assert.Equal("21", split.SourceOriginProvinceId);
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
            StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=com identity=123} } } }\n}\n}"), "42", "123");
            var (army, _, _) = Battle();
            var a = Unit("100", "a", 100, "21"); var b = Unit("101", "b", 100, "22");
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
    public void LevyOwnerRatiosAreSideSpecificAndCommanderDamageAppliesOnce()
    {
        WithMode("Enabled", () =>
        {
            BindLevyFrame();
            var attacker = new Army("1", "attacker", true); attacker.SetOwner(new Owner("42"));
            var defender = new Army("2", "defender", true); defender.SetOwner(new Owner("42"));
            var unit = new Unit("Levy", 200, Culture, RegimentType.Levy);
            Assert.True(StatFidelitySession.Apply(unit, attacker, "levy").LevyApplied);
            Assert.True(StatFidelitySession.Apply(unit, defender, "levy").LevyApplied);
            var profiles = StatFidelitySession.PendingProfiles.OrderBy(p => p.DamageMultiplier).ToArray();
            Assert.Equal(1, profiles[0].DamageMultiplier);
            Assert.Equal(1.11, profiles[0].HitPointMultiplier, 10);
            Assert.Equal(3.3, profiles[1].DamageMultiplier, 10);
            Assert.Equal(0.555, profiles[1].HitPointMultiplier, 10);
            var missing = new Army("3", "attacker", true); missing.SetOwner(new Owner("43"));
            Assert.Throws<InvalidOperationException>(() => StatFidelitySession.Apply(unit, missing, "levy"));
            missing.SetIsGarrison(true);
            Assert.False(StatFidelitySession.Apply(unit, missing, "levy").LevyApplied);
        });
    }

    [Fact]
    public void PureLevyArmyCanAnchorKnightsOnlyWithItsOwnVerifiedNativeOwnerStats()
    {
        WithMode("Enabled", () =>
        {
            BindLevyFrame();
            var db = new AttilaDatabase();
            AddTable(db, "main_units_tables", new[] { "unit", "land_unit", "num_men", "is_naval" },
                new[] { "levy", "levy", "100", "false" }, new[] { "guard", "guard", "100", "false" });
            AddTable(db, "land_units_tables", new[] { "key", "man_entity", "primary_melee_weapon", "category", "bonus_hit_points" },
                new[] { "levy", "man", "levy_weapon", "infantry", "0" }, new[] { "guard", "man", "guard_weapon", "infantry", "0" });
            AddTable(db, "battle_entities_tables", new[] { "key", "hit_points" }, new[] { "man", "100" });
            AddTable(db, "melee_weapons_tables", new[] { "key", "damage", "ap_damage" }, new[] { "levy_weapon", "8", "4" }, new[] { "guard_weapon", "6", "4" });
            var prepared = (PreparedAttilaDatabase)typeof(PreparedAttilaDatabase).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
                .Invoke(new object[] { db, Array.Empty<string>(), (Action)(() => { }) });
            typeof(StatFidelitySession).GetProperty(nameof(StatFidelitySession.PreparedDatabase))!.SetValue(null, prepared);
            var army = new Army("1", "attacker", true); army.SetOwner(new Owner("42"));
            var levy = new Unit("Levy", 200, Culture, RegimentType.Levy); levy.SetUnitKey("levy"); army.SetUnits(new List<Unit> { levy });
            var knights = new[] { new KnightStats { CharacterId = "7", Prowess = 10, Effectiveness = 1, DamagePerProwess = 50, ToughnessPerProwess = 10 } };
            ArmyProportions.SetRatio(100);
            var method = typeof(StatFidelitySession).GetMethod("PlanKnightBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
            var plan = (KnightBudgetPlan)method.Invoke(null, new object[] { army, "guard", knights })!;
            Assert.Equal("levy:attacker:42", plan.Anchor.SourceIdentity);
            Assert.Equal(15, plan.Anchor.Ck3Damage);
            Assert.Equal(5.55, plan.Anchor.Ck3Toughness);
            Assert.Equal(18, plan.Anchor.AttilaDamageBeforeCommander);
            // Matches the backend's stored double ratio: 5.55/10 *100 is just below55.5.
            Assert.Equal(55, plan.Anchor.AttilaHitPoints);
            army.SetOwner(new Owner("43"));
            Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { army, "guard", knights })).InnerException);
        });
    }

    [Fact]
    public void DefaultFidelityRejectsAnEntirelyMissingLevyExport()
    {
        WithMode("Enabled", () =>
        {
            BindLevyFrame(includeLevies: false);
            var army = new Army("1", "attacker", true); army.SetOwner(new Owner("42"));
            Assert.Throws<InvalidOperationException>(() => StatFidelitySession.Apply(new Unit("Levy", 200, Culture, RegimentType.Levy), army, "levy"));
            Assert.Empty(StatFidelitySession.PendingProfiles);
        });
    }

    private static void AddTable(AttilaDatabase db, string name, string[] columns, params string[][] rows)
    {
        var table = new TsvTable(name, "1", columns);
        foreach (var row in rows) table.Rows.Add(columns.Select((column, i) => (column, row[i])).ToDictionary(pair => pair.column, pair => pair.Item2));
        db.Merge(table);
    }

    private static void BindLevyFrame(bool includeLevies = true)
    {
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|10|40|0.05\nCCSTAT|v1|COMMANDER|defender|20|16|0.05\n" +
            (includeLevies ? "CCSTAT|v1|LEVY|attacker|42|15|5.55|10|10\nCCSTAT|v1|LEVY|defender|42|10|11.1|10|10\n" : "") + "CCSTAT|v1|END|snapshot-1");
        StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=com identity=123} } } }\n}\n}"), "42", "123");
    }

    [Fact]
    public void CommanderlessRepresentationCannotKillWoundOrCaptureArmyOwner()
    {
        WithMode("Enabled", () =>
        {
            StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|20|16|0.05\nCCSTAT|v1|END|snapshot-1");
            StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=com} } } }\n}\n}"), "42", "0");
            var army = new Army("1", "attacker", true); army.SetOwner(new Owner("42", Culture));
            Assert.True(StatFidelitySession.EnsureNativeCommanderRepresentation(army));
            Assert.Null(army.CommanderID);
            Assert.False(army.Commander!.IsTrackedCharacter);
            Assert.NotEqual("42", army.Commander.ID);
            string log = Path.Combine(scratch, "fallen.log");
            File.WriteAllText(log, "Commander42 from Army1 has fallen\nCommander" + army.Commander.ID + " from Army1 has fallen\n");
            CasualtyProcessor.CheckForSlainCommanders(army, log);
            Assert.False(army.Commander.hasFallen);
            Assert.Equal((false, false, "traits={ 1 2 }"), army.Commander.Health("traits={ 1 2 }", true));
            Assert.False(army.Commander.IsSlain || army.Commander.IsPrisoner);
            Assert.False(BattleResult.SearchCharacters("42", new List<Army> { army }).searchStarted);
            Assert.False(BattleResult.SearchCharacters(army.Commander.ID, new List<Army> { army }).searchStarted);
        });
    }

    [Fact]
    public void NativeSnapshotSelectsExactCommanderlessCombatInsteadOfLastOrOwnerLedCombat()
    {
        WithMode("Enabled", () =>
        {
            StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|20|0|0.05\nCCSTAT|v1|END|snapshot-1");
            DataSearch.Player_Character = new PlayerChar("42", "7");
            Directory.CreateDirectory(Path.GetDirectoryName(Writter.DataFilesPaths.Living_Path())!);
            File.WriteAllText(Writter.DataFilesPaths.Living_Path(), "living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=com} } } }\n}\n}");
            File.WriteAllText(Writter.DataFilesPaths.Combats_Path(), "\tcombats={\n\t\t0={\n\t\t\tprovince=1527\n\t\t}\n\t\t8={\n\t\t\t\tcommander=42\n\t\t\tprovince=999\n\t\t}\n\t}");
            BattleResult.ReadPlayerCombat("none");
            Assert.Equal("0", BattleResult.CombatID);
            Assert.Contains("province=1527", BattleResult.Player_Combat);
            Assert.DoesNotContain("province=999", BattleResult.Player_Combat);
        });
    }

    [Fact]
    public void ExactCombatResultLinkCannotSelectAnOlderBattleInTheSameProvince()
    {
        WithMode("Enabled", () =>
        {
            StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|20|0|0.05\nCCSTAT|v1|END|snapshot-1");
            DataSearch.Player_Character = new PlayerChar("42", "7");
            CrusaderWars.twbattle.BattleState.IsSiegeBattle = false;
            BattleResult.ProvinceID = "1527";
            Directory.CreateDirectory(Path.GetDirectoryName(Writter.DataFilesPaths.Living_Path())!);
            File.WriteAllText(Writter.DataFilesPaths.Living_Path(), "living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=com} } } }\n}\n}");
            File.WriteAllText(Writter.DataFilesPaths.Combats_Path(), "\tcombats={\n\t\t0={\n\t\t\tprovince=1527\n\t\t\tcombat_results=9\n\t\t}\n\t}");
            File.WriteAllText(Writter.DataFilesPaths.CombatResults_Path(), "\tcombat_results={\n\t\t7={\n\t\t\tlocation=1527\n\t\t\tmarker=old\n\t\t}\n\t\t9={\n\t\t\tlocation=1527\n\t\t\tmarker=current\n\t\t}\n\t}");
            BattleResult.GetPlayerCombatResult();
            Assert.Equal("9", BattleResult.ResultID);
            Assert.Contains("marker=current", BattleResult.Player_CombatResult);
            Assert.DoesNotContain("marker=old", BattleResult.Player_CombatResult);
            File.WriteAllText(Writter.DataFilesPaths.CombatResults_Path(), "\tcombat_results={\n\t\t7={\n\t\t\tlocation=1527\n\t\t}\n\t}");
            Assert.Throws<InvalidOperationException>(BattleResult.GetPlayerCombatResult);
            Assert.Null(BattleResult.ResultID);
            Assert.Null(BattleResult.Player_CombatResult);
        });
    }

    [Fact]
    public void VerifiedFightingCommanderGetsOneKnightBudgetAcrossRegeneration()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "settings"));
        File.WriteAllText(Path.Combine(scratch, "settings", "StatFidelity.json"), "{\"EnableKnightCalibration\":true,\"KnightBudgetMode\":\"TemplateRelative\"}");
        WithMode("Enabled", () =>
        {
            StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\n" +
                "CCSTAT|v1|COMMANDER|attacker|10|0|0.05\nCCSTAT|v1|COMMANDER|defender|20|0|0.05\n" +
                "CCSTAT|v1|KNIGHT|attacker|10|20|2|50|10\nCCSTAT|v1|KNIGHT|attacker|11|10|1|50|10\n" +
                "CCSTAT|v1|END|snapshot-1");
            StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=com identity=123} } } }\n}\n}"), "42", "123");
            var commanderRegiment = new ArmyRegiment("900"); commanderRegiment.SetType(RegimentType.Knight, "10");
            var knightRegiment = new ArmyRegiment("901"); knightRegiment.SetType(RegimentType.Knight, "11");
            var army = new Army("5", "attacker", true) { CommanderID = "10" };
            army.SetArmyRegiments(new List<ArmyRegiment> { commanderRegiment, knightRegiment });
            StatFidelitySession.CaptureCommanderKnightBudget(army);
            // The real reader removes the commander from the knight roster after capture.
            army.ArmyRegiments.Remove(commanderRegiment);
            Assert.True(StatFidelitySession.TryCreateNativeKnights(army, new(), out var nativeKnights));
            army.SetKnights(nativeKnights);
            Assert.Equal("11", Assert.Single(army.Knights.GetKnightsList()).GetID());
            Assert.Equal(10, army.Knights.GetKnightsList()[0].GetProwess());
            Assert.Equal(0, army.Knights.Effectiveness); // native total1 means no additive legacy bonus
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
            StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=com identity=123} } } }\n}\n}"), "42", "123");
            var (army, _, _) = Battle();
            var error = Assert.Throws<InvalidOperationException>(() => StatFidelitySession.Apply(Unit("100", "a", 100), army, ""));
            Assert.Contains("cannot drop a source regiment", error.Message);
            Assert.Empty(StatFidelitySession.PendingProfiles);
        });
    }

    [Theory]
    [InlineData("no")]
    [InlineData("yes")]
    public void FidelitySaveWritebackPreservesAchievementEligibilityBytes(string eligibility)
    {
        WithMode("Enabled", () =>
        {
            StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\n" +
                "CCSTAT|v1|COMMANDER|attacker|10|0|0.05\nCCSTAT|v1|COMMANDER|defender|20|0|0.05\n" +
                "CCSTAT|v1|END|snapshot-1");
            Directory.CreateDirectory(Path.Combine(scratch, "data", "save_file_data"));
            string input = Path.Combine(scratch, "eligibility.gamestate");
            byte[] original = System.Text.Encoding.UTF8.GetBytes("meta_data={\n\tcan_get_achievements=" + eligibility + "\n\tother_flag=no\n}\n");
            File.WriteAllBytes(input, original);
            Writter.SendDataToFile(input);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(scratch, "data", "save_file_data", "gamestate")));
            Assert.Equal(original, File.ReadAllBytes(input));
        });
    }

    [Fact]
    public void NativeCommanderLeadingBonusDoesNotReceiveSecondXpBonus()
    {
        var leadingOnly = new Modifiers("cc_advantage_leading");
        Assert.Equal(1, leadingOnly.GetXP());
        Assert.Equal(0, leadingOnly.GetXP(commanderAdvantageApplied: true));
        var withSeparateModifiers = new Modifiers("cc_advantage_leading faith_hostility cc_advantage_nosupplies");
        Assert.Equal(0, withSeparateModifiers.GetXP());
        Assert.Equal(-1, withSeparateModifiers.GetXP(commanderAdvantageApplied: true));
    }

    [Fact]
    public void MultipleKnightFormationsAggregateSurvivorsAndKillsOnce()
    {
        var army = new Army("5", "attacker", true);
        var knights = new[] { "11", "12" }.Select(id => (Knight)Activator.CreateInstance(typeof(Knight), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { "Knight " + id, id, Culture, 10, 4 }, null)!).ToList();
        foreach (var knight in knights) knight.FidelitySoldierCount = 4;
        army.SetKnights(new KnightSystem(knights, 0));
        var units = new[] { new Unit("Knight", 4, Culture, RegimentType.Knight), new Unit("Knight", 4, Culture, RegimentType.Knight) };
        army.SetUnits(units.ToList());
        foreach (var unit in units) StatFidelitySession.RegisterCalibratedKnightUnit(unit.UniqueID);
        try
        {
            army.UnitsResults = new UnitsResults();
            army.UnitsResults.SetAliveMainPhase(units.Select(u => ($"0_attacker_army5_TYPEknights_{u.UniqueID}_CULTURE7_0", "knights_" + u.UniqueID, "7", "4")).ToList());
            army.UnitsResults.SetKillsMainPhase(units.Select((u, index) => ($"kills_0_attacker_army5_TYPEknights_{u.UniqueID}_CULTURE7_0", "knights_" + u.UniqueID, "7", index == 0 ? "3" : "7")).ToList());
            CasualtyProcessor.CheckForSlainKnights(army);
            Assert.All(knights, knight => Assert.False(knight.HasFallen()));
            CasualtyProcessor.CheckKnightsKills(army);
            Assert.Equal(10, knights.Sum(k => k.GetKills()));
        }
        finally { StatFidelitySession.ResetGeneration(); }
    }

    [Fact]
    public void CharacterFormationResultCountsAreNotUnscaledAgain()
    {
        var options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        options.TryGetValue("BattleScale", out string? previous);
        try
        {
            options["BattleScale"] = "50%";
            var results = new UnitsResults();
            results.SetAliveMainPhase(new() { ("a", "knights_123", "7", "20"), ("b", "commander42_124", "7", "2"), ("c", "bowmen_125", "7", "100") });
            results.ScaleTo100Porcent();
            Assert.Equal(new[] { "20", "2", "200" }, results.Alive_MainPhase.Select(r => r.Remaining));
        }
        finally { if (previous == null) options.Remove("BattleScale"); else options["BattleScale"] = previous; }
    }

    [Fact]
    public void DirectGeneralDoesNotStackLegacyAccoladeAbilities()
    {
        string scriptDirectory = Path.Combine(scratch, "data", "battle files", "script");
        Directory.CreateDirectory(scriptDirectory);
        string battlePath = Path.Combine(scriptDirectory, "tut_battle.xml");
        File.WriteAllText(battlePath, "");
        var positionField = typeof(BattleFile).GetField("Position", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rotationField = typeof(BattleFile).GetField("Rotation", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? previousPosition = positionField.GetValue(null);
        object? previousRotation = rotationField.GetValue(null);
        var left = DeclarationsFile.GetLeftSideArmies();
        var previousLeft = left.ToArray();
        left.Clear(); left.Add(new Army("5", "defender", true));
        var options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        options.TryGetValue("BattleMapsSize", out string? previousMapSize);
        options["BattleMapsSize"] = "Medium";
        try
        {
            positionField.SetValue(null, Activator.CreateInstance(positionField.FieldType, new object[] { 0, 0 }));
            rotationField.SetValue(null, "0");
            var commander = new CommanderSystem("Test General", "42", 20, 40, 4, true);
            commander.SetAccolade(new Accolade("1", "contender_attribute", "mentor_attribute", "500"));
            BattleFile.AddGeneralUnit(commander, "base_general", "0_defender_army5_TYPEcommander42_123_CULTURE7_", 0, "N");
            BattleFile.AddGeneralUnit(commander, "direct_general", "1_defender_army5_TYPEcommander42_124_CULTURE7_", 0, "N", true, 2, includeAccoladeAbilities: false);
            var document = System.Xml.Linq.XDocument.Parse("<root>" + File.ReadAllText(battlePath) + "</root>");
            var units = document.Root!.Elements("unit").ToArray();
            Assert.NotEmpty(units[0].Descendants("special_ability"));
            Assert.Empty(units[1].Descendants("special_ability"));
            Assert.Equal("2", units[1].Attribute("num_soldiers")!.Value);
            Assert.Equal("1", units[1].Descendants("star_rating").Single().Attribute("level")!.Value);
        }
        finally
        {
            positionField.SetValue(null, previousPosition); rotationField.SetValue(null, previousRotation);
            left.Clear(); left.AddRange(previousLeft);
            if (previousMapSize == null) options.Remove("BattleMapsSize"); else options["BattleMapsSize"] = previousMapSize;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("22")]
    public void MissingOrDifferentNativeStationContextRejectsTheProfile(string? origin)
    {
        WithMode("Enabled", () =>
        {
            StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|10|0|0.05\nCCSTAT|v1|COMMANDER|defender|20|0|0.05\n" +
                "CCSTAT|v1|REGIMENT|attacker|none|a|42|source|21|40|30|0|10|20|10\nCCSTAT|v1|END|snapshot-1");
            StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={ data={ { flag=ccstat_snapshot_serial data={type=value identity=100000} } { flag=ccstat_combat data={type=com identity=123} } } }\n}\n}"), "42", "123");
            var (army, _, _) = Battle();
            var error = Assert.Throws<InvalidOperationException>(() => StatFidelitySession.Apply(Unit("100", "a", 100, origin), army, "base_unit"));
            Assert.Contains("origin/station", error.Message);
            Assert.Empty(StatFidelitySession.PendingProfiles);
        });
    }

    private static Unit Unit(string armyRegiment, string source, int soldiers, string? origin = null)
    {
        var unit = new Unit("bowmen", soldiers, Culture, RegimentType.MenAtArms);
        unit.SetSourceIdentity("5", armyRegiment, source, "0", "42", origin);
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
