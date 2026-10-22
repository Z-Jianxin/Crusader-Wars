using System.Reflection;
using System.Xml;
using CrusaderWars.unit_mapper;
using Xunit;

public sealed class MapperDocumentCacheTests
{
    [Fact]
    public void ReadOnlyDocumentsAreReusedAndInvalidatedBySourceAuthorityContextAndExplicitClear()
    {
        var mapper = typeof(AvailableUnit).Assembly.GetType("CrusaderWars.unit_mapper.UnitMappers_BETA")!;
        var read = mapper.GetMethod("ReadMapperXml", BindingFlags.NonPublic | BindingFlags.Static)!;
        var inspect = mapper.GetMethod("LoadMapperXml", BindingFlags.NonPublic | BindingFlags.Static)!;
        var clear = mapper.GetMethod("ClearFactionCache")!;
        var path = mapper.GetProperty("LoadedUnitMapper_FolderPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        var tag = mapper.GetProperty("ActivePlaythroughTag", BindingFlags.Public | BindingFlags.Static)!;
        object? originalPath = path.GetValue(null), originalTag = tag.GetValue(null);
        string scratch = Path.Combine(Path.GetTempPath(), "cc-mapper-cache-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(scratch, "Factions"); Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "custom.xml"), official = Path.Combine(folder, "OfficialCC_DefaultCK3_Test_Factions.xml");
        File.WriteAllText(file, "<Factions><Faction name='Latin'><MenAtArm type='conrois' key='first'/></Faction></Factions>");
        File.WriteAllText(official, "<Factions/>");
        XmlDocument Read() => (XmlDocument)read.Invoke(null, [file])!;
        try
        {
            path.SetValue(null, scratch); tag.SetValue(null, "Custom"); clear.Invoke(null, null);
            var first = Read(); Assert.Same(first, Read());
            // The inspection API owns a separate DOM, so audit tools cannot poison the cache.
            var inspected = new XmlDocument(); inspect.Invoke(null, [inspected, file]);
            inspected.DocumentElement!.RemoveAll(); Assert.Same(first, Read());
            Assert.NotNull(first.SelectSingleNode("//MenAtArm"));
            File.WriteAllText(file, "<Factions><Faction name='Latin'><MenAtArm type='conrois' key='changed_length'/></Faction></Factions>");
            var changed = Read(); Assert.NotSame(first, changed);
            Assert.Equal("changed_length", changed.SelectSingleNode("//MenAtArm")!.Attributes!["key"]!.Value);
            File.WriteAllText(official, "<Factions><Faction name='Changed authority'/></Factions>");
            var authorityChanged = Read(); Assert.NotSame(changed, authorityChanged);
            tag.SetValue(null, "DefaultCK3"); var contextChanged = Read(); Assert.NotSame(authorityChanged, contextChanged);
            clear.Invoke(null, null); Assert.NotSame(contextChanged, Read());
        }
        finally
        {
            path.SetValue(null, originalPath); tag.SetValue(null, originalTag); clear.Invoke(null, null);
            string resolved = Path.GetFullPath(scratch);
            if (Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(resolved, true);
        }
    }
}
