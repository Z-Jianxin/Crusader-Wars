using System.Reflection;
using System.Xml;
using CrusaderWars;
using CrusaderWars.data.save_file;
using CrusaderWars.twbattle;
using CrusaderWars.unit_mapper;
using Xunit;

public sealed class MapperXPathTests
{
    [Theory]
    [InlineData("Latin")]
    [InlineData("Rus' Rebels")]
    [InlineData("A \"quoted\" faction")]
    [InlineData("Rus' \"Rebels\"")]
    [InlineData("'\"")]
    [InlineData("")]
    public void XPathLiteralMatchesTheWholeAttributeWithoutInjection(string value)
    {
        var document = new XmlDocument(); document.LoadXml("<Root><Node/><Node name='unrelated'/></Root>");
        var expected = (XmlElement)document.DocumentElement!.FirstChild!; expected.SetAttribute("name", value);
        Assert.Same(expected, document.SelectSingleNode("/Root/Node[@name=" + MapperXPath.Literal(value) + "]"));
    }

    [Fact]
    public void ActualFactionTypeHeritageCultureAndTitleLookupsAcceptBothQuoteCharacters()
    {
        var mapper = typeof(AvailableUnit).Assembly.GetType("CrusaderWars.unit_mapper.UnitMappers_BETA")!;
        var path = mapper.GetProperty("LoadedUnitMapper_FolderPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        var tag = mapper.GetProperty("ActivePlaythroughTag", BindingFlags.Public | BindingFlags.Static)!;
        object? oldPath = path.GetValue(null), oldTag = tag.GetValue(null);
        string oldDirectory = Environment.CurrentDirectory, scratch = Path.Combine(Path.GetTempPath(), "cc-xpath-" + Guid.NewGuid().ToString("N"));
        bool oldSiege = BattleState.IsSiegeBattle;
        string faction = "Rus' \"Rebels\"", type = "archers' \"guard\"", heritage = "heritage'\"test", cultureName = "culture'\"test", title = "k_title'\"test";
        foreach (string folder in new[] { "Factions", "Cultures", "Titles", "data" }) Directory.CreateDirectory(Path.Combine(scratch, folder));
        var factions = new XmlDocument(); factions.LoadXml("<Factions><Faction subculture='quoted_subculture'><MenAtArm key='quoted_faction_asset' max='INFANTRY'/><Levies key='quoted_levy' percentage='100' max='LEVY'/><Garrison key='quoted_garrison' percentage='100' level='1' max='LEVY'/></Faction></Factions>");
        ((XmlElement)factions.SelectSingleNode("/Factions/Faction")!).SetAttribute("name", faction);
        ((XmlElement)factions.SelectSingleNode("//MenAtArm")!).SetAttribute("type", type);
        factions.Save(Path.Combine(scratch, "Factions", "custom.xml"));
        var cultures = new XmlDocument(); cultures.LoadXml("<Cultures><Heritage><Culture/></Heritage></Cultures>");
        var h = (XmlElement)cultures.SelectSingleNode("//Heritage")!; h.SetAttribute("name", heritage); h.SetAttribute("faction", faction);
        var c = (XmlElement)cultures.SelectSingleNode("//Culture")!; c.SetAttribute("name", cultureName); c.SetAttribute("faction", faction);
        cultures.Save(Path.Combine(scratch, "Cultures", "custom.xml"));
        var titles = new XmlDocument(); titles.LoadXml("<Titles><Title><MenAtArm key='quoted_title_asset' max='INFANTRY'/></Title></Titles>");
        ((XmlElement)titles.SelectSingleNode("//Title")!).SetAttribute("title_key", title);
        ((XmlElement)titles.SelectSingleNode("//MenAtArm")!).SetAttribute("type", type);
        titles.Save(Path.Combine(scratch, "Titles", "custom.xml"));
        object? Call(string name, params object[] args) => mapper.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
        try
        {
            Environment.CurrentDirectory = scratch; path.SetValue(null, scratch); tag.SetValue(null, "Custom"); Call("ClearFactionCache");
            var owner = new Owner("42"); var unit = new Unit(type, 100, new Culture("1", cultureName, heritage), RegimentType.MenAtArms, false, owner);
            unit.SetAttilaFaction(faction);
            Assert.Equal("quoted_faction_asset", (((string, bool))Call("GetUnitKey", unit)!).Item1);
            Assert.Equal("quoted_subculture", Call("GetSubculture", faction));
            Assert.Equal(faction, Call("GetAttilaFaction", cultureName, heritage));
            Assert.Contains(faction, (List<string>)Call("GetFactionsByHeritage", heritage)!);
            Assert.Equal("INFANTRY", Call("GetMenAtArmMaxCategory", type));
            var levies = ((List<(int, string, string, string)>, string))Call("GetFactionLevies", faction)!;
            Assert.Equal("quoted_levy", Assert.Single(levies.Item1).Item2);
            BattleState.IsSiegeBattle = true;
            var garrisons = (List<(int, string, string, string)>)Call("GetFactionGarrison", faction, 1)!;
            Assert.Equal("quoted_garrison", Assert.Single(garrisons).Item2);
            owner.SetPrimaryTitle(title);
            Assert.Equal("quoted_title_asset", (((string, bool))Call("GetUnitKey", unit)!).Item1);
        }
        finally
        {
            path.SetValue(null, oldPath); tag.SetValue(null, oldTag); Call("ClearFactionCache"); BattleState.IsSiegeBattle = oldSiege;
            Environment.CurrentDirectory = oldDirectory;
            string full = Path.GetFullPath(scratch);
            if (Path.GetDirectoryName(full) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(full, true);
        }
    }
}
