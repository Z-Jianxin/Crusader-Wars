using System.Reflection;
using System.Text.Json;
using CrusaderWars;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using Xunit;

public sealed class NativeMaaDefinitionsTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-native-maa-" + Guid.NewGuid().ToString("N"));
    private const string Folder = "common/men_at_arms_types";

    [Fact]
    public void NativeNoncombatantClassificationDoesNotDependOnMapperFlagsOrKnownUnitNames()
    {
        string root = Root("base", "custom_equipment={ type=siege_weapon fights_in_main_phase=no damage=0 toughness=12 }\n" +
            "custom_attendant={ type=skirmishers fights_in_main_phase=no }\nconrois={ type=heavy_cavalry damage=100 }");
        var definitions = NativeMaaDefinitions.LoadRoots(new[] {(root, Array.Empty<string>())});
        Assert.True(definitions["custom_equipment"].IsSiegeEquipment);
        Assert.True(definitions["custom_equipment"].IsFieldNoncombatant);
        Assert.False(definitions["custom_attendant"].IsSiegeEquipment);
        Assert.True(definitions["custom_attendant"].IsFieldNoncombatant);
        Assert.False(definitions["conrois"].IsSiegeEquipment); Assert.False(definitions["conrois"].IsFieldNoncombatant);
        var unit = new Unit("custom_equipment", 20, null, RegimentType.MenAtArms); unit.SetUnitKey("ordinary_infantry_proxy"); unit.SetIsSiege(false);
        var field = typeof(ArmiesReader).GetField("nativeMaaDefinitions", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? before = field.GetValue(null);
        try
        {
            field.SetValue(null, definitions);
            Assert.True(ArmiesReader.RequireNativeMaaDefinition(unit).IsSiegeEquipment);
            Assert.False(unit.IsSiege()); Assert.Equal("ordinary_infantry_proxy", unit.GetAttilaUnitKey());
        }
        finally { field.SetValue(null, before); }
    }

    [Fact]
    public void HigherPriorityVirtualFileReplacesWholeFileAndPreservesAuthorFlags()
    {
        string baseline = Root("base", "same={type=siege_weapon fights_in_main_phase=no} removed={type=siege_weapon}");
        string mod = Root("mod", "same={type=heavy_infantry fights_in_main_phase=yes}");
        var values = NativeMaaDefinitions.LoadRoots(new[] {(baseline, Array.Empty<string>()), (mod, Array.Empty<string>())});
        Assert.False(values["same"].IsSiegeEquipment); Assert.True(values["same"].FightsInMainPhase);
        Assert.False(values.ContainsKey("removed")); Assert.StartsWith(mod, values["same"].SourceFile);
    }

    [Fact]
    public void ReplacePathRemovesOldTypesBeforeLoadingNewDefinitions()
    {
        string baseline = Root("base", "old={type=siege_weapon fights_in_main_phase=no}");
        string mod = Root("mod", "new={type=siege_weapon fights_in_main_phase=yes}", "new.txt");
        var values = NativeMaaDefinitions.LoadRoots(new[] {(baseline, Array.Empty<string>()), (mod, new[] {Folder})});
        Assert.False(values.ContainsKey("old")); Assert.True(values["new"].IsSiegeEquipment); Assert.False(values["new"].IsFieldNoncombatant);
    }

    [Fact]
    public void ActiveDescriptorsFollowEnabledOrderAndIgnoreDisabledRoots()
    {
        string installation = Path.Combine(scratch, "installation");
        RootAt(Path.Combine(installation, "game"), "test={type=heavy_cavalry}");
        string first = Root("first", "test={type=siege_weapon fights_in_main_phase=no}");
        string second = Root("second", "test={type=archers fights_in_main_phase=yes}");
        Root("disabled", "test={type=siege_weapon fights_in_main_phase=no}");
        string user = Path.Combine(scratch, "user"); Directory.CreateDirectory(Path.Combine(user, "mod"));
        File.WriteAllText(Path.Combine(user, "mod/first.mod"), "path=\"" + first.Replace('\\','/') + "\"");
        File.WriteAllText(Path.Combine(user, "mod/second.mod"), "path=\"" + second.Replace('\\','/') + "\"");
        File.WriteAllText(Path.Combine(user, "dlc_load.json"), JsonSerializer.Serialize(new {enabled_mods=new[] {"mod/first.mod", "mod/second.mod"}, disabled_dlcs=Array.Empty<string>()}));
        var values = NativeMaaDefinitions.LoadActive(Path.Combine(installation, "binaries/ck3.exe"), user);
        Assert.Equal("archers", values["test"].Category); Assert.True(values["test"].FightsInMainPhase);
    }

    [Theory]
    [InlineData("a={type=siege_weapon fights_in_main_phase=no fights_in_main_phase=yes}")]
    [InlineData("a={type=siege_weapon fights_in_main_phase=unknown}")]
    [InlineData("a={type=siege_weapon")]
    public void AmbiguousOrMalformedNativeSemanticsFailExplicitly(string text)
    {
        string root = Root("base", text);
        Assert.Throws<InvalidDataException>(() => NativeMaaDefinitions.LoadRoots(new[] {(root, Array.Empty<string>())}));
    }

    private string Root(string name, string text, string filename = "types.txt")
    {
        string root = Path.Combine(scratch, name); RootAt(root, text, filename); return root;
    }
    private static void RootAt(string root, string text, string filename = "types.txt")
    {
        string folder = Path.Combine(root, Folder); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, filename), text);
    }
    public void Dispose()
    {
        string resolved = Path.GetFullPath(scratch);
        if (Directory.Exists(resolved) && Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(resolved, true);
    }
}
