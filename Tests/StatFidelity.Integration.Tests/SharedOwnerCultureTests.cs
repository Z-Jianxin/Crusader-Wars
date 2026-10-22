using System.Reflection;
using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using Xunit;

public sealed class SharedOwnerCultureTests : IDisposable
{
    private readonly string originalDirectory = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-owner-culture-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> options;
    private readonly string? originalMode;
    private readonly FieldInfo attackers = typeof(ArmiesReader).GetField("attacker_armies", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly FieldInfo defenders = typeof(ArmiesReader).GetField("defender_armies", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object originalAttackers;
    private readonly object originalDefenders;

    public SharedOwnerCultureTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data", "save_file_data"));
        Environment.CurrentDirectory = scratch;
        options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        options.TryGetValue("StatFidelityMode", out originalMode);
        originalAttackers = attackers.GetValue(null)!;
        originalDefenders = defenders.GetValue(null)!;
    }

    [Fact]
    public void SavedCultureReachesEveryOwnerAcrossSidesAndRefreshesOnlyUntrackedGenerals()
    {
        Bind();
        var first = Army("29", "attacker", "32570"); first.CommanderID = "32570";
        var second = Army("30", "defender", "32570");
        Assert.True(StatFidelitySession.EnsureNativeCommanderRepresentation(second));
        var untracked = second.Commander;
        var trackedOther = Army("31", "defender", "32570"); trackedOther.CommanderID = "999";
        trackedOther.SetCommander(new CommanderSystem("Other commander", "999", 1, 1, 1, false));
        trackedOther.Commander!.ChangeCulture(new Culture("88"));
        var unrelated = Army("32", "defender", "40000"); unrelated.Owner!.SetCulture(new Culture("77"));
        var merged = Army("33", "defender", "32570"); second.AddMergedArmy(merged);
        Read([first], [second, trackedOther, unrelated], NativeCharacter());
        foreach (var army in new[] { first, second, trackedOther, merged }) Assert.Equal("231", army.Owner!.GetCulture().ID);
        Assert.Equal("77", unrelated.Owner!.GetCulture().ID);
        Assert.Same(untracked, second.Commander);
        Assert.Equal("231", second.Commander!.GetCultureObj()!.ID);
        Assert.False(second.Commander.IsTrackedCharacter);
        Assert.Null(second.CommanderID);
        Assert.Equal("cc_untracked_30", second.Commander.ID);
        Assert.Equal("999", trackedOther.Commander.ID);
        Assert.Equal("88", trackedOther.Commander.GetCultureObj()!.ID);
    }

    [Fact]
    public void EarlyMainCommanderlessPlaceholderGetsNativeCultureWithoutOwnerIdentity()
    {
        Bind();
        var main = Army("1", "attacker", "32570", true);
        Assert.True(StatFidelitySession.EnsureNativeCommanderRepresentation(main));
        var placeholder = main.Commander;
        Read([main], [], NativeCharacter());
        Assert.Same(placeholder, main.Commander);
        Assert.Equal("231", main.Commander!.GetCultureObj()!.ID);
        Assert.Equal("cc_untracked_1", main.Commander.ID);
        Assert.Null(main.CommanderID);
        Assert.False(main.Commander.IsTrackedCharacter);
    }

    [Fact]
    public void FirstCommanderMatchDoesNotHideItsOwnershipOfAnotherArmy()
    {
        Bind();
        var first = Army("29", "attacker", "90000"); first.CommanderID = "32570";
        var owned = Army("30", "defender", "32570");
        Read([first], [owned], NativeCharacter());
        Assert.Equal("231", owned.Owner!.GetCulture().ID);
        Assert.Equal("", first.Owner!.GetCulture().ID);
    }

    [Fact]
    public void LegacyOrUnboundFramesDoNotPropagateAdditionalOwners()
    {
        foreach (bool enabled in new[] { false, true })
        {
            Bind(enabled, bound: false);
            var first = Army("29", "attacker", "32570");
            var second = Army("30", "defender", "32570");
            Read([first], [second], NativeCharacter(includeNestedCulture: false));
            Assert.Equal("231", first.Owner!.GetCulture().ID);
            Assert.Equal("", second.Owner!.GetCulture().ID);
        }
    }

    private void Bind(bool enabled = true, bool bound = true)
    {
        options["StatFidelityMode"] = enabled ? "Enabled" : "Disabled";
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|20|0|0.05\nCCSTAT|v1|END|snapshot-1");
        if (enabled && bound)
            StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={ data={ {flag=ccstat_snapshot_serial data={type=value identity=100000}} {flag=ccstat_combat data={type=com}} } }\n}\n}"), "42", "0");
    }

    private static Army Army(string id, string side, string owner, bool main = false)
    {
        var army = new Army(id, side, main);
        army.SetOwner(new Owner(owner));
        return army;
    }

    private static string NativeCharacter(bool includeNestedCulture = true) =>
        "living={\n32570={\n\tfirst_name=\"Same native owner\"\n\tskill={ 1 2 3 4 5 6 }\n\tculture=231\n" +
        (includeNestedCulture ? "\talive_data={\n\t\tvariables={\n\t\t\tculture=999\n\t\t}\n\t}\n" : "") + "}\n}\n";

    private void Read(List<Army> left, List<Army> right, string living)
    {
        attackers.SetValue(null, left); defenders.SetValue(null, right);
        File.WriteAllText(Path.Combine(scratch, "data", "save_file_data", "Living.txt"), living);
        typeof(ArmiesReader).GetMethod("ReadCharacters", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
    }

    public void Dispose()
    {
        attackers.SetValue(null, originalAttackers); defenders.SetValue(null, originalDefenders);
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin("");
        if (originalMode == null) options.Remove("StatFidelityMode"); else options["StatFidelityMode"] = originalMode;
        Environment.CurrentDirectory = originalDirectory;
        if (Path.GetDirectoryName(Path.GetFullPath(scratch)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
            Directory.Delete(scratch, true);
    }
}
