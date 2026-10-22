using System.Reflection;
using CrusaderWars;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using Xunit;

public sealed class CombatResultStatisticsTests
{
    private static readonly Culture Culture = new("7", "native", "native");

    [Fact]
    public void AlliedTypesAndPooledLeviesAggregateOnceAcrossSourcesSplitsAndMergedReferences()
    {
        var f = new Fixture();
        var attack = new Army("24", "attacker", true);
        var defend = new Army("18", "defender", true);
        var ally = new Army("29", "defender", false);
        var merged = new Army("30", "defender", false);
        var levyA = Regiment(attack, "100", RegimentType.Levy, "Levy", 12);
        Regiment(attack, "102", RegimentType.Levy, "Levy", 5);
        var armorA = Regiment(attack, "101", RegimentType.MenAtArms, "armored_footmen", 8);
        var armorD = Regiment(defend, "201", RegimentType.MenAtArms, "armored_footmen", 7);
        var armorAlly = Regiment(ally, "301", RegimentType.MenAtArms, "armored_footmen", 9);
        var levyMerged = Regiment(merged, "401", RegimentType.Levy, "Levy", 2);
        f.Add(attack, RegimentType.Levy, "mapped_spears", 50, 12, 12, 3, 3);
        f.Add(attack, RegimentType.Levy, "mapped_archers", 50, 5, 5, 4, 4);
        f.Add(attack, RegimentType.MenAtArms, "presentation_name_is_not_native_type", 40, 3, 3, 11, 11, armorA);
        f.Add(attack, RegimentType.MenAtArms, "split", 60, 5, 5, 13, 13, armorA);
        f.Add(defend, RegimentType.MenAtArms, "armor", 100, 7, 7, 17, 17, armorD);
        f.Add(ally, RegimentType.MenAtArms, "armor", 100, 9, 9, 19, 19, armorAlly);
        f.Add(merged, RegimentType.Levy, "Levy", 20, 2, 2, 23, 23);
        defend.AddMergedArmy(merged);
        defend.ArmyRegiments.Add(levyMerged);
        defend.Units.AddRange(merged.Units);
        defend.CasualitiesReports.AddRange(merged.CasualitiesReports);
        var result = CombatResultStatistics.Build([attack], [defend, ally, merged], f.Roster, f.Reports());
        Assert.Equal(4, result.Updates.Count);
        var aLevy = Assert.Single(result.Updates.Where(u => u.Side == "attacker" && u.RegimentType == null));
        Assert.Equal(17, aLevy.FinalCount); Assert.Equal(7, aLevy.MainKills);
        var aArmor = Assert.Single(result.Updates.Where(u => u.Side == "attacker" && u.RegimentType == "armored_footmen"));
        Assert.Equal(8, aArmor.FinalCount); Assert.Equal(24, aArmor.MainKills);
        var dArmor = Assert.Single(result.Updates.Where(u => u.Side == "defender" && u.RegimentType == "armored_footmen"));
        Assert.Equal(16, dArmor.FinalCount); Assert.Equal(36, dArmor.MainKills);
        var dLevy = Assert.Single(result.Updates.Where(u => u.Side == "defender" && u.RegimentType == null));
        Assert.Equal(2, dLevy.FinalCount); Assert.Equal(23, dLevy.MainKills);
        Assert.Equal(12, levyA.CurrentNum);
    }

    [Fact]
    public void RawCreditsStayUnscaledWhileNativePursuitAndFinalCountsRemainSeparate()
    {
        var f = new Fixture(); var army = new Army("1", "attacker", true);
        var regiment = Regiment(army, "10", RegimentType.MenAtArms, "bowmen", 60);
        f.Add(army, RegimentType.MenAtArms, "bows", 50, 40, 30, 5, 9, regiment, normalizedScale: 2);
        army.UnitsResults = new UnitsResults();
        army.UnitsResults.SetKillsMainPhase([("irrelevant_scaled_row", "bows", "7", "10")]);
        var update = Assert.Single(CombatResultStatistics.Build([army], [], f.Roster, f.Reports()).Updates);
        Assert.Equal(5, update.MainKills); Assert.Equal(4, update.PursuitKills);
        Assert.Equal(20, update.PursuitLosses); Assert.Equal(60, update.FinalCount);
    }

    [Fact]
    public void KnightPoolUsesStableModelledSharesAndExplicitlyExcludesGuardCredits()
    {
        var f = new Fixture(); var army = new Army("1", "defender", true);
        Regiment(army, "10", RegimentType.Knight, "101", 1);
        Regiment(army, "11", RegimentType.Knight, "102", 0);
        var a = Knight("101", 4); var b = Knight("102", 6); b.IsPrisoner = true;
        a.SetKills(999); b.SetKills(888); // Deliberately unrelated old random allocations.
        army.SetKnights(new KnightSystem([b, a], 100));
        f.Add(army, RegimentType.Knight, "knights", 10, 4, 0, 9, 13);
        f.Add(army, RegimentType.Commander, "generic_guard", 2, 1, 1, 3, 5);
        var first = CombatResultStatistics.Build([], [army], f.Roster, f.Reports());
        var second = CombatResultStatistics.Build([], [army], f.Roster, f.Reports());
        Assert.Equal(first.Updates, second.Updates);
        var one = first.Updates.Single(u => u.KnightId == "101");
        var two = first.Updates.Single(u => u.KnightId == "102");
        Assert.Equal(4, one.MainKills); Assert.Equal(5, two.MainKills);
        Assert.Equal(2, one.PursuitKills); Assert.Equal(2, two.PursuitKills);
        Assert.Equal(1, one.FinalCount); Assert.Equal(0, two.FinalCount);
        Assert.Contains(first.Diagnostics, d => d.Contains("3 main/2 pursuit") && d.Contains("Excluded"));
        Assert.Contains(first.Diagnostics, d => d.Contains("modeled") && d.Contains("not observed personal kills"));
        Assert.Equal(999, a.GetKills()); Assert.Equal(888, b.GetKills()); Assert.True(b.IsPrisoner);
    }

    [Fact]
    public void DecreasingKillsAndMissingSourceBindingsAbortWithoutClampingOrNameGuessing()
    {
        var f = new Fixture(); var army = new Army("1", "attacker", true);
        var source = Regiment(army, "10", RegimentType.MenAtArms, "bows", 5);
        f.Add(army, RegimentType.MenAtArms, "bows", 10, 8, 5, 10, 9, source);
        Assert.Throws<InvalidDataException>(() => CombatResultStatistics.Build([army], [], f.Roster, f.Reports()));
        var missing = new Fixture(); var missingArmy = new Army("2", "attacker", true);
        Regiment(missingArmy, "20", RegimentType.MenAtArms, "bows", 5);
        missing.Add(missingArmy, RegimentType.MenAtArms, "bows", 10, 5, 5, 1, 1);
        Assert.Throws<InvalidDataException>(() => CombatResultStatistics.Build([missingArmy], [], missing.Roster, missing.Reports()));
    }

    private static ArmyRegiment Regiment(Army army, string id, RegimentType type, string name, int current)
    {
        var regiment = new ArmyRegiment(id) { CurrentNum = current, StartingNum = 100 };
        regiment.SetType(type, name); army.ArmyRegiments.Add(regiment); return regiment;
    }
    private static Knight Knight(string id, int bodies)
    {
        var knight = (Knight)Activator.CreateInstance(typeof(Knight), BindingFlags.NonPublic | BindingFlags.Instance,
            null, ["Knight", id, Culture, 10, bodies], null)!;
        knight.FidelitySoldierCount = bodies; return knight;
    }
    private sealed class Fixture
    {
        public List<DeployedFormation> Roster { get; } = new();
        private readonly List<string> main = new(), final = new(), killsMain = new(), killsFinal = new();
        public void Add(Army army, RegimentType type, string name, int deployed, int before, int after,
            int creditedMain, int creditedFinal, ArmyRegiment? source = null, int normalizedScale = 1)
        {
            var unit = new Unit(name, deployed, Culture, type);
            if (source != null) unit.SetSourceIdentity(army.ID, source.ID, "native" + source.ID, "0", "owner");
            army.Units.Add(unit);
            string script = $"0_{army.CombatSide}_army{army.ID}_TYPE{name}_{unit.UniqueID}_CULTURE_0";
            Roster.Add(new(script, deployed));
            main.Add(script + "-" + before); final.Add(script + "-" + after);
            killsMain.Add("kills_" + script + "-" + creditedMain); killsFinal.Add("kills_" + script + "-" + creditedFinal);
            army.CasualitiesReports.Add(new UnitCasualitiesReport(type, name, Culture, deployed * normalizedScale,
                before * normalizedScale, after * normalizedScale, 0, "native", script));
        }
        public ValidatedBattleReports Reports() => BattleReportEvidence.Validate(Roster,
            [string.Join("\n", main), string.Join("\n", final)], [string.Join("\n", killsMain), string.Join("\n", killsFinal)]);
    }
}
