using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

public sealed class LevyCohortScalingTests : IDisposable
{
    private readonly string previous = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-levy-cohort-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private readonly Dictionary<string, string> oldOptions;
    private readonly FieldInfo scripts = typeof(Army).Assembly.GetType("CrusaderWars.Data")!.GetField("units_scripts", BindingFlags.Public | BindingFlags.Static)!;
    private readonly object oldScripts;
    private readonly Army army = new("1", "attacker", true);
    private readonly Culture culture = new("7", "test", "test");
    private readonly List<(Unit Unit, string Script)> units = new();

    public LevyCohortScalingTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data")); Environment.CurrentDirectory = scratch;
        oldOptions = new(options); options["StatFidelityMode"] = "Enabled"; options["BattleScale"] = "100%"; oldScripts = scripts.GetValue(null)!;
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|none|0|0.05\nCCSTAT|v1|LEVY|attacker|42|10|10|10|10\nCCSTAT|v1|END|snapshot-1");
        StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={data={ {flag=ccstat_snapshot_serial data={type=value identity=100000}} {flag=ccstat_combat data={type=com}} }}\n}\n}"), "42", "0");
        army.SetOwner(new Owner("42", culture)); army.SetUnits(new()); army.SetArmyRegiments(new());
    }

    [Theory]
    [InlineData(25)] [InlineData(50)] [InlineData(75)] [InlineData(100)]
    public void RawCohortSurvivalProjectsOnceToNativeSourcesAndReportPursuit(int scale)
    {
        options["BattleScale"] = scale + "%"; ArmyProportions.SetRatio(scale);
        var a = Source("10", "100", 61); var b = Source("11", "101", 40);
        int deployed = (int)Math.Round(101 * scale / 100.0);
        Emit(deployed / 2); Emit(deployed - deployed / 2); Bind();
        int[] main = units.Select(u => (int)Math.Round(u.Unit.GetSoldiers() * 0.5)).ToArray();
        int[] final = units.Select(u => (int)Math.Round(u.Unit.GetSoldiers() * 0.25)).ToArray();
        int expectedMain = (int)Math.Round(101m * main.Sum() / deployed, MidpointRounding.AwayFromZero);
        int expectedFinal = (int)Math.Round(101m * final.Sum() / deployed, MidpointRounding.AwayFromZero);
        Consume(main, final);
        Assert.Equal(main.Select(n => n.ToString()), army.UnitsResults!.Alive_MainPhase.Select(r => r.Remaining));
        Assert.Equal(deployed, army.CasualitiesReports.Sum(r => r.GetStarting()));
        Assert.Equal(expectedFinal, int.Parse(a.CurrentNum) + int.Parse(b.CurrentNum));
        Assert.InRange(int.Parse(a.CurrentNum), 0, 61); Assert.InRange(int.Parse(b.CurrentNum), 0, 40);
        var evidence = BattleReportEvidence.Validate(StatFidelitySession.DeployedRoster,
            new[] {Block(main, false), Block(final, false)}, new[] {Block(Enumerable.Repeat(3, units.Count).ToArray(), true), Block(Enumerable.Repeat(5, units.Count).ToArray(), true)});
        var update = Assert.Single(CombatResultStatistics.Build(new[] {army}, Array.Empty<Army>(), StatFidelitySession.DeployedRoster, evidence).Updates);
        Assert.Equal(expectedFinal, update.FinalCount); Assert.Equal(expectedMain - expectedFinal, update.PursuitLosses);
        Assert.Equal(6, update.MainKills); Assert.Equal(4, update.PursuitKills);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(76, 101)] [InlineData(38, 51)]
    public void SeventyFivePercent101TroopCohortHasNoRoundingSurvivorsOrResurrection(int remaining, int expected)
    {
        options["BattleScale"] = "75%"; ArmyProportions.SetRatio(75);
        var source = Source("10", "100", 101); Emit(76); Bind(); Consume(new[] {remaining}, new[] {remaining});
        Assert.Equal(expected.ToString(), source.CurrentNum); Assert.Equal("111", source.Max);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Maa101At75PercentReturns51From38SurvivorsRegardlessOfFormationSplit(bool split)
    {
        options["BattleScale"] = "75%"; ArmyProportions.SetRatio(75);
        var source = MaaSource();
        if (split) { EmitMaa(38); EmitMaa(38); } else EmitMaa(76);
        Bind(); Consume(split ? new[] {19, 19} : new[] {38}, split ? new[] {19, 19} : new[] {38});
        Assert.Equal("51", source.CurrentNum); Assert.Equal(51, army.ArmyRegiments[0].CurrentNum);
        Assert.Equal(76, army.CasualitiesReports.Sum(r => r.GetStarting()));
    }

    [Theory]
    [InlineData(25)] [InlineData(50)] [InlineData(75)] [InlineData(100)]
    public void MaaRawSurvivalAndPursuitUseNativeCountsAtEveryScale(int scale)
    {
        options["BattleScale"] = scale + "%"; ArmyProportions.SetRatio(scale);
        var source = MaaSource(); int deployed = (int)Math.Round(101 * scale / 100.0); EmitMaa(deployed); Bind();
        int main = deployed / 2, final = deployed / 4;
        Consume(new[] {main}, new[] {final});
        int expectedMain = (int)Math.Round(101m * main / deployed, MidpointRounding.AwayFromZero);
        int expectedFinal = (int)Math.Round(101m * final / deployed, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedFinal.ToString(), source.CurrentNum);
        var reports = BattleReportEvidence.Validate(StatFidelitySession.DeployedRoster, new[] {Block(new[] {main}, false), Block(new[] {final}, false)},
            new[] {Block(new[] {3}, true), Block(new[] {5}, true)});
        var update = Assert.Single(CombatResultStatistics.Build(new[] {army}, Array.Empty<Army>(), StatFidelitySession.DeployedRoster, reports).Updates);
        Assert.Equal(expectedMain - expectedFinal, update.PursuitLosses); Assert.Equal(expectedFinal, update.FinalCount);
        Assert.Equal(3, update.MainKills); Assert.Equal(2, update.PursuitKills);
    }

    [Fact]
    public void PositiveNativeCohortMissingFromGenerationRejectsInsteadOfDisappearing()
    {
        Source("10", "100", 101);
        Assert.Throws<InvalidOperationException>(() => StatFidelitySession.RegisterLevyCohorts(army));
        Emit(101); StatFidelitySession.RegisterLevyCohorts(army);
        army.Units.Clear();
        Assert.Throws<InvalidOperationException>(() => StatFidelitySession.ValidateLevyCohortDeployment(army));
    }

    [Fact]
    public void InitialPartialMissingSourceCannotHideBehindAnotherPositiveLevyOfSameCulture()
    {
        Source("10", "100", 61); Source("11", "101", 40); Emit(61);
        var error = Assert.Throws<InvalidOperationException>(() => StatFidelitySession.RegisterLevyCohorts(army, verifyOriginalStrength: true));
        Assert.Contains("native 101, represented original 61", error.Message);
        Assert.Empty(StatFidelitySession.LevyCohorts);
    }

    [Fact]
    public void ProvenNativeStrengthSurvivesCompositionButRejectsLostBodiesOrChangedScaleOnRegeneration()
    {
        Source("10", "100", 101); Emit(101); units[0].Unit.ChangeSoldiers(76); ArmyProportions.SetRatio(75);
        StatFidelitySession.RegisterLevyCohorts(army, verifyOriginalStrength: true);
        StatFidelitySession.ResetGeneration();
        army.Units.Clear(); units.Clear(); Emit(76); // composed formation now stores its already-scaled starting body count
        StatFidelitySession.RegisterLevyCohorts(army, verifyOriginalStrength: true);
        Assert.Equal(101, Assert.Single(StatFidelitySession.LevyCohorts).NativeSoldiers);
        StatFidelitySession.ResetGeneration(); units[0].Unit.SetInitialSoldiers(75);
        Assert.Throws<InvalidOperationException>(() => StatFidelitySession.RegisterLevyCohorts(army, verifyOriginalStrength: true));
        units[0].Unit.SetInitialSoldiers(76); ArmyProportions.SetRatio(50);
        Assert.Throws<InvalidOperationException>(() => StatFidelitySession.RegisterLevyCohorts(army, verifyOriginalStrength: true));
    }

    [Fact]
    public void MaaRegenerationCannotHideAMissingSplitBehindAnotherSplitOfTheSameSource()
    {
        MaaSource(); EmitMaa(50); EmitMaa(51); foreach (var item in units) item.Unit.ChangeSoldiers(38); ArmyProportions.SetRatio(75);
        StatFidelitySession.RegisterMaaSourceCohorts(army, verifyOriginalStrength: true);
        StatFidelitySession.ResetGeneration(); foreach (var item in units) item.Unit.SetInitialSoldiers(38);
        StatFidelitySession.RegisterMaaSourceCohorts(army, verifyOriginalStrength: true);
        Assert.Equal(101, Assert.Single(StatFidelitySession.MaaSourceCohorts).NativeSoldiers);
        StatFidelitySession.ResetGeneration(); army.Units.Remove(units[1].Unit);
        Assert.Throws<InvalidOperationException>(() => StatFidelitySession.RegisterMaaSourceCohorts(army, verifyOriginalStrength: true));
    }

    [Fact]
    public void NativeEquipmentIsNotRegisteredAsOrdinaryMaaWhenItsInheritedMapperFlagIsFalse()
    {
        var field = typeof(ArmiesReader).GetField("nativeMaaDefinitions", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousDefinitions = field.GetValue(null);
        try
        {
            field.SetValue(null, new Dictionary<string, NativeMaaDefinition> { ["onager"] = new("onager", "siege_weapon", false, "fixture") });
            MaaSource(); army.ArmyRegiments[0].SetType(RegimentType.MenAtArms, "onager"); EmitMaa(101); units[0].Unit.ChangeName("onager");
            Assert.False(units[0].Unit.IsSiege());
            StatFidelitySession.RegisterMaaSourceCohorts(army, verifyOriginalStrength: true);
            Assert.Empty(StatFidelitySession.MaaSourceCohorts);
            StatFidelitySession.RegisterSiegeSource(army, units[0].Unit); army.Units.Clear();
            StatFidelitySession.ValidateMaaSourceDeployment(army);
            Assert.Empty(StatFidelitySession.MaaSourceCohorts); Assert.Equal(101, Assert.Single(StatFidelitySession.SiegeSources).Value.OriginalMachines);
        }
        finally { field.SetValue(null, previousDefinitions); }
    }

    [Fact]
    public void EmptyAuthoredPoolRejectsBeforeAnyBattleEmission()
    {
        Source("10", "100", 20); Emit(20);
        var method = typeof(UnitsFile).GetMethod("BETA_LevyComposition", BindingFlags.NonPublic | BindingFlags.Static)!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] {
            units[0].Unit, army, new List<(int, string, string, string)>(), 0, "test", 0 }));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("no authored composition", error.InnerException!.Message);
    }

    [Fact]
    public void FidelityCultureGroupingUsesNativeIdentityInsteadOfSharedDisplayName()
    {
        var other = new Culture("8", "test", "test");
        var method = typeof(ArmiesReader).Assembly.GetType("CrusaderWars.data.save_file.Armies_Functions")!.GetMethod("OrganizeUnitsIntoCultures", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (List<Unit>)method.Invoke(null, new object[] {new List<Unit> {new("Levy", 10, culture, RegimentType.Levy), new("Levy", 20, other, RegimentType.Levy)}, army.Owner!})!;
        Assert.Equal(new[] {"7", "8"}, result.Select(u => u.GetObjCulture()!.ID).OrderBy(x => x));
        Assert.Equal(30, result.Sum(u => u.GetSoldiers()));
    }

    [Fact]
    public void MissingIndependentGarrisonStatsAreExplicitlyUnsupported()
    {
        var unit = new Unit("Garrison", 100, culture, RegimentType.Garrison);
        var error = Assert.Throws<InvalidOperationException>(() => StatFidelitySession.Apply(unit, army, "garrison"));
        Assert.Contains("garrison combat stats", error.Message);
    }

    [Fact]
    public void MissingCultureNeverTransfersSoldiersAcrossNativeRolesOrIdentities()
    {
        var unit = new Unit("spears", 10, null!, RegimentType.MenAtArms); unit.SetSourceIdentity("1", "10", "100", "0", "99", "none"); army.Units.Add(unit);
        string? identity = unit.SourceIdentity;
        StatFidelitySession.ValidateNativeUnitCultures(army);
        Assert.Same(culture, unit.GetObjCulture()); Assert.Equal(identity, unit.SourceIdentity); Assert.Equal(10, unit.GetSoldiers());
        var unknown = new Unit("bows", 12, null!, RegimentType.MenAtArms); army.Units.Add(unknown); army.SetOwner(new Owner("42"));
        Assert.Throws<InvalidOperationException>(() => StatFidelitySession.ValidateNativeUnitCultures(army));
        Assert.Equal(10, unit.GetSoldiers()); Assert.Equal(12, unknown.GetSoldiers()); Assert.Equal(2, army.Units.Count);
    }

    private Regiment Source(string armyRegiment, string id, int count)
    {
        var source = new Regiment(id, "0"); source.SetSoldiers(count.ToString()); source.SetMax((count + 10).ToString()); source.SetCulture("7"); source.SetOwner("42");
        var regiment = new ArmyRegiment(armyRegiment) {CurrentNum = count}; regiment.SetType(RegimentType.Levy, ""); regiment.SetRegiments(new() {source}); army.ArmyRegiments.Add(regiment); return source;
    }
    private Regiment MaaSource()
    {
        var source = new Regiment("100", "0"); source.SetSoldiers("101"); source.SetMax("120"); source.SetCulture("7"); source.SetOwner("42");
        var regiment = new ArmyRegiment("10") {CurrentNum = 101}; regiment.SetType(RegimentType.MenAtArms, "archers"); regiment.SetRegiments(new() {source}); army.ArmyRegiments.Add(regiment); return source;
    }
    private void EmitMaa(int bodies)
    {
        var unit = new Unit("archers", bodies, culture, RegimentType.MenAtArms); unit.SetUnitKey("archers"); unit.SetSourceIdentity("1", "10", "100", "0", "42", "none"); army.Units.Add(unit);
        units.Add((unit, "0_attacker_army1_TYPEarchers_" + unit.UniqueID + "_CULTURE7_0"));
    }
    private void Emit(int bodies)
    {
        var unit = new Unit("Levy", bodies, culture, RegimentType.Levy); unit.SetUnitKey("levy"); unit.SetAttilaFaction("test"); unit.SetMax(500); army.Units.Add(unit);
        units.Add((unit, "0_attacker_army1_TYPELevy_" + unit.UniqueID + "_CULTURE7_0"));
    }
    private void Bind()
    {
        scripts.SetValue(null, units.Select(u => (u.Script, "declaration")).ToList());
        new XDocument(new XElement("battle", units.Select(u => new XElement("unit", new XAttribute("script_name", u.Script), new XAttribute("num_soldiers", u.Unit.GetSoldiers()), new XElement("unit_type", new XAttribute("type", "levy")))))).Save("deployment.xml");
        StatFidelitySession.CaptureDeployedRoster(new[] {army}, "deployment.xml");
    }
    private string Block(int[] counts, bool kills) => string.Join("\n", units.Select((u, i) => (kills ? "kills_" : "") + u.Script + "-" + counts[i]));
    private void Consume(int[] main, int[] final)
    {
        army.UnitsResults = new();
        army.UnitsResults.SetAliveMainPhase(units.Select((u, i) => (u.Script, u.Unit.GetName() + "_" + u.Unit.UniqueID, "7", main[i].ToString())).ToList());
        army.UnitsResults.SetAlivePursuitPhase(units.Select((u, i) => (u.Script, u.Unit.GetName() + "_" + u.Unit.UniqueID, "7", final[i].ToString())).ToList());
        army.UnitsResults.SetKillsMainPhase(units.Select(u => ("kills_" + u.Script, u.Unit.GetName() + "_" + u.Unit.UniqueID, "7", "3")).ToList());
        army.UnitsResults.SetKillsPursuitPhase(units.Select(u => ("kills_" + u.Script, u.Unit.GetName() + "_" + u.Unit.UniqueID, "7", "5")).ToList());
        army.UnitsResults.ScaleTo100Porcent();
        foreach (string method in new[] {"CreateUnitsReports", "ChangeRegimentsSoldiers"})
            typeof(CasualtyProcessor).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] {army});
    }
    public void Dispose()
    {
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin(""); options.Clear(); foreach (var pair in oldOptions) options.Add(pair.Key, pair.Value);
        scripts.SetValue(null, oldScripts); Environment.CurrentDirectory = previous;
        if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
    }
}
