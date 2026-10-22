using CrusaderWars;
using CrusaderWars.client;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.battle_results.editors;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using System.Reflection;
using Xunit;

public sealed class ResultWritebackTests : IDisposable
{
    private readonly string previous = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-native-result-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> options;
    private readonly string? previousMode;

    public ResultWritebackTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "data", "save_file_data", "temp"));
        Environment.CurrentDirectory = scratch;
        options = (Dictionary<string, string>)typeof(ModOptions).GetField("optionsValuesCollection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        options.TryGetValue("StatFidelityMode", out previousMode);
        options["StatFidelityMode"] = "Enabled";
        StatFidelitySession.Begin("CCSTAT|v1|BEGIN|snapshot-1\nCCSTAT|v1|COMMANDER|attacker|none|0|0.05\nCCSTAT|v1|COMMANDER|defender|none|0|0.05\nCCSTAT|v1|END|snapshot-1");
        StatFidelitySession.ValidateSaveEvidence(new StringReader("living={\n42={\n variables={data={ {flag=ccstat_snapshot_serial data={type=value identity=100000}} {flag=ccstat_combat data={type=com}} }}\n}\n}"), "42", "0");
    }

    [Fact]
    public void FidelityLeavesPriorWarHistoryAndScoreByteEquivalentForNativeCompletion()
    {
        BattleResult.WarID = "1";
        byte[] original = System.Text.Encoding.UTF8.GetBytes("wars={\r\n\t1={\r\n\t\tname=\"英格兰战争\"\r\n\t\twar_score=17.25\r\n\t\tbattle_results={ { war_score=8 size=400 casualties=71 } }\r\n\t}\r\n\t2={ war_score=-6 }\r\n}\r\n");
        File.WriteAllBytes("data/save_file_data/Wars.txt", original);
        BattleResult.EditWarsFile(new(), new(), 26.2308);
        Assert.Equal(original, File.ReadAllBytes("data/save_file_data/temp/Wars.txt"));
        // Repeated import preparation must still preserve the input, not accumulate awards.
        BattleResult.EditWarsFile(new(), new(), 26.2308);
        Assert.Equal(original, File.ReadAllBytes("data/save_file_data/temp/Wars.txt"));
    }

    [Fact]
    public void MissingBoundCombatNeverPublishesAnUnchangedFallback()
    {
        BattleResult.Player_Combat = null; BattleResult.Original_Player_Combat = null;
        File.WriteAllText("data/save_file_data/Combats.txt", "combats={ 9={ untouched=yes } }");
        Assert.Throws<InvalidDataException>(() => BattleResult.EditCombatFile(new(), new()));
        Assert.False(File.Exists("data/save_file_data/temp/Combats.txt"));
        Assert.Equal("combats={ 9={ untouched=yes } }", File.ReadAllText("data/save_file_data/Combats.txt"));
    }

    [Fact]
    public void MissingNativeCombatSideNeverPublishesWinnerOnlyFallback()
    {
        string original = "\t\t0={\n\t\t\tphase=main\n\t\t\tdays=1\n\t\t\tbase_combat_width=50\n\t\t\tattacker={\n\t\t\ttotal_fighting_men=10\n\t\t\t}\n\t\t}\n";
        BattleResult.Player_Combat = original; BattleResult.Original_Player_Combat = original;
        BattleResult.IsAttackerVictorious = true;
        File.WriteAllText("data/save_file_data/Combats.txt", original);
        Assert.Throws<InvalidDataException>(() => BattleResult.EditCombatFile(new(), new()));
        Assert.False(File.Exists("data/save_file_data/temp/Combats.txt"));
        Assert.Equal(original, File.ReadAllText("data/save_file_data/Combats.txt"));
    }

    [Fact]
    public void CombatLocalRegimentIdsDoNotMatchLongerIdsWithTheSamePrefix()
    {
        Army side(string id, string name, string shortId, string longId)
        {
            var army = new Army(id, name, name == "attacker");
            var regular = new ArmyRegiment(longId) { CurrentNum = 47 }; regular.SetType(RegimentType.Levy);
            var knight = new ArmyRegiment(shortId) { CurrentNum = 0 }; knight.SetType(RegimentType.Knight, "123");
            // Process the short ID last so a prefix match cannot be hidden by a later repair.
            army.SetArmyRegiments(new() { regular, knight });
            return army;
        }
        string block(string name, string shortId, string longId) => "\t\t\t" + name + "={\n\t\t\ttotal_fighting_men=48\n\t\t\ttotal_levy_men=47\n\t\t\tregiments={ { regiment=" + shortId + " current=1 } { regiment=" + longId + " current=47 } }\n\t\t\t}\n";
        string original = "\t\t0={\n\t\t\tphase=main\n\t\t\tdays=1\n\t\t\tbase_combat_width=50\n" + block("attacker", "6", "65") + block("defender", "7", "70") + "\t\t}\n";
        BattleResult.Player_Combat = original; BattleResult.Original_Player_Combat = original;
        BattleResult.IsAttackerVictorious = true;
        File.WriteAllText("data/save_file_data/Combats.txt", "combats={\n" + original + "}\n");
        BattleResult.EditCombatFile(new() { side("24", "attacker", "6", "65") }, new() { side("18", "defender", "7", "70") });
        string result = File.ReadAllText("data/save_file_data/temp/Combats.txt");
        Assert.Contains("regiment=6 current=0", result);
        Assert.Contains("regiment=65 current=47", result);
        Assert.Contains("regiment=7 current=0", result);
        Assert.Contains("regiment=70 current=47", result);
    }

    [Fact]
    public void WoundedKnightsRemainAvailableWhileCapturedOrSlainLoseCachedStrength()
    {
        var army = new Army("18", "defender", true);
        var records = new[] { ("58", "10", false, false), ("59", "11", false, true), ("60", "12", true, false) }
            .Select(value => {
                var regiment = new ArmyRegiment(value.Item1) { CurrentNum = 1 };
                regiment.SetType(RegimentType.Knight, value.Item2);
                BattleResult.CharacterFates.Resolve(value.Item2, "traits={31}", () => new(value.Item3, value.Item4, "traits={31 116}"));
                return regiment;
            }).ToList();
        army.SetArmyRegiments(records);
        BattleResult.SynchronizeNativeKnightRegiments(new[] { army });
        Assert.Equal(new[] { 1, 0, 0 }, records.Select(r => r.CurrentNum));
        string source = "army_regiments={\n" + string.Join("", records.Select(r =>
            "\t\t" + r.ID + "={\n\t\t\tcached={\n\t\t\t\tcurrent=1\n\t\t\t\tpower=600\n\t\t\t\tmax_power=600\n\t\t\t}\n\t\t}\n")) + "}\n";
        File.WriteAllText("data/save_file_data/ArmyRegiments.txt", source);
        ArmyRegimentsFileEditor.EditArmyRegimentsFile(new(), new() { army });
        string result = File.ReadAllText("data/save_file_data/temp/ArmyRegiments.txt");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(result, @"(?m)^\t{4}current=0$").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(result, @"(?m)^\t{4}power=0$").Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(result, "max_power=600").Count);
        Assert.Contains("58={\n\t\t\tcached={\n\t\t\t\tcurrent=1\n\t\t\t\tpower=600", result);
    }

    [Fact]
    public void NativeCurrentTotalsExcludeAttilaGuardAndRetinueBodiesAndPreserveInitialCounters()
    {
        var attacker = new Army("24", "attacker", true);
        var defender = new Army("18", "defender", false);
        var a = new ArmyRegiment("1") { CurrentNum = 2973 }; a.SetType(RegimentType.Levy);
        var d = new ArmyRegiment("2") { CurrentNum = 261 }; d.SetType(RegimentType.Levy);
        attacker.SetArmyRegiments(new() { a }); defender.SetArmyRegiments(new() { d });
        attacker.SetUnits(new() { new Unit("Levy", 8002, new Culture("7"), RegimentType.Levy) });
        defender.SetUnits(new() { new Unit("Levy", 4892, new Culture("7"), RegimentType.Levy) });
        string side(string name, string initial, string current) => "\t\t\t" + name + "={\n\t\t\t\tinitial_men=" + initial + "\n\t\t\t\ttotal_fighting_men=" + current + "\n\t\t\t\tinitial_levies=" + initial + "\n\t\t\t\ttotal_levy_men=" + current + "\n\t\t\t\tsoft_casualties=0.75\n\t\t\t}\n";
        string original = "\t\t0={\n\t\t\tphase=main\n\t\t\tdays=1\n\t\t\tbase_combat_width=6207\n" + side("attacker", "8000", "7859.90453") + side("defender", "4837", "4555.56217") + "\t\t}\n";
        BattleResult.Player_Combat = original; BattleResult.Original_Player_Combat = original;
        BattleResult.IsAttackerVictorious = true;
        File.WriteAllText("data/save_file_data/Combats.txt", "combats={\n" + original + "}\n");
        BattleResult.EditCombatFile(new() { attacker }, new() { defender });
        string result = File.ReadAllText("data/save_file_data/temp/Combats.txt");
        Assert.Contains("total_fighting_men=2973", result);
        Assert.Contains("total_fighting_men=261", result);
        Assert.Contains("initial_men=8000", result);
        Assert.Contains("initial_men=4837", result);
        Assert.Contains("initial_levies=8000", result);
        Assert.Contains("initial_levies=4837", result);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(result, @"soft_casualties=0\.75").Count);
    }

    public void Dispose()
    {
        BattleResult.CharacterFates.Clear();
        options["StatFidelityMode"] = "Disabled"; StatFidelitySession.Begin("");
        if (previousMode == null) options.Remove("StatFidelityMode"); else options["StatFidelityMode"] = previousMode;
        Environment.CurrentDirectory = previous;
        if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
    }
}
