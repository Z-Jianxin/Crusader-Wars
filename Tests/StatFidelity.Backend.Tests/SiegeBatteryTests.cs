using CrusaderWars.StatFidelity;
using Xunit;

public sealed class SiegeBatteryTests
{
    private static readonly AttilaSiegeUnitStats Native = new("battery", "battery_land", "engine", 40, 4);

    [Fact]
    public void ThreeTwentyMachineSourcesKeepAllSixtyMachinesInFifteenFullBatteries()
    {
        var sources = Enumerable.Range(0, 3).Select(_ => SiegeBatteryPlanner.Plan(20, 20, Native)).ToArray();
        Assert.All(sources, source => {
            Assert.Equal(5, source.Count);
            Assert.Equal(20, source.Sum(b => b.SourceMachines));
            Assert.All(source, b => Assert.Equal(new SiegeBattery(4, 4, 40), b));
        });
        Assert.Equal(60, sources.SelectMany(s => s).Sum(b => b.AttilaEngines));
        // The old arbitrary20-battery cap must never discard the tail of a source.
        var larger = SiegeBatteryPlanner.Plan(100, 100, Native);
        Assert.Equal(25, larger.Count);
        Assert.Equal(100, larger.Sum(b => b.SourceMachines));
    }

    [Fact]
    public void PartialBatteryUsesActiveTenCrewPerEngineRatherThanLegacyFour()
    {
        var batteries = SiegeBatteryPlanner.Plan(10, 10, Native);
        Assert.Equal(new[] { 4, 4, 2 }, batteries.Select(b => b.AttilaEngines));
        Assert.Equal(new[] { 40, 40, 20 }, batteries.Select(b => b.Crew));
        Assert.Equal(new[] { 4, 4, 2 }, batteries.Select(b => b.SourceMachines));
    }

    [Fact]
    public void BattlefieldScalingKeepsExactNativeSourceBudgetWithStableRemainders()
    {
        Assert.Equal(new[] { 8, 8, 4 }, SiegeBatteryPlanner.Plan(20, 10, Native).Select(b => b.SourceMachines));
        Assert.Equal(new[] { 7, 7, 3 }, SiegeBatteryPlanner.Plan(17, 10, Native).Select(b => b.SourceMachines));
        var large = SiegeBatteryPlanner.Plan(int.MaxValue, 4, Native);
        Assert.Equal(int.MaxValue, Assert.Single(large).SourceMachines);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(-1, 1)]
    [InlineData(16385, 16385)]
    public void InvalidOrUnreportablePlansFailWithoutTruncation(int source, int scaled)
    {
        Assert.Throws<InvalidDataException>(() => SiegeBatteryPlanner.Plan(source, scaled, Native));
    }

    [Fact]
    public void ActiveQueryRejectsInfantryProxiesAndGuessedCrewRatios()
    {
        var database = Fixture();
        var prepared = new PreparedAttilaDatabase(database, [], () => { });
        Assert.Equal(Native, prepared.QuerySiegeUnit("battery"));
        var land = database.Require("land_units_tables", "battery_land");
        land["category"] = "inf_melee";
        Assert.Throws<InvalidDataException>(() => prepared.QuerySiegeUnit("battery"));
        land["category"] = "artillery";
        land["num_guns"] = "3";
        Assert.Throws<InvalidDataException>(() => prepared.QuerySiegeUnit("battery"));
        land["num_guns"] = "4";
        land["engine"] = "";
        Assert.Throws<InvalidDataException>(() => prepared.QuerySiegeUnit("battery"));
    }

    [Fact]
    public void PrivatePartialBatteryChangesOnlyCountFieldsAndKeepsSharedStatsUntouched()
    {
        var database = Fixture();
        var builder = new StatVariantBuilder(database);
        string key = builder.Add(new("source:partial", "battery", 1, 1, 2, 20));
        var main = Assert.Single(builder.Tables.Single(t => t.Name == "main_units_tables").Rows);
        var land = Assert.Single(builder.Tables.Single(t => t.Name == "land_units_tables").Rows);
        Assert.Equal("20", main["num_men"]);
        Assert.Equal("2", land["num_guns"]);
        Assert.Equal(key, main["land_unit"]);
        Assert.Equal("9", land["ammo"]);
        Assert.Equal("100", Assert.Single(builder.Tables.Single(t => t.Name == "battle_entities_tables").Rows)["hit_points"]);
        Assert.Equal("5", Assert.Single(builder.Tables.Single(t => t.Name == "melee_weapons_tables").Rows)["damage"]);
        Assert.Equal("native_mesh", Assert.Single(builder.Tables.Single(t => t.Name == "unit_variants_tables").Rows)["variant"]);
        Assert.Equal("40", database.Require("main_units_tables", "battery")["num_men"]);
        Assert.Equal("4", database.Require("land_units_tables", "battery_land")["num_guns"]);
        Assert.Equal("engine", database.Require("land_units_tables", "battery_land")["engine"]);
    }

    [Fact]
    public void InvalidPartialProfilesCannotInventLargerBatteriesOrNewCombatStats()
    {
        foreach (var profile in new[] {
            new UnitStatProfile("a", "battery", 1, 1, 2, 19),
            new UnitStatProfile("b", "battery", 1, 1, 5, 50),
            new UnitStatProfile("c", "battery", 2, 1, 2, 20),
            new UnitStatProfile("d", "battery", 1, 2, 2, 20),
            new UnitStatProfile("e", "battery", 1, 1, 2),
            new UnitStatProfile("f", "battery", 1, 1, null, 20)
        }) Assert.Throws<InvalidOperationException>(() => new StatVariantBuilder(Fixture()).Add(profile));
        Assert.NotEqual(StatFidelityBackend.GeneratedKey(new("same", "battery", 1, 1, 1, 10)),
            StatFidelityBackend.GeneratedKey(new("same", "battery", 1, 1, 2, 20)));
    }

    private static AttilaDatabase Fixture()
    {
        var database = new AttilaDatabase();
        void Add(string name, string[] columns, params string[][] rows)
        {
            var table = new TsvTable(name, "1", columns);
            foreach (var row in rows) table.Rows.Add(columns.Select((c, i) => (c, row[i])).ToDictionary(p => p.c, p => p.Item2));
            database.Merge(table);
        }
        Add("main_units_tables", ["unit", "land_unit", "num_men", "is_naval"], ["battery", "battery_land", "40", "false"]);
        Add("land_units_tables", ["key", "category", "num_guns", "engine", "man_entity", "bonus_hit_points", "primary_melee_weapon", "ammo"],
            ["battery_land", "artillery", "4", "engine", "crew", "0", "sword", "9"]);
        Add("battlefield_engines_tables", ["key", "missile_weapon", "engine_type"], ["engine", "", "Rome_Cheiroballistra"]);
        Add("battle_entities_tables", ["key", "hit_points"], ["crew", "100"]);
        Add("melee_weapons_tables", ["key", "damage", "ap_damage"], ["sword", "5", "1"]);
        Add("unit_variants_tables", ["unit", "faction", "variant"], ["battery_land", "", "native_mesh"]);
        Add("Loc", ["key", "text", "tooltip"], ["land_units_onscreen_name_battery_land", "Native artillery", "true"]);
        return database;
    }
}
