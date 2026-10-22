using System.Reflection;
using System.Xml;
using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.save_file;
using CrusaderWars.mod_manager;
using CrusaderWars.twbattle;
using CrusaderWars.unit_mapper;
using Xunit;

public sealed class DefaultCk3OverlayScopeTests
{
    public const string InstalledMapper = "C:/[GAME]Crusader Wars/Crusader Conflicts - CK3 Stat Fidelity/unit mappers/OfficialCC_DefaultCK3_EarlyMedieval_919Mod";

    [Fact]
    public void FingerprintsProtectEditedFilesCustomMappersAndOtherPlaythroughs()
    {
        const string known = "DB0701A0B8C26413CE0DF6635CC219E12F8861D4873EEEBF7EDE1F247F0B2248";
        Assert.Equal(MapperOverlayScope.Asian, DefaultCk3OverlayScope.Identify("DefaultCK3", "Submod_DahanChina_Cultures.xml", known));
        Assert.Equal(MapperOverlayScope.UnrecognizedRevision, DefaultCk3OverlayScope.Identify("DefaultCK3", "Submod_DahanChina_Cultures.xml", "edited"));
        Assert.Equal(MapperOverlayScope.Unrestricted, DefaultCk3OverlayScope.Identify("DefaultCK3", "MyCustom_Cultures.xml", known));
        Assert.Equal(MapperOverlayScope.Unrestricted, DefaultCk3OverlayScope.Identify("BookmarksPlus", "Submod_DahanChina_Cultures.xml", known));
    }

    [Fact]
    public void RegionalCultureOverlayPreservesNativeNormanAndKeepsHanAndNewCultures()
    {
        var official = Xml("<Cultures><Heritage name='heritage_frankish' faction='Frankish'><Culture name='norman' faction='Latin'/></Heritage><Heritage name='heritage_chinese' faction='Chinese'><Culture name='han' faction='Chinese'/></Heritage></Cultures>");
        string untouched = official.OuterXml;
        var overlay = Xml("<Cultures><Heritage name='heritage_frankish' faction='Imazigen'><Culture name='norman' faction='Imazigen'/><Culture name='new_culture' faction='New Faction'/></Heritage><Heritage name='heritage_chinese' faction='Han Empire'><Culture name='han' faction='Han Empire'/></Heritage></Cultures>");
        Assert.Equal(1, DefaultCk3OverlayScope.Apply(overlay, [official], MapperOverlayScope.Asian));
        Assert.Null(overlay.SelectSingleNode("//Culture[@name='norman']"));
        Assert.Equal("Frankish", overlay.SelectSingleNode("//Heritage[@name='heritage_frankish']")!.Attributes!["faction"]!.Value);
        Assert.Equal("New Faction", overlay.SelectSingleNode("//Culture[@name='new_culture']")!.Attributes!["faction"]!.Value);
        Assert.Equal("Han Empire", overlay.SelectSingleNode("//Culture[@name='han']")!.Attributes!["faction"]!.Value);
        Assert.Equal(untouched, official.OuterXml);
    }

    [Fact]
    public void FactionPolicyProtectsAllExistingRolePoolsButRetainsNewUnitsAndAsianOverrides()
    {
        var official = Xml("<Factions><Faction name='Latin' subculture='latin'><General key='native_general'/><Knights key='native_knight'/><Levies key='native_levy'/><Garrison key='native_garrison'/><MenAtArm type='conrois' key='cha_nor_chef'/></Faction><Faction name='Han Empire'><MenAtArm type='bowmen' key='old'/></Faction></Factions>");
        var overlay = Xml("<Factions><Faction name='Latin' subculture='wrong'><General key='wrong'/><Knights key='wrong'/><Levies key='wrong'/><Garrison key='wrong'/><MenAtArm type='conrois' key='ARM_light_cavalry'/><MenAtArm type='new_maa' key='new'/></Faction><Faction name='Han Empire'><MenAtArm type='bowmen' key='dahan'/></Faction><Faction name='New Culture'><Levies key='new'/></Faction></Factions>");
        Assert.Equal(5, DefaultCk3OverlayScope.Apply(overlay, [official], MapperOverlayScope.Asian));
        Assert.Null(overlay.SelectSingleNode("//Faction[@name='Latin']/MenAtArm[@type='conrois']"));
        Assert.Single(overlay.SelectNodes("//Faction[@name='Latin']/*")!.Cast<XmlNode>());
        Assert.Equal("latin", overlay.SelectSingleNode("//Faction[@name='Latin']")!.Attributes!["subculture"]!.Value);
        Assert.NotNull(overlay.SelectSingleNode("//MenAtArm[@type='new_maa']"));
        Assert.Equal("dahan", overlay.SelectSingleNode("//Faction[@name='Han Empire']/MenAtArm")!.Attributes!["key"]!.Value);
        Assert.NotNull(overlay.SelectSingleNode("//Faction[@name='New Culture']/Levies"));
    }

    [Fact]
    public void UnrelatedBookmarkCompatibilityIsAdditiveEvenInsideAsiaAndUnknownRevisionsAreUntouched()
    {
        var official = Xml("<Factions><Faction name='Han Empire'><MenAtArm type='bowmen' key='native'/></Faction></Factions>");
        const string text = "<Factions><Faction name='Han Empire'><MenAtArm type='bowmen' key='override'/><MenAtArm type='new_maa' key='new'/></Faction></Factions>";
        var additive = Xml(text);
        Assert.Equal(1, DefaultCk3OverlayScope.Apply(additive, [official], MapperOverlayScope.Additive));
        Assert.NotNull(additive.SelectSingleNode("//MenAtArm[@type='new_maa']"));
        var custom = Xml(text); string before = custom.OuterXml;
        Assert.Equal(0, DefaultCk3OverlayScope.Apply(custom, [official], MapperOverlayScope.UnrecognizedRevision));
        Assert.Equal(before, custom.OuterXml);
    }

    [InstalledDefaultMapperFact]
    public void ActualSharedLoaderKeepsNormanLatinAcrossMaaLevyGeneralKnightCachesAndExplicitReplacements()
    {
        var mapper = typeof(AvailableUnit).Assembly.GetType("CrusaderWars.unit_mapper.UnitMappers_BETA")!;
        var path = mapper.GetProperty("LoadedUnitMapper_FolderPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        var tag = mapper.GetProperty("ActivePlaythroughTag", BindingFlags.Public | BindingFlags.Static)!;
        var submods = typeof(SubmodManager).GetProperty("ActiveSubmodsByPlaythrough", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldPath = path.GetValue(null); var oldTag = tag.GetValue(null); var oldSubmods = submods.GetValue(null);
        string oldDirectory = Environment.CurrentDirectory, scratch = Path.Combine(Path.GetTempPath(), "cc-overlay-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(scratch, "data")); Environment.CurrentDirectory = scratch;
        var oldManual = BattleState.ManualUnitReplacements;
        var oldAuto = new Dictionary<string, (string replacementKey, bool isSiege)>(BattleProcessor.AutofixReplacements);
        try
        {
            path.SetValue(null, InstalledMapper); tag.SetValue(null, "DefaultCK3");
            submods.SetValue(null, new Dictionary<string, List<string>> { ["DefaultCK3"] = ["DahanChina"] });
            BattleState.ManualUnitReplacements = new(); BattleProcessor.AutofixReplacements.Clear();
            mapper.GetMethod("ClearFactionCache")!.Invoke(null, null);
            string Faction(string culture, string heritage) => (string)mapper.GetMethod("GetAttilaFaction")!.Invoke(null, [culture, heritage])!;
            Assert.Equal("Latin", Faction("norman", "heritage_frankish"));
            Assert.Equal("Han Empire", Faction("han", "heritage_chinese"));
            var culture = new Culture("73", "norman", "heritage_frankish"); var owner = new Owner("42", culture);
            string Map(string name, RegimentType type)
            {
                var unit = new Unit(name, 100, culture, type, false, owner); unit.SetAttilaFaction("Latin");
                return (((string, bool))mapper.GetMethod("GetUnitKey")!.Invoke(null, [unit])!).Item1;
            }
            Assert.Equal("cha_nor_chef", Map("conrois", RegimentType.MenAtArms));
            var official = new XmlDocument(); official.Load(Path.Combine(InstalledMapper, "Factions", "OfficialCC_DefaultCK3_EarlyMedieval_Factions.xml"));
            var latin = official.SelectSingleNode("/Factions/Faction[@name='Latin']")!;
            foreach (var (role, type) in new[] { ("General", RegimentType.Commander), ("Knights", RegimentType.Knight) })
            {
                string expected = latin.SelectNodes(role)!.Cast<XmlNode>().Where(n => n.Attributes?["rank"]?.Value == "1")
                    .Select(n => n.Attributes!["key"]!.Value).OrderBy(k => k).First();
                Assert.Equal(expected, Map(role, type));
            }
            var levies = ((List<(int percentage, string unit_key, string name, string max)>, string))mapper.GetMethod("GetFactionLevies")!.Invoke(null, ["Latin"])!;
            Assert.Equal(latin.SelectNodes("Levies")!.Cast<XmlNode>().Select(n => n.Attributes!["key"]!.Value), levies.Item1.Select(v => v.unit_key));
            Assert.Equal("cha_nor_chef", Map("conrois", RegimentType.MenAtArms)); // After cache population.
            BattleState.ManualUnitReplacements[("cha_nor_chef", false)] = ("user_choice", false);
            Assert.Equal("user_choice", Map("conrois", RegimentType.MenAtArms));
            BattleState.ManualUnitReplacements.Clear(); BattleProcessor.AutofixReplacements["cha_nor_chef"] = ("explicit_fix", false);
            Assert.Equal("explicit_fix", Map("conrois", RegimentType.MenAtArms));
        }
        finally
        {
            path.SetValue(null, oldPath); tag.SetValue(null, oldTag); submods.SetValue(null, oldSubmods);
            mapper.GetMethod("ClearFactionCache")!.Invoke(null, null);
            BattleState.ManualUnitReplacements = oldManual; BattleProcessor.AutofixReplacements.Clear();
            foreach (var pair in oldAuto) BattleProcessor.AutofixReplacements.Add(pair.Key, pair.Value);
            Environment.CurrentDirectory = oldDirectory;
            if (Path.GetDirectoryName(Path.GetFullPath(scratch)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(scratch, true);
        }
    }

    [Theory]
    [InlineData(397, 500)]
    [InlineData(398, 500)]
    [InlineData(396, 500)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(400, 500)]
    public void OrdinarySourceBelowConfiguredMaximumRemainsOneCohort(int soldiers, int maximum)
    {
        var result = UnitsFile.RetriveCalculatedUnits(soldiers, maximum);
        Assert.Equal(1, result.UnitNum);
        Assert.Equal(soldiers, result.UnitSoldiers + result.SoldiersRest);
    }

    private static XmlDocument Xml(string text) { var result = new XmlDocument(); result.LoadXml(text); return result; }
}

public sealed class InstalledDefaultMapperFactAttribute : FactAttribute
{
    public InstalledDefaultMapperFactAttribute()
    {
        if (!Directory.Exists(DefaultCk3OverlayScopeTests.InstalledMapper)) Skip = "Installed reviewed DefaultCK3 mapper fixture is unavailable.";
    }
}
