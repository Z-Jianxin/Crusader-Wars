using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

public sealed class CommanderGuardTests : IDisposable
{
    private readonly string previous = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-funded-guard-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private readonly Dictionary<string, string> oldOptions;
    private readonly FieldInfo scripts = typeof(Army).Assembly.GetType("CrusaderWars.Data")!.GetField("units_scripts", BindingFlags.Public | BindingFlags.Static)!;
    private readonly object oldScripts;
    private readonly FieldInfo nativeCombat = typeof(ArmiesReader).GetField("nativeCombatParticipation", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? oldNativeCombat;
    private readonly Army army = new("1", "attacker", true);
    private static readonly Culture Culture = new("7", "test", "test");
    private readonly List<XElement> xml = new();
    private readonly Unit guard;
    private PreparedAttilaDatabase database;

    public CommanderGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data")); Environment.CurrentDirectory = scratch;
        oldOptions = new(options); options["StatFidelityMode"] = "Enabled"; options["BattleScale"] = "100%";
        oldScripts = scripts.GetValue(null)!;
        oldNativeCombat = nativeCombat.GetValue(null);
        army.SetOwner(new Owner("42", Culture)); army.SetUnits(new()); army.SetArmyRegiments(new()); army.CommanderID = "500";
        var db = new AttilaDatabase();
        Add(db, "main_units_tables", ["unit", "land_unit", "num_men", "is_naval"], ["melee", "melee", "100", "false"], ["bow", "bow", "100", "false"]);
        Add(db, "land_units_tables", ["key", "man_entity", "primary_melee_weapon", "category", "bonus_hit_points", "primary_missile_weapon"],
            ["melee", "man", "sword", "inf_melee", "100", ""], ["bow", "man", "sword", "inf_ranged", "100", "bow"]);
        Add(db, "battle_entities_tables", ["key", "hit_points"], ["man", "100"]);
        Add(db, "melee_weapons_tables", ["key", "damage", "ap_damage"], ["sword", "12", "8"]);
        database = (PreparedAttilaDatabase)typeof(PreparedAttilaDatabase).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke(new object[] { db, Array.Empty<string>(), (Action)(() => { }) });
        BindFrame(false);
        guard = new Unit("General", 2, Culture, RegimentType.Commander); guard.SetCharacterID("500"); guard.SetUnitKey("melee"); army.Units.Add(guard);
        Emit(guard, StatFidelitySession.Apply(guard, army, "melee", false).UnitKey, "commander500");
    }

    [Theory]
    [InlineData(25, 60)]
    [InlineData(100, 85)]
    [InlineData(50, 70)]
    [InlineData(75, 80)]
    public void GuardDetachesExistingTroopsAndReturnsExactNativeLossesAtEachScale(int scale, int expected)
    {
        options["BattleScale"] = scale + "%"; ArmyProportions.SetRatio(scale);
        var (source, donor) = Maa(100, scale);
        var originalProfile = StatFidelitySession.PendingProfiles.Single(p => p.Id.Contains(donor.SourceIdentity!));
        StatFidelitySession.FundCommanderGuard(army, xml);
        int borrowed = Math.Min(30, scale);
        Assert.Equal(borrowed, guard.GetSoldiers()); Assert.Equal(scale - borrowed, donor.GetSoldiers());
        Assert.Equal(scale, army.Units.Sum(u => u.GetSoldiers()));
        var metadata = Assert.IsType<CommanderGuardRepresentation>(guard.CommanderGuardRepresentation);
        Assert.Null(metadata.NativeKnightId); Assert.Equal(0, metadata.PersonalDamageShare);
        Assert.Equal("500", guard.GetCharacterID()); Assert.Equal(RegimentType.Commander, guard.GetRegimentType());
        var guardProfile = StatFidelitySession.PendingProfiles.Single(p => p.Id.Contains(":commander-funded:"));
        Assert.Equal(originalProfile.DamageMultiplier, guardProfile.DamageMultiplier);
        Assert.Equal(originalProfile.HitPointMultiplier, guardProfile.HitPointMultiplier);
        BindDeployment(); Consume(u => ReferenceEquals(u, guard) ? 15 : u.GetSoldiers());
        Assert.Equal(expected.ToString(), source.CurrentNum); Assert.Equal(expected, army.ArmyRegiments[0].CurrentNum);
        Assert.Equal(15, army.CasualitiesReports.Single(r => r.Script.Contains("_" + guard.UniqueID + "_CULTURE")).GetAliveBeforePursuit());
        var evidence = BattleReportEvidence.Validate(StatFidelitySession.DeployedRoster,
            new[] {string.Join("\n", army.UnitsResults.Alive_MainPhase.Select(r => r.Script + "-" + r.Remaining))},
            new[] {string.Join("\n", army.UnitsResults.Kills_MainPhase.Select(r => r.Script + "-" + r.Kills))});
        var statistics = CombatResultStatistics.Build(new[] {army}, Array.Empty<Army>(), StatFidelitySession.DeployedRoster, evidence);
        Assert.Equal(army.Units.Count * 3, Assert.Single(statistics.Updates).MainKills); // guard credits belong to its donor, not an invented knight.
    }

    [Fact]
    public void WholeSmallCohortTransfersWithoutZeroBodyXmlOrExtraGeneral()
    {
        var (source, donor) = Maa(15, 15);
        StatFidelitySession.FundCommanderGuard(army, xml);
        Assert.Equal(15, guard.GetSoldiers()); Assert.Single(xml); Assert.Single(army.Units);
        Assert.DoesNotContain(donor, army.Units);
        BindDeployment(); Assert.Single(StatFidelitySession.PendingProfiles); Consume(_ => 5);
        Assert.Equal("5", source.CurrentNum);
        StatFidelitySession.RestoreCommanderGuardDonation(army);
        Assert.Equal(15, donor.GetSoldiers()); Assert.Contains(donor, army.Units); Assert.Null(guard.CommanderGuardRepresentation);
    }

    [Fact]
    public void ActualLevyDonorUsesItsOwnNativeCulturePoolWithBoundedSourceAllocation()
    {
        var sources = new[] {LevySource("11", "101", 40), LevySource("12", "102", 60)};
        var levy = new Unit("Levy", 100, Culture, RegimentType.Levy); levy.SetUnitKey("melee"); army.Units.Add(levy);
        Emit(levy, StatFidelitySession.Apply(levy, army, "melee").UnitKey, "Levy");
        StatFidelitySession.FundCommanderGuard(army, xml);
        Assert.Equal(RegimentType.Levy, guard.CommanderGuardRepresentation!.DonorType);
        BindDeployment(); Consume(u => ReferenceEquals(u, guard) ? 0 : u.GetSoldiers());
        Assert.Equal(new[] {"28", "42"}, sources.Select(s => s.CurrentNum));
        Assert.Equal(new[] {40, 60}, sources.Select(s => int.Parse(s.Max)));
    }

    [Fact]
    public void VerifiedCommanderKnightAddsOneAggregatePersonalBudgetInsteadOfMultiplyingEveryGuard()
    {
        BindFrame(true);
        xml[0].Element("unit_type")!.SetAttributeValue("type", StatFidelitySession.Apply(guard, army, "melee", false).UnitKey);
        var (_, donor) = Maa(100, 100);
        var nativeKnight = new ArmyRegiment("50") { CurrentNum = 1 }; nativeKnight.SetType(RegimentType.Knight, "500"); army.ArmyRegiments.Add(nativeKnight);
        var unavailable = new ArmyRegiment("51") {CurrentNum = 0}; unavailable.SetType(RegimentType.Knight, "999"); army.ArmyRegiments.Add(unavailable); // no export is needed for unavailable stock
        StatFidelitySession.CaptureCommanderKnightBudget(army);
        StatFidelitySession.FundCommanderGuard(army, xml);
        var metadata = guard.CommanderGuardRepresentation!;
        Assert.Equal("500", metadata.NativeKnightId); Assert.Equal(30, guard.GetSoldiers());
        Assert.Single(army.Units.Where(u => u.GetCharacterID() == "500")); Assert.Empty(army.Knights.GetKnightsList());
        var profile = StatFidelitySession.PendingProfiles.Single(p => p.Id.Contains(":commander-funded:"));
        // 100 ordinary bodies ×30 effective damage + 500 native knight damage ×1.5 advantage.
        Assert.Equal(3750, donor.GetSoldiers() * 30 + guard.GetSoldiers() * 20 * profile.DamageMultiplier, 6);
        Assert.Equal(21000, donor.GetSoldiers() * 200 + guard.GetSoldiers() * 200 * profile.HitPointMultiplier, 6);
        var shares = CommanderGuardAccounting.SplitCredits(100, metadata.PersonalDamageShare);
        Assert.Equal(100, shares.Donor + shares.Personal); Assert.InRange(shares.Personal, 1, 99);
    }

    [Fact]
    public void RangedOnlyArmyRetainsMinimalGeneralWithoutInventingPersonalProwessPower()
    {
        var (_, bow) = Maa(100, 100, "bow");
        StatFidelitySession.FundCommanderGuard(army, xml);
        Assert.Equal(2, guard.GetSoldiers()); Assert.Equal(100, bow.GetSoldiers()); Assert.Null(guard.CommanderGuardRepresentation);
    }

    [Fact]
    public void ZeroCurrentCommanderKnightRecordDoesNotGrantPersonalCombatStrength()
    {
        BindFrame(true); xml[0].Element("unit_type")!.SetAttributeValue("type", StatFidelitySession.Apply(guard, army, "melee", false).UnitKey);
        Maa(100, 100);
        var nativeKnight = new ArmyRegiment("50") { CurrentNum = 0 }; nativeKnight.SetType(RegimentType.Knight, "500"); army.ArmyRegiments.Add(nativeKnight);
        StatFidelitySession.CaptureCommanderKnightBudget(army); StatFidelitySession.FundCommanderGuard(army, xml);
        Assert.Null(guard.CommanderGuardRepresentation!.NativeKnightId);
        Assert.Equal(0, guard.CommanderGuardRepresentation.PersonalDamageShare);
    }

    [Fact]
    public void StockedButNonparticipatingCommanderKnightDoesNotGrantPersonalCombatStrength()
    {
        BindFrame(true); nativeCombat.SetValue(null, NativeCombatParticipation.Parse("attacker={armies={1} men_at_arms={ {regiment=50 starting=1 current=0} }} defender={armies={2}}"));
        xml[0].Element("unit_type")!.SetAttributeValue("type", StatFidelitySession.Apply(guard, army, "melee", false).UnitKey);
        Maa(100, 100); var nativeKnight = new ArmyRegiment("50") { CurrentNum = 1 }; nativeKnight.SetType(RegimentType.Knight, "500"); army.ArmyRegiments.Add(nativeKnight);
        StatFidelitySession.CaptureCommanderKnightBudget(army); StatFidelitySession.FundCommanderGuard(army, xml);
        Assert.Null(guard.CommanderGuardRepresentation!.NativeKnightId);
    }

    [Fact]
    public void MissingDonorEvidenceFailsBeforeMutatingItsNativeCount()
    {
        var (source, _) = Maa(100, 100);
        StatFidelitySession.FundCommanderGuard(army, xml); BindDeployment();
        army.SetCasualitiesReport(new());
        Assert.Throws<InvalidOperationException>(() => StatFidelitySession.GetCommanderGuardCasualties(army));
        Assert.Equal("100", source.CurrentNum);
    }

    [Fact]
    public void GuardPursuitLossesAreCountedOnceInNativeUnitsAtHalfScale()
    {
        options["BattleScale"] = "50%"; ArmyProportions.SetRatio(50);
        var (source, _) = Maa(100, 50); StatFidelitySession.FundCommanderGuard(army, xml); BindDeployment();
        Consume(u => ReferenceEquals(u, guard) ? 15 : 20, u => ReferenceEquals(u, guard) ? 0 : 10);
        Assert.Equal("20", source.CurrentNum);
        var reports = BattleReportEvidence.Validate(StatFidelitySession.DeployedRoster,
            new[] { string.Join("\n", army.UnitsResults.Alive_MainPhase.Select(r => r.Script + "-" + r.Remaining)),
                string.Join("\n", army.UnitsResults.Alive_PursuitPhase!.Select(r => r.Script + "-" + r.Remaining)) },
            new[] { string.Join("\n", army.UnitsResults.Kills_MainPhase.Select(r => r.Script + "-" + r.Kills)),
                string.Join("\n", army.UnitsResults.Kills_PursuitPhase!.Select(r => r.Script + "-" + r.Kills)) });
        var statistics = CombatResultStatistics.Build(new[] {army}, Array.Empty<Army>(), StatFidelitySession.DeployedRoster, reports);
        var result = Assert.Single(statistics.Updates);
        Assert.Equal(20, result.FinalCount); Assert.Equal(50, result.PursuitLosses);
        Assert.Equal(6, result.MainKills); Assert.Equal(4, result.PursuitKills);
    }

    [Theory]
    [InlineData(50)] [InlineData(100)]
    public void RegenerationRestoresDonationOnceWithoutCreatingNewNativeStrength(int scale)
    {
        options["BattleScale"] = scale + "%"; ArmyProportions.SetRatio(scale);
        var (_, donor) = Maa(100, scale); donor.SetInitialSoldiers(100); donor.ChangeSoldiers(scale);
        StatFidelitySession.RegisterMaaSourceCohorts(army, verifyOriginalStrength: true);
        StatFidelitySession.FundCommanderGuard(army, xml);
        Assert.Equal(scale - 30, donor.GetSoldiers());
        StatFidelitySession.ResetGeneration(); StatFidelitySession.RestoreCommanderGuardDonation(army);
        Assert.Equal(scale, donor.GetSoldiers()); Assert.Null(guard.CommanderGuardRepresentation);
        StatFidelitySession.RegisterMaaSourceCohorts(army, verifyOriginalStrength: true);
        Assert.Single(army.Units.Where(u => ReferenceEquals(u, donor)));
        xml.Clear(); guard.SetInitialSoldiers(2);
        Emit(guard, StatFidelitySession.Apply(guard, army, "melee", false).UnitKey, "commander500");
        Emit(donor, StatFidelitySession.Apply(donor, army, "melee").UnitKey, "swords");
        StatFidelitySession.FundCommanderGuard(army, xml);
        Assert.Equal(scale - 30, donor.GetSoldiers()); Assert.Equal(30, guard.GetSoldiers());
    }

    [Fact]
    public void DisabledModeDoesNotReassignExistingUnits()
    {
        var (_, donor) = Maa(100, 100);
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin("");
        StatFidelitySession.FundCommanderGuard(army, xml);
        Assert.Equal(2, guard.GetSoldiers()); Assert.Equal(100, donor.GetSoldiers());
    }

    private void BindFrame(bool knight)
    {
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|500|10|0.05\nCCSTAT|v1|COMMANDER|defender|none|0|0.05\n" +
            "CCSTAT|v1|LEVY|attacker|42|10|10|10|10\nCCSTAT|v1|REGIMENT|attacker|none|100|42|source|none|20|20|0|0|20|20\n" +
            (knight ? "CCSTAT|v1|KNIGHT|attacker|500|10|1|50|10\n" : "") + "CCSTAT|v1|END|snapshot-1");
        StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={data={ {flag=ccstat_snapshot_serial data={type=value identity=100000}} {flag=ccstat_combat data={type=com}} }}\n}\n}"), "42", "0");
        typeof(StatFidelitySession).GetProperty(nameof(StatFidelitySession.PreparedDatabase))!.SetValue(null, database);
        nativeCombat.SetValue(null, NativeCombatParticipation.Parse("attacker={armies={1} men_at_arms={ {regiment=50 starting=1 current=1} }} defender={armies={2}}"));
        ArmyProportions.SetRatio(100);
    }
    private (Regiment, Unit) Maa(int native, int bodies, string key = "melee")
    {
        var source = new Regiment("100", "0"); source.SetSoldiers(native.ToString()); source.SetMax(native.ToString()); source.SetOwner("42"); source.SetCulture("7");
        var regiment = new ArmyRegiment("10") {CurrentNum = native}; regiment.SetType(RegimentType.MenAtArms, "swords"); regiment.SetRegiments(new() {source}); army.ArmyRegiments.Add(regiment);
        var unit = new Unit("swords", bodies, Culture, RegimentType.MenAtArms); unit.SetUnitKey(key); unit.SetSourceIdentity(army.ID, "10", "100", "0", "42", "none"); army.Units.Add(unit);
        Emit(unit, StatFidelitySession.Apply(unit, army, key).UnitKey, "swords"); return (source, unit);
    }
    private Regiment LevySource(string regimentId, string id, int count)
    {
        var source = new Regiment(id, "0"); source.SetSoldiers(count.ToString()); source.SetMax(count.ToString()); source.SetOwner("42"); source.SetCulture("7");
        var regiment = new ArmyRegiment(regimentId) {CurrentNum = count}; regiment.SetType(RegimentType.Levy, ""); regiment.SetRegiments(new() {source}); army.ArmyRegiments.Add(regiment); return source;
    }
    private void Emit(Unit unit, string key, string type) => xml.Add(new XElement("unit", new XAttribute("num_soldiers", unit.GetSoldiers()),
        new XAttribute("script_name", "0_attacker_army1_TYPE" + type + "_" + unit.UniqueID + "_CULTURE7_0"), new XElement("unit_type", new XAttribute("type", key))));
    private void BindDeployment()
    {
        scripts.SetValue(null, xml.Select(e => (e.Attribute("script_name")!.Value, "declaration")).ToList());
        new XDocument(new XElement("battle", xml)).Save("deployment.xml"); StatFidelitySession.CaptureDeployedRoster(new[] {army}, "deployment.xml");
    }
    private void Consume(Func<Unit, int> remain, Func<Unit, int>? after = null)
    {
        army.UnitsResults = new UnitsResults();
        var rows = xml.Select(e => {
            string name = e.Attribute("script_name")!.Value;
            var unit = army.Units.Single(u => name.Contains("_" + u.UniqueID + "_CULTURE"));
            string type = name.Split("_TYPE")[1].Split("_CULTURE")[0];
            return (name, type, "7", remain(unit).ToString());
        }).ToList();
        army.UnitsResults.SetAliveMainPhase(rows); army.UnitsResults.SetKillsMainPhase(rows.Select(r => ("kills_" + r.name, r.type, "7", "3")).ToList());
        if (after != null)
        {
            army.UnitsResults.SetAlivePursuitPhase(rows.Select(r => (r.name, r.type, "7", after(army.Units.Single(u => r.name.Contains("_" + u.UniqueID + "_CULTURE"))).ToString())).ToList());
            army.UnitsResults.SetKillsPursuitPhase(rows.Select(r => ("kills_" + r.name, r.type, "7", "5")).ToList());
        }
        army.UnitsResults.ScaleTo100Porcent();
        foreach (string method in new[] {"CreateUnitsReports", "ChangeRegimentsSoldiers"})
            typeof(CasualtyProcessor).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] {army});
    }
    private static void Add(AttilaDatabase db, string name, string[] columns, params string[][] rows)
    {
        var table = new TsvTable(name, "1", columns); foreach (var row in rows) table.Rows.Add(columns.Select((column, index) => (column, row[index])).ToDictionary(v => v.column, v => v.Item2)); db.Merge(table);
    }
    public void Dispose()
    {
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin(""); options.Clear(); foreach (var pair in oldOptions) options.Add(pair.Key, pair.Value);
        scripts.SetValue(null, oldScripts); Environment.CurrentDirectory = previous;
        nativeCombat.SetValue(null, oldNativeCombat);
        if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
    }
}
