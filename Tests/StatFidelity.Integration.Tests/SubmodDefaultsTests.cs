using CrusaderWars.mod_manager;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

public sealed class SubmodDefaultsTests : IDisposable
{
    private readonly string previousDirectory = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-submod-defaults-" + Guid.NewGuid().ToString("N"));
    private readonly PropertyInfo selections = typeof(SubmodManager).GetProperty("ActiveSubmodsByPlaythrough", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object previousSelections;

    public SubmodDefaultsTests()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "settings", "schemas", "ActiveSubmods.xsd")), "Production ActiveSubmods schema must be copied by the application reference.");
        previousSelections = selections.GetValue(null)!;
        selections.SetValue(null, new Dictionary<string, List<string>>());
        Directory.CreateDirectory(scratch);
        Environment.CurrentDirectory = scratch;
    }

    [Fact]
    public void MissingSettingsCreatesAndImmediatelyLoadsOnlyTheRealDefaultTag()
    {
        SubmodManager.LoadActiveSubmods();
        Assert.Equal(new[] { "DahanChina" }, SubmodManager.GetActiveSubmodsForPlaythrough("DefaultCK3"));
        Assert.Empty(SubmodManager.GetActiveSubmodsForPlaythrough("AGOT"));
        var saved = XDocument.Load("settings/ActiveSubmods.xml");
        var playthrough = Assert.Single(saved.Root!.Elements("Playthrough"));
        Assert.Equal("DefaultCK3", (string?)playthrough.Attribute("tag"));
        Assert.Equal("DahanChina", Assert.Single(playthrough.Elements("Submod")).Value);
        SubmodManager.LoadActiveSubmods();
        Assert.Equal(new[] { "DahanChina" }, SubmodManager.GetActiveSubmodsForPlaythrough("DefaultCK3"));
    }

    [Fact]
    public void MissingDefaultPlaythroughReceivesDefaultsWithoutChangingOtherSelections()
    {
        Write("<Playthrough tag='AGOT'><Submod>First</Submod><Submod>Second</Submod></Playthrough><Playthrough tag='Custom'/>");
        SubmodManager.LoadActiveSubmods();
        Assert.Equal(new[] { "DahanChina" }, SubmodManager.GetActiveSubmodsForPlaythrough("DefaultCK3"));
        Assert.Equal(new[] { "First", "Second" }, SubmodManager.GetActiveSubmodsForPlaythrough("AGOT"));
        Assert.Empty(SubmodManager.GetActiveSubmodsForPlaythrough("Custom"));
        SubmodManager.LoadActiveSubmods();
        Assert.Equal(new[] { "First", "Second" }, SubmodManager.GetActiveSubmodsForPlaythrough("AGOT"));
        Assert.Single(XDocument.Load("settings/ActiveSubmods.xml").Root!.Elements("Playthrough"), n => (string?)n.Attribute("tag") == "Custom");
    }

    [Theory]
    [InlineData("")]
    [InlineData("<Submod>AnotherChoice</Submod>")]
    [InlineData("<Submod>AnotherChoice</Submod><Submod>DahanChina</Submod>")]
    public void ExistingDefaultPlaythroughChoicesAreAuthoritative(string children)
    {
        Write("<Playthrough tag='DefaultCK3'>" + children + "</Playthrough>");
        byte[] before = File.ReadAllBytes("settings/ActiveSubmods.xml");
        SubmodManager.LoadActiveSubmods();
        Assert.Equal(XElement.Parse("<Selection>" + children + "</Selection>").Elements().Select(e => e.Value),
            SubmodManager.GetActiveSubmodsForPlaythrough("DefaultCK3"));
        Assert.Equal(before, File.ReadAllBytes("settings/ActiveSubmods.xml"));
    }

    [Fact]
    public void ExplicitEmptySelectionSurvivesSaveReloadWithoutReenablingDefaults()
    {
        SubmodManager.LoadActiveSubmods();
        SubmodManager.SetActiveSubmodsForPlaythrough("DefaultCK3", new());
        SubmodManager.SetActiveSubmodsForPlaythrough("AGOT", new() { "RememberMe" });
        SubmodManager.SaveActiveSubmods();
        SubmodManager.LoadActiveSubmods();
        Assert.Empty(SubmodManager.GetActiveSubmodsForPlaythrough("DefaultCK3"));
        var emptyChoice = Assert.Single(XDocument.Load("settings/ActiveSubmods.xml").Root!.Elements("Playthrough"), n => (string?)n.Attribute("tag") == "DefaultCK3");
        Assert.Empty(emptyChoice.Elements("Submod"));
        Assert.Equal(new[] { "RememberMe" }, SubmodManager.GetActiveSubmodsForPlaythrough("AGOT"));
    }

    [Fact]
    public void StoredSelectionsDoNotAliasCallerOrReturnedLists()
    {
        var choice = new List<string> { "DahanChina" };
        SubmodManager.SetActiveSubmodsForPlaythrough("DefaultCK3", choice);
        choice.Clear();
        SubmodManager.GetActiveSubmodsForPlaythrough("DefaultCK3").Clear();
        Assert.Equal(new[] { "DahanChina" }, SubmodManager.GetActiveSubmodsForPlaythrough("DefaultCK3"));
        SubmodManager.SetActiveSubmodsForPlaythrough("Custom", new() { "CustomChoice" });
        Assert.Equal(new[] { "DahanChina" }, SubmodManager.GetActiveSubmodsForPlaythrough("DefaultCK3"));
    }

    private static void Write(string contents)
    {
        Directory.CreateDirectory("settings");
        File.WriteAllText("settings/ActiveSubmods.xml", "<ActiveSubmods>" + contents + "</ActiveSubmods>");
    }

    public void Dispose()
    {
        selections.SetValue(null, previousSelections);
        Environment.CurrentDirectory = previousDirectory;
        if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
    }
}
