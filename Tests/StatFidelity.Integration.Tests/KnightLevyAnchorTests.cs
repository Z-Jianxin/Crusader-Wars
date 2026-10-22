using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using CrusaderWars.twbattle;
using System.Reflection;
using System.Xml;
using Xunit;

public sealed class KnightLevyAnchorTests : IDisposable
{
    private static readonly Culture Culture = new("231", "cornish", "heritage_brythonic");
    private static readonly Type Mapper = typeof(HomePage).Assembly.GetType("CrusaderWars.unit_mapper.UnitMappers_BETA")!;
    private static readonly MethodInfo Planner = typeof(StatFidelitySession).GetMethod("PlanKnightBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly string previous = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-own-levy-anchor-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private readonly Dictionary<string, string> oldOptions;
    private readonly Dictionary<(string, bool), (string, bool)> oldManual = BattleState.ManualUnitReplacements;
    private readonly Dictionary<string, (string, bool)> oldAutofix = new(BattleProcessor.AutofixReplacements);
    private readonly List<(FieldInfo Field, object? Value)> caches = new();
    private readonly Army army;
    private readonly Unit levy;
    private readonly Unit bow;

    public KnightLevyAnchorTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data"));
        Environment.CurrentDirectory = scratch;
        oldOptions = new(options); options["StatFidelityMode"] = "Enabled";
        BattleState.ManualUnitReplacements = new(); BattleProcessor.AutofixReplacements.Clear();
        foreach (string name in new[] { "_factionFileCache", "_levyCache", "_garrisonCache" })
        {
            var field = Mapper.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
            caches.Add((field, field.GetValue(null)));
        }
        Mapper.GetField("_factionFileCache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, new Dictionary<string, XmlDocument>());
        SetPool((33, "cavalry"), (34, "precursor_levy"), (33, "skirmisher"));
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\n" +
            "CCSTAT|v1|COMMANDER|attacker|33624|106|0.05\nCCSTAT|v1|COMMANDER|defender|32635|26|0.05\n" +
            "CCSTAT|v1|LEVY|defender|32635|10|10.6|10|10\n" +
            "CCSTAT|v1|REGIMENT|defender|none|13737|60671|source|none|25|9.9|0|0|25|10\n" +
            "CCSTAT|v1|END|snapshot-1");
        StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n32635={\n variables={data={ {flag=ccstat_snapshot_serial data={type=value identity=100000}} {flag=ccstat_combat data={type=com}} }}\n}\n}"), "32635", "0");
        var db = new AttilaDatabase();
        Add(db, "main_units_tables", ["unit", "land_unit", "num_men", "is_naval"],
            ["precursor_levy", "precursor_levy", "80", "false"], ["other_foot", "other_foot", "80", "false"],
            ["cavalry", "cavalry", "80", "false"], ["skirmisher", "skirmisher", "80", "false"],
            ["bowmen", "bowmen", "80", "false"], ["guard", "guard", "80", "false"]);
        Add(db, "land_units_tables", ["key", "man_entity", "primary_melee_weapon", "category", "bonus_hit_points", "primary_missile_weapon", "mount", "num_mounts"],
            ["precursor_levy", "man", "spear", "inf_melee", "0", "precursor", "", "0"],
            ["other_foot", "man", "spear", "inf_melee", "20", "", "", "0"],
            ["cavalry", "man", "spear", "cavalry", "0", "javelin", "horse", "80"],
            ["skirmisher", "man", "spear", "inf_ranged", "0", "javelin", "", "0"],
            ["bowmen", "man", "spear", "inf_ranged", "0", "bow", "", "0"],
            ["guard", "man", "spear", "inf_melee", "0", "", "", "0"]);
        Add(db, "battle_entities_tables", ["key", "hit_points"], ["man", "100"], ["horse_entity", "125"]);
        Add(db, "mounts_tables", ["key", "entity"], ["horse", "horse_entity"]);
        Add(db, "melee_weapons_tables", ["key", "damage", "ap_damage"], ["spear", "6", "4"]);
        var prepared = (PreparedAttilaDatabase)typeof(PreparedAttilaDatabase).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke(new object[] { db, Array.Empty<string>(), (Action)(() => { }) });
        typeof(StatFidelitySession).GetProperty(nameof(StatFidelitySession.PreparedDatabase))!.SetValue(null, prepared);
        ArmyProportions.SetRatio(100);
        army = new Army("16777228", "defender", false); army.SetOwner(new Owner("32635", Culture));
        levy = new Unit("Levy", 477, Culture, RegimentType.Levy, true, army.Owner); levy.SetAttilaFaction("Briton"); levy.SetMax(500);
        bow = new Unit("bowmen", 358, Culture, RegimentType.MenAtArms, true, army.Owner);
        bow.SetUnitKey("bowmen"); bow.SetSourceIdentity(army.ID, "82", "13737", "0", "60671", "none");
        army.SetUnits(new() { levy, bow });
    }

    [Fact]
    public void UnmappedMercenaryLevyCanUseOwnMeleePrimaryPoolBeforeAnyUnitEmission()
    {
        int[] ids = army.Units.Select(u => u.UniqueID).ToArray();
        var before = new Unit("marker", 1, Culture, RegimentType.Levy);
        var plan = Plan(); var repeat = Plan();
        var after = new Unit("marker", 1, Culture, RegimentType.Levy);
        Assert.Equal(before.UniqueID + 1, after.UniqueID); // Planning allocated no Unit identities.
        Assert.Equal(ids, army.Units.Select(u => u.UniqueID));
        Assert.Equal("", levy.GetAttilaUnitKey());
        Assert.Equal(477, levy.GetSoldiers()); Assert.Equal(358, bow.GetSoldiers());
        Assert.Equal("60671", bow.SourceOwnerId); Assert.Equal("13737", bow.SourceRegimentId);
        Assert.Equal("levy-pool:defender:16777228:32635:Briton", plan.Anchor.SourceIdentity);
        Assert.Equal("precursor_levy", plan.Anchor.UnitKey);
        Assert.Equal(10, plan.Anchor.Ck3Damage); Assert.Equal(10.6, plan.Anchor.Ck3Toughness);
        Assert.Equal(10, plan.Anchor.AttilaDamageBeforeCommander); Assert.Equal(106, plan.Anchor.AttilaHitPoints);
        Assert.Equal(1365, plan.Ck3Damage); Assert.Equal(273, plan.Ck3Toughness);
        Assert.Equal(plan.Anchor, repeat.Anchor); Assert.Equal(plan.Bodies, repeat.Bodies);
    }

    [Fact]
    public void MercenaryCompanyOrOtherArmyOwnerDoesNotReplaceVerifiedNativeArmyOwner()
    {
        army.SetOwner(new Owner("60671", Culture));
        AssertMissingAnchor();
        army.SetOwner(new Owner("99999", Culture));
        AssertMissingAnchor();
        army.SetOwner(new Owner("32635", Culture)); army.SetIsGarrison(true);
        AssertMissingAnchor();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RangedOnlyOrZeroWeightFootPoolCannotBorrowAnotherReference(bool zeroWeightFoot)
    {
        SetPool((50, "cavalry"), (50, "skirmisher"), (zeroWeightFoot ? 0 : -1, "precursor_levy"));
        AssertMissingAnchor(); // A valid foot record elsewhere in the DB is not sufficient.
    }

    [Fact]
    public void PoolReferenceHonorsManualAutofixSideAndSiegeFiltering()
    {
        levy.SetUnitKey("precursor_levy"); // A previous generation must not bypass the effective pool.
        BattleProcessor.AutofixReplacements["precursor_levy"] = ("skirmisher", false);
        AssertMissingAnchor();
        BattleState.ManualUnitReplacements[("precursor_levy", army.IsPlayer())] = ("other_foot", false);
        Assert.Equal("other_foot", Plan().Anchor.UnitKey); // Manual replacement wins over autofix.
        options["SiegeEnginesInFieldBattles"] = "Disabled";
        BattleState.ManualUnitReplacements[("precursor_levy", army.IsPlayer())] = ("other_foot", true);
        AssertMissingAnchor();
    }

    [Fact]
    public void DeterministicReferenceUsesPositiveMappedWeightWithoutChangingComposition()
    {
        SetPool((30, "precursor_levy"), (70, "other_foot"));
        Assert.Equal("other_foot", Plan().Anchor.UnitKey);
        levy.SetUnitKey("precursor_levy"); // Model a lower-weight member drawn for actual deployment.
        Assert.Equal("other_foot", Plan().Anchor.UnitKey);
        SetPool((70, "other_foot"), (30, "precursor_levy"));
        Assert.Equal("other_foot", Plan().Anchor.UnitKey);
        Assert.Equal(477, levy.GetSoldiers()); Assert.Equal("precursor_levy", levy.GetAttilaUnitKey());
    }

    [Fact]
    public void AuthorDefaultPoolFallbackIsIdentifiedAsDefaultInProvenance()
    {
        Mapper.GetField("_levyCache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null,
            new Dictionary<string, (List<(int, string, string, string)>, string)> {
                ["Default"] = (new() { (100, "precursor_levy", "Foot levy", "500") }, "Default")
            });
        Assert.Equal("levy-pool:defender:16777228:32635:Default", Plan().Anchor.SourceIdentity);
        Assert.Equal("Briton", levy.GetAttilaFaction());
    }

    [Fact]
    public void AlreadyMappedMeleePrimaryLevyWithPrecursorRetainsItsPoolReference()
    {
        levy.SetUnitKey("precursor_levy");
        Assert.Equal("levy-pool:defender:16777228:32635:Briton", Plan().Anchor.SourceIdentity);
        Assert.Equal("precursor_levy", Plan().Anchor.UnitKey);
    }

    private KnightBudgetPlan Plan() => (KnightBudgetPlan)Planner.Invoke(null, new object[] { army, "guard", new[] {
        new KnightStats {CharacterId="60674",Prowess=6,Effectiveness=1.95,DamagePerProwess=50,ToughnessPerProwess=10},
        new KnightStats {CharacterId="60672",Prowess=8,Effectiveness=1.95,DamagePerProwess=50,ToughnessPerProwess=10}
    } })!;
    private void AssertMissingAnchor()
    {
        var error = Assert.Throws<TargetInvocationException>(() => Plan());
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("No verified foot-melee", error.InnerException!.Message);
    }
    private static void SetPool(params (int Percentage, string Key)[] entries)
    {
        var pool = entries.Select(e => (e.Percentage, e.Key, e.Key, "500")).ToList();
        Mapper.GetField("_levyCache", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null,
            new Dictionary<string, (List<(int, string, string, string)>, string)> { ["Briton"] = (pool, "Briton") });
    }
    private static void Add(AttilaDatabase db, string name, string[] columns, params string[][] rows)
    {
        var table = new TsvTable(name, "1", columns);
        foreach (var row in rows) table.Rows.Add(columns.Select((column, index) => (column, row[index])).ToDictionary(v => v.column, v => v.Item2));
        db.Merge(table);
    }
    public void Dispose()
    {
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin("");
        options.Clear(); foreach (var pair in oldOptions) options.Add(pair.Key, pair.Value);
        BattleState.ManualUnitReplacements = oldManual;
        BattleProcessor.AutofixReplacements.Clear(); foreach (var pair in oldAutofix) BattleProcessor.AutofixReplacements.Add(pair.Key, pair.Value);
        foreach (var cache in caches) cache.Field.SetValue(null, cache.Value);
        Environment.CurrentDirectory = previous;
        if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
    }
}
