using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

public sealed class SiegeCasualtyAccountingTests : IDisposable
{
    private readonly string previous = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-siege-accounting-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private readonly Dictionary<string, string> oldOptions;
    private readonly FieldInfo scripts = typeof(Army).Assembly.GetType("CrusaderWars.Data")!.GetField("units_scripts", BindingFlags.Public | BindingFlags.Static)!;
    private readonly object oldScripts;
    private readonly Army army = new("1", "attacker", true);
    private readonly List<(Unit Unit, string Script)> emitted = new();
    private static readonly Culture Culture = new("7", "test", "test");

    public SiegeCasualtyAccountingTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data")); Environment.CurrentDirectory = scratch;
        oldOptions = new(options); options["StatFidelityMode"] = "Enabled"; options["BattleScale"] = "100%";
        oldScripts = scripts.GetValue(null)!;
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|none|0|0.05\nCCSTAT|v1|END|snapshot-1");
        StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={data={ {flag=ccstat_snapshot_serial data={type=value identity=100000}} {flag=ccstat_combat data={type=com}} }}\n}\n}"), "42", "0");
        army.SetOwner(new Owner("42", Culture)); army.SetUnits(new()); army.SetArmyRegiments(new());
    }

    [Fact]
    public void WeightedSourceAccountingKeepsPartialBatteriesAndUndeployedEquipmentSeparate()
    {
        Assert.Equal(9, SiegeCasualtyAccounting.Survivors(10, new[] {new SiegeCrewResult(4, 40, 40), new(4, 40, 40), new(2, 20, 9)}));
        Assert.Equal(9, SiegeCasualtyAccounting.Survivors(10, new[] {new SiegeCrewResult(4, 14, 14), new(4, 14, 14), new(2, 7, 3)}));
        Assert.Equal(15, SiegeCasualtyAccounting.Survivors(20, new[] {new SiegeCrewResult(10, 100, 50)}));
        Assert.Equal(20, SiegeCasualtyAccounting.Survivors(20, Array.Empty<SiegeCrewResult>()));
        Assert.Throws<InvalidDataException>(() => SiegeCasualtyAccounting.Survivors(1, new[] {new SiegeCrewResult(2, 20, 20)}));
        Assert.Throws<InvalidDataException>(() => SiegeCasualtyAccounting.Survivors(2, new[] {new SiegeCrewResult(2, 20, 21)}));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(50)]
    public void RawCrewCountsAndKillsAreNotUnscaledAndEquipmentUsesNativeBudget(int scale)
    {
        options["BattleScale"] = scale + "%";
        var (source, prototype) = Source("10", "100", 20);
        if (scale == 50)
        {
            Emit(prototype, 8, 40, 4); Emit(prototype, 8, 40, 4); Emit(prototype, 4, 20, 2);
        }
        else
        {
            Emit(prototype, 4, 40, 4); Emit(prototype, 4, 40, 4); Emit(prototype, 12, 120, 12);
        }
        Bind();
        int[] remaining = scale == 50 ? new[] {20, 40, 10} : new[] {20, 40, 80};
        Consume(remaining);
        Assert.Equal(remaining.Select(n => n.ToString()), army.UnitsResults.Alive_MainPhase.Select(r => r.Remaining));
        Assert.All(army.UnitsResults.Kills_MainPhase, r => Assert.Equal("3", r.Kills));
        Assert.Equal(scale == 50 ? "14" : "14", source.CurrentNum);
        Assert.Equal(emitted.Select(e => e.Unit.SiegeRepresentation!.DeployedBodies), army.CasualitiesReports.Select(r => r.GetStarting()));
    }

    [Fact]
    public void SameTypeSourcesDoNotShareCasualtiesAndOmittedOrCulturelessSourcesSurvive()
    {
        var (first, a) = Source("10", "100", 10);
        var (second, b) = Source("11", "101", 10);
        var (omitted, _) = Source("12", "102", 20);
        typeof(Regiment).GetProperty(nameof(Regiment.Culture))!.SetValue(first, null);
        Emit(a, 10, 100, 10); Emit(b, 10, 100, 10); Bind();
        Consume(new[] {0, 100});
        Assert.Equal("0", first.CurrentNum); Assert.Equal("10", second.CurrentNum); Assert.Equal("20", omitted.CurrentNum);
        Assert.Equal(new[] {0, 10, 20}, army.ArmyRegiments.Select(r => r.CurrentNum));
    }

    [Fact]
    public void MissingOrExcessiveCrewEvidenceAbortsBeforeSourceMutation()
    {
        var (source, prototype) = Source("10", "100", 10);
        Emit(prototype, 10, 100, 10); Bind();
        army.UnitsResults = new UnitsResults();
        Assert.Throws<TargetInvocationException>(() => Invoke("CreateUnitsReports"));
        Assert.Equal("10", source.CurrentNum);
        Assert.Throws<TargetInvocationException>(() => Consume(new[] {101}));
        Assert.Equal("10", source.CurrentNum);
    }

    [Fact]
    public void InvalidatingHandoffClearsSiegeBudgetsAndRawScaleBindings()
    {
        var (_, prototype) = Source("10", "100", 10); Emit(prototype, 10, 100, 10); Bind();
        Assert.True(StatFidelitySession.IsSiegeFormation(emitted[0].Script));
        StatFidelitySession.InvalidateHandoff("test invalidation");
        Assert.Empty(StatFidelitySession.SiegeSources);
        Assert.False(StatFidelitySession.IsSiegeFormation(emitted[0].Script));
    }

    private (Regiment, Unit) Source(string regimentId, string sourceId, int current)
    {
        var source = new Regiment(sourceId, "0"); source.SetSoldiers(current.ToString()); source.SetMax((current + 20).ToString()); source.SetCulture("7"); source.SetOwner("42");
        var regiment = new ArmyRegiment(regimentId) {CurrentNum = current}; regiment.SetType(RegimentType.MenAtArms, "onager"); regiment.SetRegiments(new() {source});
        army.ArmyRegiments.Add(regiment);
        var unit = new Unit("onager", current, Culture, RegimentType.MenAtArms); unit.SetIsSiege(true);
        unit.SetSourceIdentity(army.ID, regimentId, sourceId, "0", "42", "none");
        StatFidelitySession.RegisterSiegeSource(army, unit); return (source, unit);
    }
    private void Emit(Unit prototype, int nativeMachines, int bodies, int engines)
    {
        var unit = new Unit("onager", bodies, Culture, RegimentType.MenAtArms); unit.CopySourceIdentity(prototype); unit.SetIsSiege(true); unit.SetUnitKey("fixture_artillery");
        unit.SiegeRepresentation = new(nativeMachines, bodies, engines, "artillery"); army.Units.Add(unit);
        emitted.Add((unit, "0_attacker_army1_TYPEonager_" + unit.UniqueID + "_CULTURE7_0"));
    }
    private void Bind()
    {
        scripts.SetValue(null, emitted.Select(e => (e.Script, "declaration")).ToList());
        new XDocument(new XElement("battle", emitted.Select(e => new XElement("unit", new XAttribute("script_name", e.Script), new XAttribute("num_soldiers", e.Unit.GetSoldiers()), new XElement("unit_type", new XAttribute("type", e.Unit.GetAttilaUnitKey())))))).Save("deployment.xml");
        StatFidelitySession.CaptureDeployedRoster(new[] {army}, "deployment.xml");
    }
    private void Consume(int[] remaining)
    {
        army.UnitsResults = new UnitsResults();
        army.UnitsResults.SetAliveMainPhase(emitted.Select((e, i) => (e.Script, "onager_" + e.Unit.UniqueID, "7", remaining[i].ToString())).ToList());
        army.UnitsResults.SetKillsMainPhase(emitted.Select(e => ("kills_" + e.Script, "onager_" + e.Unit.UniqueID, "7", "3")).ToList());
        army.UnitsResults.ScaleTo100Porcent(); Invoke("CreateUnitsReports"); Invoke("ChangeRegimentsSoldiers");
    }
    private void Invoke(string name) => typeof(CasualtyProcessor).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] {army});
    public void Dispose()
    {
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin("");
        options.Clear(); foreach (var pair in oldOptions) options.Add(pair.Key, pair.Value);
        scripts.SetValue(null, oldScripts); Environment.CurrentDirectory = previous;
        if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
    }
}
