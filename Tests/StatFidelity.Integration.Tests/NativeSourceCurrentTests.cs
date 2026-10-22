using System.Reflection;
using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.battle_results.editors;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using Xunit;

public sealed class NativeSourceCurrentTests : IDisposable
{
    private readonly string previousDirectory = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-native-current-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> options = (Dictionary<string, string>)typeof(ModOptions)
        .GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private readonly string? previousMode;
    private readonly FieldInfo attackers = typeof(ArmiesReader).GetField("attacker_armies", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly FieldInfo defenders = typeof(ArmiesReader).GetField("defender_armies", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object oldAttackers;
    private readonly object oldDefenders;

    public NativeSourceCurrentTests()
    {
        oldAttackers = attackers.GetValue(null)!; oldDefenders = defenders.GetValue(null)!;
        options.TryGetValue("StatFidelityMode", out previousMode);
        Directory.CreateDirectory(Path.Combine(scratch, "data", "save_file_data"));
        Environment.CurrentDirectory = scratch;
    }

    [Fact]
    public void BoundNativeHiredSourcesKeepCurrentInsteadOfCombatInitialAndSerializeWithoutHealing()
    {
        Bind(true);
        var levy = Source("13736", "0", "0", "477");
        var bows = Source("13737", "0", "0", "358");
        var army = new Army("16777228", "defender", false);
        var levyRegiment = Regiment("73", RegimentType.Levy, "Levy", 477, 456, levy);
        var bowRegiment = Regiment("74", RegimentType.MenAtArms, "bowmen", 358, 343, bows);
        army.SetArmyRegiments([levyRegiment, bowRegiment]);
        SetArmies([], [army]);
        string sourceText = "\tregiments={\n" +
            "\t\t13736={\n\t\t\tmax=477\n\t\t\towner=60671\n\t\t\tsource=hired\n\t\t\tchunks={\n\t\t\t\t{\n\t\t\t\t\tmax=477\n\t\t\t\t\tcurrent=456\n\t\t\t\t}\n\t\t\t}\n\t\t}\n" +
            "\t\t13737={\n\t\t\ttype=bowmen\n\t\t\tmax=358\n\t\t\towner=60671\n\t\t\tsource=hired\n\t\t\tchunks={\n\t\t\t\t{\n\t\t\t\t\tmax=358\n\t\t\t\t\tcurrent=343\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n";
        File.WriteAllText(Writter.DataFilesPaths.Regiments_Path(), sourceText);
        Invoke("ReadRegiments");
        Invoke("CorrectRegimentSoldiers");
        Assert.Equal("456", levy.CurrentNum); Assert.Equal("343", bows.CurrentNum);
        Assert.Equal("477", levy.Max); Assert.Equal("358", bows.Max);
        Assert.True(levy.isMercenary()); Assert.True(bows.isMercenary());
        Assert.Equal("60671", levy.Owner); Assert.Equal("60671", bows.Owner);
        Assert.Equal(477, levyRegiment.StartingNum); Assert.Equal(358, bowRegiment.StartingNum);
        Assert.Equal(456, levyRegiment.CurrentNum); Assert.Equal(343, bowRegiment.CurrentNum);
        Assert.Equal(sourceText, SourceRegimentEditor.Edit(sourceText,
            [new("13736", 0, levy.CurrentNum), new("13737", 0, bows.CurrentNum)]));
        Assert.Throws<InvalidDataException>(() => SourceRegimentEditor.Edit(sourceText, [new("13736", 0, "477")]));
    }

    [Fact]
    public void BoundSourcesKeepUnequalChunkLossesAndHistoricalStartingBelowMaximum()
    {
        Bind(true);
        var first = Source("900", "0", "3", "10");
        var second = Source("900", "1", "7", "10");
        var army = new Army("4", "attacker", true);
        var regiment = Regiment("12", RegimentType.Levy, "Levy", 15, 10, first, second);
        army.SetArmyRegiments([regiment]); SetArmies([army], []);
        Invoke("CorrectRegimentSoldiers");
        Assert.Equal("3", first.CurrentNum); Assert.Equal("7", second.CurrentNum);
        Assert.Equal(15, regiment.StartingNum); Assert.Equal(10, regiment.CurrentNum);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledOrUnboundLegacyPathRetainsHistoricalReconstruction(bool enabled)
    {
        Bind(enabled, bound: false);
        var source = Source("900", "0", "200", "477");
        var army = new Army("4", "attacker", true);
        var regiment = Regiment("12", RegimentType.Levy, "Levy", 300, 200, source);
        army.SetArmyRegiments([regiment]); SetArmies([army], []);
        Invoke("CorrectRegimentSoldiers");
        Assert.Equal("300", source.CurrentNum); // Historical starting is not necessarily maximum.
        Assert.Equal("477", source.Max); Assert.Equal(300, regiment.StartingNum);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoundFightingCommanderKeepsNativeKnightIdentityWithoutDuplicateKnightObject(bool fidelity)
    {
        Bind(fidelity);
        // Membership/cached-fate preservation is required independently of the
        // user's separate choice of personal knight budget calibration.
        StatFidelitySession.Settings.EnableKnightCalibration = false;
        var army = new Army("4", "attacker", true) { CommanderID = "10" };
        var nativeKnight = new ArmyRegiment("12") { CurrentNum = 1, StartingNum = 1 };
        nativeKnight.SetType(RegimentType.Knight, "10"); army.SetArmyRegiments([nativeKnight]); SetArmies([army], []);
        Invoke("RemoveCommandersAsKnights");
        if (!fidelity) { Assert.Empty(army.ArmyRegiments); return; }
        Assert.Same(nativeKnight, Assert.Single(army.ArmyRegiments));
        Assert.True(StatFidelitySession.TryCreateNativeKnights(army, new(), out var ordinaryKnights));
        Assert.Empty(ordinaryKnights.GetKnightsList());
        BattleResult.CharacterFates.Resolve("10", "traits={}", () => new(false, true, "traits={}"));
        BattleResult.SynchronizeNativeKnightRegiments(new[] {army});
        Assert.Equal(0, nativeKnight.CurrentNum); Assert.Equal(1, nativeKnight.StartingNum);
        BattleResult.CharacterFates.Clear();
    }

    private void Bind(bool enabled, bool bound = true)
    {
        options["StatFidelityMode"] = enabled ? "Enabled" : "Disabled";
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-4\nCCSTAT|v1|COMMANDER|attacker|10|0|0.05\nCCSTAT|v1|COMMANDER|defender|20|0|0.05\nCCSTAT|v1|END|snapshot-4");
        if (enabled && bound)
            StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={data={ {flag=ccstat_snapshot_serial data={type=value identity=400000}} {flag=ccstat_combat data={type=com}} }}\n}\n}"), "42", "0");
    }
    private void SetArmies(List<Army> left, List<Army> right) { attackers.SetValue(null, left); defenders.SetValue(null, right); }
    private static void Invoke(string name) => typeof(ArmiesReader).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
    private static Regiment Source(string id, string index, string current, string maximum)
    {
        var source = new Regiment(id, index); source.SetSoldiers(current); source.SetMax(maximum); return source;
    }
    private static ArmyRegiment Regiment(string id, RegimentType type, string name, int starting, int current, params Regiment[] sources)
    {
        var regiment = new ArmyRegiment(id) { StartingNum = starting, CurrentNum = current };
        regiment.SetType(type, name); regiment.SetRegiments(sources.ToList()); return regiment;
    }
    public void Dispose()
    {
        BattleResult.CharacterFates.Clear();
        attackers.SetValue(null, oldAttackers); defenders.SetValue(null, oldDefenders);
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin("");
        if (previousMode == null) options.Remove("StatFidelityMode"); else options["StatFidelityMode"] = previousMode;
        Environment.CurrentDirectory = previousDirectory;
        string resolved = Path.GetFullPath(scratch);
        if (Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
            Directory.Delete(resolved, true);
    }
}
