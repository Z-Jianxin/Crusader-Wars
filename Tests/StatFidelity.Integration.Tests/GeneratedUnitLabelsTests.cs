using System.Reflection;
using System.Xml.Linq;
using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.save_file;
using CrusaderWars.locs;
using CrusaderWars.StatFidelity;
using Xunit;

public sealed class GeneratedUnitLabelsTests : IDisposable
{
    private readonly string previous = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-private-labels-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private readonly Dictionary<string, string> oldOptions;
    private readonly List<string> modRoots = LandedTitles.GetEnabledModsPaths();
    private readonly string[] oldRoots;
    private readonly object settings = typeof(HomePage).Assembly.GetType("CrusaderWars.Properties.Settings")!.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    private readonly PropertyInfo ck3Path;
    private readonly object? oldCk3Path;
    private static readonly Culture Culture = new("7", "test", "test");

    public GeneratedUnitLabelsTests()
    {
        oldOptions = new(options); oldRoots = modRoots.ToArray();
        ck3Path = settings.GetType().GetProperty("VAR_ck3_path")!; oldCk3Path = ck3Path.GetValue(settings);
        Directory.CreateDirectory(scratch); Environment.CurrentDirectory = scratch;
        options["StatFidelityMode"] = "Enabled"; options["UnitCards"] = "Enabled";
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|none|0|0.05\nCCSTAT|v1|END|snapshot-1");
        StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={data={ {flag=ccstat_snapshot_serial data={type=value identity=100000}} {flag=ccstat_combat data={type=com}} }}\n}\n}"), "42", "0");
        ck3Path.SetValue(settings, Path.Combine(scratch, "ck3", "binaries", "ck3.exe")); modRoots.Clear();
        Write("ck3/game/localization/english/regiment_l_english.yml", "l_english:\n conrois: \"Conrois\"\n huscarl: \"Huscarls\"\n");
    }

    [Fact]
    public void NestedModLocalizationOverridesBaseAndResolvesLiteralReferences()
    {
        Write("base/a.yml", "l_english:\n conrois: \"Original\"\n alias: \"$conrois$\"\n");
        Write("mod/nested/not_named_regiment.yml", "l_english:\n conrois:0 \"Norman \\\"guard\\\"\" # comment\n");
        var names = Ck3RegimentNames.Resolve(["base", "mod"], ["conrois", "alias", "new_heavy_cavalry"]);
        Assert.Equal("Norman \"guard\"", names["conrois"]);
        Assert.Equal("Norman \"guard\"", names["alias"]);
        Assert.Equal("New Heavy Cavalry", names["new_heavy_cavalry"]);
    }

    [Fact]
    public void UnresolvedRuntimeExpressionsAndCyclicReferencesUseReadableTypeFallbacks()
    {
        Write("mod/a.yml", "l_english:\n scripted_unit: \"[GetMaA('conrois').GetName]\"\n cycle_a: \"$cycle_b$\"\n cycle_b: \"$cycle_a$\"\n");
        var names = Ck3RegimentNames.Resolve(["mod"], ["scripted_unit", "cycle_a"]);
        Assert.Equal("Scripted Unit", names["scripted_unit"]); Assert.Equal("Cycle A", names["cycle_a"]);
    }

    [Fact]
    public void MaaAndFundedGuardSharingAnAssetReceiveDifferentPrivateLabels()
    {
        var maa = new Unit("conrois", 70, Culture, RegimentType.MenAtArms); maa.SetLocName("Wrong inherited levy label");
        var guard = new Unit("Commander", 30, Culture, RegimentType.Commander);
        guard.CommanderGuardRepresentation = new("1", RegimentType.MenAtArms, "conrois", "7", "shared_heavy",
            "10", "100", "0", 100, 100, 30, 30, [maa.UniqueID], null, 0);
        var army = new Army("1", "attacker", true); army.SetUnits([maa, guard]);
        Deployment((maa, "cc_sf_maa"), (guard, "cc_sf_guard"));
        var table = new TsvTable("Loc", "1", ["key", "text", "tooltip"]);
        foreach (string key in new[] {"shared_heavy", "cc_sf_maa", "cc_sf_guard"})
            table.Rows.Add(new() {{"key", "land_units_onscreen_name_" + key}, {"text", "Shared original"}, {"tooltip", "true"}});
        table.Write("battle/text/db/cc_stat_fidelity.loc.tsv");
        GeneratedUnitLabels.Capture([army], "deployment.xml"); GeneratedUnitLabels.Apply("battle");
        var rows = TsvTable.Read("battle/text/db/cc_stat_fidelity.loc.tsv").Rows.ToDictionary(r => r["key"], r => r["text"]);
        Assert.Equal("MAA Conrois", rows["land_units_onscreen_name_cc_sf_maa"]);
        Assert.Equal("Commander's guard (Conrois)", rows["land_units_onscreen_name_cc_sf_guard"]);
        Assert.Equal("Shared original", rows["land_units_onscreen_name_shared_heavy"]);
        Assert.Equal(2, GeneratedUnitLabels.Pending.Count);
    }

    [Fact]
    public void SharedPrivateKeyCannotSilentlyHideADifferentNativeType()
    {
        var conrois = new Unit("conrois", 100, Culture, RegimentType.MenAtArms);
        var huscarl = new Unit("huscarl", 100, Culture, RegimentType.MenAtArms);
        var army = new Army("1", "attacker", true); army.SetUnits([conrois, huscarl]);
        Deployment((conrois, "cc_sf_same"), (huscarl, "cc_sf_same"));
        Assert.Throws<InvalidDataException>(() => GeneratedUnitLabels.Capture([army], "deployment.xml"));
        options["UnitCards"] = "Disabled";
        GeneratedUnitLabels.Capture([army], "deployment.xml"); Assert.Empty(GeneratedUnitLabels.Pending);
        GeneratedUnitLabels.Apply("missing-output");
    }

    private static void Deployment(params (Unit Unit, string Key)[] units) =>
        new XDocument(new XElement("battle", units.Select(pair => new XElement("unit",
            new XAttribute("script_name", "0_attacker_army1_TYPE" + pair.Unit.GetName() + "_" + pair.Unit.UniqueID + "_CULTURE7_0"),
            new XElement("unit_type", new XAttribute("type", pair.Key)))))).Save("deployment.xml");
    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    public void Dispose()
    {
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin("");
        options.Clear(); foreach (var pair in oldOptions) options.Add(pair.Key, pair.Value);
        modRoots.Clear(); modRoots.AddRange(oldRoots); ck3Path.SetValue(settings, oldCk3Path);
        Environment.CurrentDirectory = previous;
        if (Path.GetDirectoryName(Path.GetFullPath(scratch)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(scratch, true);
    }
}
