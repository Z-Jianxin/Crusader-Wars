using System.Globalization;
using System.Reflection;
using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using CrusaderWars.unit_mapper;
using Xunit;

public sealed class NativePopulationDeploymentTests : IDisposable
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly string previousDirectory = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-population-deployment-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string,string> options = (Dictionary<string,string>)typeof(ModOptions).GetField("optionsValuesCollection", Static)!.GetValue(null)!;
    private readonly string? previousMode;
    private readonly int previousScale = ArmyProportions.BattleScale;
    private readonly FieldInfo snapshot = typeof(ArmiesReader).GetField("nativeCombatParticipation", Static)!;
    private readonly FieldInfo definitions = typeof(ArmiesReader).GetField("nativeMaaDefinitions", Static)!;
    private readonly object? previousSnapshot, previousDefinitions;
    private readonly Type mapper = typeof(AvailableUnit).Assembly.GetType("CrusaderWars.unit_mapper.UnitMappers_BETA")!;
    private readonly object? previousTag, previousFolder;
    private readonly Culture culture = new("7", "fixture", "fixture");

    public NativePopulationDeploymentTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data")); Environment.CurrentDirectory = scratch;
        options.TryGetValue("StatFidelityMode", out previousMode); options["StatFidelityMode"] = "Enabled";
        previousSnapshot = snapshot.GetValue(null); previousDefinitions = definitions.GetValue(null);
        previousTag = mapper.GetProperty("ActivePlaythroughTag", Static)!.GetValue(null);
        previousFolder = mapper.GetProperty("LoadedUnitMapper_FolderPath", Static)!.GetValue(null);
        mapper.GetProperty("ActivePlaythroughTag", Static)!.SetValue(null, "DefaultCK3");
        mapper.GetProperty("LoadedUnitMapper_FolderPath", Static)!.SetValue(null, scratch);
        definitions.SetValue(null, new Dictionary<string,NativeMaaDefinition> {
            ["unmapped_elephants"] = new("unmapped_elephants", "elephant_cavalry", true, "fixture.txt"),
            ["onager"] = new("onager", "siege_weapon", false, "fixture.txt")
        });
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|none|0|0.05\nCCSTAT|v1|END|snapshot-1");
        StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={data={ {flag=ccstat_snapshot_serial data={type=value identity=100000}} {flag=ccstat_combat data={type=com}} }}\n}\n}"), "42", "0");
        ArmyProportions.SetRatio(100);
    }

    [Fact]
    public void AllSoftUnsupportedMaaIsRemovedBeforeCultureAndMappingPreflight()
    {
        var army = Army(includeLevy: true); army.Units[0].ChangeCulture(null!);
        Participation(0, 100, includeLevy: true);
        StatFidelitySession.CaptureNativePopulations(new[] { army });
        var inactive = army.Units[0]; var activeLevy = army.Units[1];
        Assert.True(StatFidelitySession.IsInactiveNativePopulationPlaceholder(army, inactive));
        Assert.False(StatFidelitySession.IsInactiveNativePopulationPlaceholder(army, activeLevy));
        var initialMappingInputs = army.Units.Where(u => !StatFidelitySession.IsInactiveNativePopulationPlaceholder(army, u)).ToArray();
        Assert.Same(activeLevy, Assert.Single(initialMappingInputs));
        Assert.Equal(2, army.Units.Count); Assert.Contains(inactive, army.Units);
        Assert.Equal(100, inactive.GetSoldiers()); Assert.Equal(100, inactive.GetOriginalSoldiers());
        Assert.Equal("100", army.ArmyRegiments[0].Regiments[0].CurrentNum);
        Assert.Throws<InvalidOperationException>(() => StatFidelitySession.ValidateNativeUnitCultures(army));
        StatFidelitySession.ApplyNativeActiveDeployment(army);
        var remaining = Assert.Single(army.Units);
        Assert.Equal(RegimentType.Levy, remaining.GetRegimentType()); Assert.Equal(60, remaining.GetSoldiers());
        Assert.Equal(100, army.ArmyRegiments[0].CurrentNum); Assert.Equal("100", army.ArmyRegiments[0].Regiments[0].CurrentNum);
        Assert.Equal(0, StatFidelitySession.NativePopulationDeployments.Single(p => p.Kind == RegimentType.MenAtArms).DeployedBodies);
        StatFidelitySession.ValidateNativeUnitCultures(army);
        // Enable the real semantic preflight with an intentionally empty database: any
        // attempt to resolve the removed unsupported MAA would fail this test.
        var prepared = (PreparedAttilaDatabase)typeof(PreparedAttilaDatabase).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke(new object[] { new AttilaDatabase(), Array.Empty<string>(), (Action)(() => { }) });
        typeof(StatFidelitySession).GetProperty("PreparedDatabase", Static)!.SetValue(null, prepared);
        mapper.GetMethod("RefreshNativeMappings", Static)!.Invoke(null, new object[] { army });
        Assert.Empty(StatFidelitySession.PendingProfiles);
    }

    [Theory]
    [InlineData(100, 80)]
    [InlineData(50, 40)]
    public void FreshIdenticalNativeReadAcceptsStockPlaceholdersAfterPriorDeployment(int scale, int expected)
    {
        ArmyProportions.SetRatio(scale); Participation(80, 20);
        var first = Army(); StatFidelitySession.CaptureNativePopulations(new[] { first });
        StatFidelitySession.ApplyNativeActiveDeployment(first);
        Assert.Equal(expected, Assert.Single(first.Units).GetSoldiers());
        var originalBudget = Assert.Single(StatFidelitySession.NativePopulationBudgets);
        var fresh = Army(); // Production autofix re-reads original source stock into NEW Army instances.
        Assert.Equal(100, Assert.Single(fresh.Units).GetOriginalSoldiers());
        StatFidelitySession.CaptureNativePopulations(new[] { fresh });
        Assert.Same(originalBudget, Assert.Single(StatFidelitySession.NativePopulationBudgets));
        Assert.Empty(StatFidelitySession.NativePopulationDeployments);
        StatFidelitySession.ApplyNativeActiveDeployment(fresh);
        Assert.Equal(expected, Assert.Single(fresh.Units).GetSoldiers());
        Assert.Equal("100", fresh.ArmyRegiments[0].Regiments[0].CurrentNum);
        Assert.Equal(20m, Assert.Single(StatFidelitySession.NativePopulationBudgets).PreservedSoft);
    }

    [Fact]
    public void SameArmyReadCannotClearPlanAndHideMissingGeneratedFormation()
    {
        Participation(80, 20); var army = Army();
        StatFidelitySession.CaptureNativePopulations(new[] { army }); StatFidelitySession.ApplyNativeActiveDeployment(army);
        // Simulate the two emitted cohorts, whose original sizes are their actual bodies.
        army.SetUnits(new() { Maa(40), Maa(40) });
        StatFidelitySession.CaptureNativePopulations(new[] { army });
        StatFidelitySession.ApplyNativeActiveDeployment(army);
        army.Units.RemoveAt(1);
        StatFidelitySession.CaptureNativePopulations(new[] { army });
        Assert.Equal(80, Assert.Single(StatFidelitySession.NativePopulationDeployments).DeployedBodies);
        var error = Assert.Throws<InvalidOperationException>(() => StatFidelitySession.ApplyNativeActiveDeployment(army));
        Assert.Contains("complete prior formations", error.Message);
        Assert.Equal(40, Assert.Single(army.Units).GetSoldiers()); Assert.Equal(100, army.ArmyRegiments[0].CurrentNum);
    }

    [Fact]
    public void EquipmentOnlyReserveArmyCannotAcquireAnEngineOnlyGeneral()
    {
        var army = Army(type: "onager"); Participation(0, 0);
        StatFidelitySession.CaptureNativePopulations(new[] { army });
        Assert.Empty(StatFidelitySession.NativePopulationBudgets); Assert.Single(StatFidelitySession.NativeEquipmentInputs);
        Assert.Throws<InvalidOperationException>(() => StatFidelitySession.ApplyNativeActiveDeployment(army));
        Assert.DoesNotContain(army.Units, u => u.GetRegimentType() == RegimentType.Commander);
        Assert.Equal("100", army.ArmyRegiments[0].Regiments[0].CurrentNum);
    }

    private Unit Maa(int bodies, string type = "unmapped_elephants")
    {
        var unit = new Unit(type, bodies, culture, RegimentType.MenAtArms);
        unit.SetSourceIdentity("1", "10", "100", "0", "42"); unit.SetUnitKey("missing_active_asset"); unit.SetAttilaFaction("unmapped");
        return unit;
    }
    private Army Army(bool includeLevy = false, string type = "unmapped_elephants")
    {
        var army = new Army("1", "attacker", true);
        Regiment Source(string id, int count) { var source = new Regiment(id, "0"); source.SetSoldiers(count.ToString(CultureInfo.InvariantCulture)); source.SetMax(count.ToString(CultureInfo.InvariantCulture)); source.SetCulture("7"); source.SetOwner("42"); return source; }
        var maa = new ArmyRegiment("10") { CurrentNum = 100, StartingNum = 100 }; maa.SetType(RegimentType.MenAtArms, type); maa.SetRegiments(new() { Source("100", 100) });
        army.SetArmyRegiments(new() { maa }); army.SetUnits(new() { Maa(100, type) });
        if (includeLevy) {
            var levy = new ArmyRegiment("20") { CurrentNum = 60, StartingNum = 60 }; levy.SetType(RegimentType.Levy); levy.SetRegiments(new() { Source("200", 60) });
            army.ArmyRegiments.Add(levy); army.Units.Add(new Unit("Levy", 60, culture, RegimentType.Levy));
        }
        return army;
    }
    private void Participation(int active, int soft, bool includeLevy = false) => snapshot.SetValue(null, NativeCombatParticipation.Parse(
        "0={ attacker={ armies={1} men_at_arms={ {regiment=10 starting=100 current=" + active + " soft_casualties=" + soft + "} } " +
        (includeLevy ? "levies={ {regiment=20 starting=60 current=60 soft_casualties=0} } " : "") + "} defender={ armies={2} } }"));

    public void Dispose()
    {
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin("");
        if (previousMode == null) options.Remove("StatFidelityMode"); else options["StatFidelityMode"] = previousMode;
        snapshot.SetValue(null, previousSnapshot); definitions.SetValue(null, previousDefinitions); ArmyProportions.SetRatio(previousScale);
        mapper.GetProperty("ActivePlaythroughTag", Static)!.SetValue(null, previousTag); mapper.GetProperty("LoadedUnitMapper_FolderPath", Static)!.SetValue(null, previousFolder);
        mapper.GetMethod("ClearFactionCache", Static)!.Invoke(null, null);
        Environment.CurrentDirectory = previousDirectory;
        string resolved = Path.GetFullPath(scratch);
        if (Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(resolved, true);
    }
}
