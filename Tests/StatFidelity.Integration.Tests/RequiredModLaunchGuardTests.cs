using System.Collections;
using System.Reflection;
using CrusaderWars;
using CrusaderWars.mod_manager;
using CrusaderWars.unit_mapper;
using Xunit;

public sealed class RequiredModLaunchGuardTests : IDisposable
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly string previousDirectory = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-required-packs-" + Guid.NewGuid().ToString("N"));
    private readonly Type mapper = typeof(HomePage).Assembly.GetType("CrusaderWars.unit_mapper.UnitMappers_BETA")!;
    private readonly PropertyInfo mods = typeof(AttilaModManager).GetProperty("ModsPaths", Static)!;
    private readonly PropertyInfo requests = typeof(AttilaModManager).GetProperty("RequestedRequiredMods", Static)!;
    private readonly PropertyInfo submods = typeof(SubmodManager).GetProperty("ActiveSubmodsByPlaythrough", Static)!;
    private readonly List<(PropertyInfo Property, object? Value)> originals = new();
    private readonly object settings;
    private readonly PropertyInfo attilaSetting;
    private readonly object? oldAttila;
    private readonly int oldYear = Date.Year;
    private readonly string game;

    public RequiredModLaunchGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data", "mod manager"));
        Directory.CreateDirectory(Path.Combine(scratch, "unit mappers"));
        Environment.CurrentDirectory = scratch;
        File.WriteAllText("data/mod manager/active_mods.txt", "");
        game = Path.Combine(scratch, "steamapps", "common", "Attila");
        Directory.CreateDirectory(Path.Combine(game, "data"));
        File.WriteAllText(Path.Combine(game, "Attila.exe"), "fixture, never executed");

        foreach (var property in new[] { mods, requests, submods, MapperProperty("LoadedUnitMapper_FolderPath"), MapperProperty("ActivePlaythroughTag"), MapperProperty("AvailableSubmods"), MapperProperty("Terrains") })
            originals.Add((property, property.GetValue(null)));
        mods.SetValue(null, Activator.CreateInstance(mods.PropertyType));
        requests.SetValue(null, new List<string>());
        submods.SetValue(null, new Dictionary<string, List<string>>());
        MapperProperty("LoadedUnitMapper_FolderPath").SetValue(null, "fixture_mapper");
        MapperProperty("ActivePlaythroughTag").SetValue(null, "DefaultCK3");
        MapperProperty("AvailableSubmods").SetValue(null, new List<Submod>());

        Type settingsType = typeof(HomePage).Assembly.GetType("CrusaderWars.Properties.Settings")!;
        settings = settingsType.GetProperty("Default", Static)!.GetValue(null)!;
        attilaSetting = settingsType.GetProperty("VAR_attila_path")!;
        oldAttila = attilaSetting.GetValue(settings);
        attilaSetting.SetValue(settings, Path.Combine(game, "Attila.exe"));
    }

    [Fact]
    public void MissingSelectedSubmodStopsBeforeEitherLaunchFileChanges()
    {
        AddPack("base.pack");
        AttilaModManager.SetLoadingRequiredMods(new() { "base.pack", "Dahan33_attila_crusader_conflicts_fix.pack" });
        string startup = Path.Combine(game, "used_mods_cc.txt");
        File.WriteAllText(startup, "previous valid startup");
        var error = Assert.Throws<FileNotFoundException>(() => AttilaModManager.CreateUserModsFile());
        Assert.Contains("Dahan33_attila_crusader_conflicts_fix.pack", error.Message);
        Assert.Equal("previous valid startup", File.ReadAllText(startup));
        Assert.False(File.Exists(Path.Combine(game, "steam_appid.txt")));
    }

    [Fact]
    public void ApplicableReplacementDoesNotRequireTheReplacedBaseAndOptOutRestoresIt()
    {
        AddPack("replacement.pack");
        MapperProperty("AvailableSubmods").SetValue(null, new List<Submod> {
            new() { Tag="replacement", Replaces=new() {"old-base.pack"} }
        });
        SubmodManager.SetActiveSubmodsForPlaythrough("DefaultCK3", new() { "replacement" });
        AttilaModManager.SetLoadingRequiredMods(new() { "old-base.pack", "replacement.pack" });
        Assert.Equal(new[] { "replacement.pack" }, AttilaModManager.GetEffectiveRequiredModNames());
        AttilaModManager.ValidateRequiredModsForLaunch();
        SubmodManager.SetActiveSubmodsForPlaythrough("DefaultCK3", new());
        Assert.Throws<FileNotFoundException>(() => AttilaModManager.ValidateRequiredModsForLaunch());
    }

    [Fact]
    public void CurrentEraRequirementsDoNotBorrowUnavailableOtherEraDependencies()
    {
        AddPack("early.pack");
        AddPack("dahan-main.pack");
        SubmodManager.SetActiveSubmodsForPlaythrough("DefaultCK3", new() { "DahanChina", "unrelated-choice" });
        MapperProperty("AvailableSubmods").SetValue(null, new List<Submod> {
            new() { Tag="DahanChina", Mods=new() {new() {FileName="dahan-main.pack"}} }
        });
        AttilaModManager.SetLoadingRequiredMods(new() { "early.pack", "dahan-main.pack" });
        AttilaModManager.ValidateRequiredModsForLaunch();
        Assert.DoesNotContain("later-era-fix.pack", AttilaModManager.GetEffectiveRequiredModNames());
        Assert.Equal(new[] { "DahanChina", "unrelated-choice" }, SubmodManager.GetActiveSubmodsForPlaythrough("DefaultCK3"));
    }

    [Fact]
    public void EveryPackInOneWorkshopSubscriptionIsDiscovered()
    {
        string workshop = Path.Combine(scratch, "workshop");
        Directory.CreateDirectory(Path.Combine(workshop, "123"));
        Directory.CreateDirectory(Path.Combine(workshop, "456"));
        File.WriteAllText(Path.Combine(workshop, "123", "main.pack"), "a");
        File.WriteAllText(Path.Combine(workshop, "123", "fix.PACK"), "b");
        File.WriteAllText(Path.Combine(workshop, "123", "preview.png"), "not a pack");
        File.WriteAllText(Path.Combine(workshop, "456", "extra.pack"), "c");
        Assert.Equal(new[] { "fix.PACK", "main.pack", "extra.pack" },
            AttilaModManager.DiscoverWorkshopPackFiles(workshop).Select(Path.GetFileName));
    }

    [Fact]
    public void RequiredDuplicateUsesDataPriorityOnceAndKeepsUnrelatedOptionalChoice()
    {
        AddPack("base.pack");
        AddPack("base.pack", steam: true);
        AddPack("fix.pack", steam: true);
        AddPack("cosmetic.pack", enabled: true);
        AttilaModManager.SetLoadingRequiredMods(new() { "base.pack", "BASE.PACK", "FIX.PACK" });
        AttilaModManager.CreateUserModsFile();
        string actual = File.ReadAllText(Path.Combine(game, "used_mods_cc.txt"));
        Assert.Equal(1, actual.Split("mod \"base.pack\";").Length - 1);
        Assert.Contains("mod \"fix.pack\";", actual);
        Assert.Contains("mod \"cosmetic.pack\";", actual);
        Assert.Equal(1, actual.Split("add_working_directory").Length - 1);
    }

    [Fact]
    public void FutureEraRequiredCopiesCannotLeakThroughOptionalMods()
    {
        AddPack("base.pack");
        AddPack("earl.pack");
        AddPack("earl.pack", steam: true);
        AddPack("cosmetic.pack");
        string catalog = Path.Combine(scratch, "unit mappers", "catalog");
        Directory.CreateDirectory(catalog);
        File.WriteAllText(Path.Combine(catalog, "Mods.xml"), "<Mods><Mod>base.pack</Mod><Mod>earl.pack</Mod></Mods>");
        File.WriteAllText("data/mod manager/active_mods.txt", "earl.pack\ncosmetic.pack\n");
        AttilaModManager.ReadInstalledMods();
        AttilaModManager.SetLoadingRequiredMods(new() { "base.pack" });
        AttilaModManager.CreateUserModsFile();
        string actual = File.ReadAllText(Path.Combine(game, "used_mods_cc.txt"));
        Assert.DoesNotContain("earl.pack", actual);
        Assert.Contains("cosmetic.pack", actual);
    }

    [Fact]
    public void MissingAndVersionMismatchRemainDistinctAndDisappearingFilesFail()
    {
        string file = AddPack("present.pack");
        AttilaModManager.SetLoadingRequiredMods(new() { "present.pack" });
        AttilaModManager.ValidateRequiredModsForLaunch();
        var verification = AttilaModManager.VerifyModFiles(new() { ("present.pack", new string('0', 64), null, null) }, null);
        Assert.Empty(verification.MissingFiles);
        Assert.Single(verification.MismatchedFiles);
        File.Delete(file);
        Assert.Throws<FileNotFoundException>(() => AttilaModManager.ValidateRequiredModsForLaunch());
    }

    [Fact]
    public void BoundarySelectionAndFailedLookupCannotRetainAnotherEra()
    {
        MakeMapper(DefaultCk3EraPolicy.EarlyMapper, 768, 1130, "early.pack");
        MakeMapper(DefaultCk3EraPolicy.HighMapper, 1129, 1259, "earl.pack");
        Date.Year = 1129;
        Assert.Equal(new[] { "early.pack" }, SelectMapper());
        Date.Year = 1130;
        Assert.Equal(new[] { "earl.pack" }, SelectMapper());
        Date.Year = 2200;
        Assert.Empty(SelectMapper());
        Assert.Null(mapper.GetMethod("GetLoadedUnitMapperName", Static)!.Invoke(null, null));
        Assert.Null(MapperProperty("Terrains").GetValue(null));
        AttilaModManager.SetLoadingRequiredMods(new());
        Assert.Throws<InvalidOperationException>(() => AttilaModManager.ValidateRequiredModsForLaunch());
    }

    private PropertyInfo MapperProperty(string name) => mapper.GetProperty(name, Static)!;
    private string AddPack(string name, bool steam = false, bool enabled = false)
    {
        string folder = steam ? Path.Combine(scratch, "steamapps", "workshop", "content", "325610", "123") : Path.Combine(game, "data");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "pack existence fixture, not launched");
        var assembly = typeof(HomePage).Assembly;
        var modType = assembly.GetType("CrusaderWars.mod_manager.Mod")!;
        var locationType = assembly.GetType("CrusaderWars.mod_manager.ModLocalization")!;
        var mod = Activator.CreateInstance(modType, enabled, null, name, Enum.Parse(locationType, steam ? "Steam" : "Data"), steam ? folder : path)!;
        ((IList)mods.GetValue(null)!).Add(mod);
        return path;
    }
    private void MakeMapper(string name, int start, int end, string pack)
    {
        string folder = Path.Combine(scratch, "unit mappers", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "tag.txt"), "DefaultCK3");
        File.WriteAllText(Path.Combine(folder, "Time Period.xml"), $"<TimePeriod><StartDate>{start}</StartDate><EndDate>{end}</EndDate></TimePeriod>");
        File.WriteAllText(Path.Combine(folder, "Mods.xml"), $"<Mods><Mod>{pack}</Mod></Mods>");
    }
    private List<string> SelectMapper() => (List<string>)mapper.GetMethod("GetUnitMapperModFromTagAndTimePeriod", Static)!
        .Invoke(null, new object[] { "DefaultCK3", new List<string>() })!;

    public void Dispose()
    {
        attilaSetting.SetValue(settings, oldAttila);
        foreach (var original in originals) original.Property.SetValue(null, original.Value);
        Date.Year = oldYear;
        Environment.CurrentDirectory = previousDirectory;
        Directory.Delete(scratch, recursive: true);
    }
}
