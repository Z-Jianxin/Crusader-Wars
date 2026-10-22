using CrusaderWars;
using CrusaderWars.data.save_file;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.battle_results.editors;
using CrusaderWars.StatFidelity;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ActualImportReplay
{
    public static object Complete(List<Army> attackers, List<Army> defenders, string gamestate, string output, string evidencePath)
    {
        var armies = attackers.Concat(defenders).ToList();
        string originalInputHash = Hash(gamestate);
        string nativeLog = Path.Combine(output, "inputs", "native-attila-result.txt");
        CrusaderWars.Properties.Settings.Default.VAR_log_attila = nativeLog;
        foreach (var army in armies)
        {
            CasualtyProcessor.CheckForSlainCommanders(army, nativeLog);
            CasualtyProcessor.CheckKnightsKills(army);
            CasualtyProcessor.CheckForSlainKnights(army);
        }
        var left = ArmiesReader.GetSideArmies("left", attackers, defenders)!;
        var right = ArmiesReader.GetSideArmies("right", attackers, defenders)!;
        string winner = BattleResultReader.GetAttilaWinner(nativeLog, left[0].CombatSide, right[0].CombatSide);
        BattleResult.IsAttackerVictorious = winner == "attacker";
        BattleResult.WarID = ArmiesReader.FindWarID();
        var decisions = ReadObservedDecisions(evidencePath, output);
        BattleResult.DetermineCharacterFates(armies, decisions);
        BattleResult.SynchronizeNativeKnightRegiments(armies);
        BattleResult.CalculateAndSetWarScore(attackers, defenders);
        var resolved = BattleResult.CharacterFates.Entries.ToDictionary(p => p.Key, p => p.Value);
        foreach (string target in Directory.GetFiles(Path.Combine(output, "data", "save_file_data", "temp"), "*.txt"))
        {
            string source = Path.Combine(output, "data", "save_file_data", Path.GetFileName(target));
            if (File.Exists(source)) File.Copy(source, target, true);
        }
        BattleResult.EditVassalContractsFile();
        BattleResult.EditLivingFile(attackers, defenders);
        BattleResult.EditCombatFile(attackers, defenders);
        BattleResult.EditCombatResultsFile(attackers, defenders);
        BattleResult.EditRegimentsFile(attackers, defenders);
        ArmyRegimentsFileEditor.EditArmyRegimentsFile(attackers, defenders);
        if (BattleResult.WarScoreValue.HasValue) BattleResult.EditWarsFile(attackers, defenders, BattleResult.WarScoreValue.Value);
        bool preservedNativeWarState = File.ReadAllBytes(Writter.DataFilesPaths.Wars_Path())
            .SequenceEqual(File.ReadAllBytes(Writter.DataTEMPFilesPaths.Wars_Path()));
        if (!preservedNativeWarState) throw new InvalidDataException("Fidelity import altered native war history or score before CK3 completion.");
        File.Copy(Path.Combine(output, "inputs", "handoff.ck3"), Path.Combine(output, "data", "save_file_data", "last_save.zip"));
        Directory.CreateDirectory(Path.Combine(output, "data", "attila_battle"));
        File.Copy(Path.Combine(output, "inputs", "console.log"), Path.Combine(output, "data", "attila_battle", "battle_log_snippet.txt"));
        BattleResult.SendToSaveFile(gamestate);
        if (Hash(gamestate) != originalInputHash) throw new InvalidOperationException("Original isolated native input was changed.");
        Type saveFile = typeof(HomePage).Assembly.GetType("CrusaderWars.data.save_file.SaveFile")!;
        saveFile.GetMethod("Compress", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
        saveFile.GetMethod("Finish", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
        string manifest = Directory.GetFiles(Path.Combine(output, "results"), "result-manifest.json", SearchOption.AllDirectories).Single();
        return new {
            actualNativeAar = true, preservedObservedFates = true, syntheticOutcome = false,
            gameLaunched = false, personalCampaignWritten = false, isolatedInputUnchanged = true,
            preservedNativeWarState, warScorePendingNativeResolution = true,
            winner, observedFateEvidenceSha256 = Hash(evidencePath), resolvedFates = resolved,
            resultManifest = manifest, validationLimit = "Corrected offline import of exact actual native AAR and original logged fate decisions; native CK3 reload is a separate check."
        };
    }

    private static Dictionary<string, ResolvedCharacterFate> ReadObservedDecisions(string evidencePath, string output)
    {
        using var evidence = JsonDocument.Parse(File.ReadAllText(evidencePath));
        var root = evidence.RootElement;
        if (root.GetProperty("snapshot").GetString() != StatFidelitySession.Export.BattleId)
            throw new InvalidDataException("Original fate evidence belongs to a different native snapshot.");
        string debugPath = root.GetProperty("actualDebugPath").GetString()!;
        if (!Hash(debugPath).Equals(root.GetProperty("actualDebugSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Original native app fate log hash differs.");
        string captureRoot = Directory.GetParent(Path.GetDirectoryName(debugPath)!)!.FullName;
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(captureRoot, "evidence.json")));
        using var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "inputs.json")));
        if (!capture.RootElement.GetProperty("inputSaveSha256").GetString()!.Equals(inputs.RootElement.GetProperty("saveSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Original fate evidence is not bound to this native input save.");
        string[] debugLines = File.ReadAllLines(debugPath);
        var evidenceEntries = root.GetProperty("entries").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var originalTraits = new Dictionary<string, string[]>();
        string current = "";
        foreach (string line in File.ReadLines(Writter.DataFilesPaths.Living_Path()))
        {
            var id = Regex.Match(line, @"^(\d+)={");
            if (id.Success) current = id.Groups[1].Value;
            if (line == "}") current = "";
            if (evidenceEntries.ContainsKey(current) && line.StartsWith("\ttraits={"))
                originalTraits.Add(current, Regex.Matches(line, @"\d+").Select(m => m.Value).ToArray());
        }
        var result = new Dictionary<string, ResolvedCharacterFate>();
        foreach (var pair in evidenceEntries)
        {
            var entry = pair.Value;
            string[] original = entry.GetProperty("originalTraits").EnumerateArray().Select(v => v.GetString()!).ToArray();
            if (!originalTraits.TryGetValue(pair.Key, out var actualOriginal) || !actualOriginal.SequenceEqual(original))
                throw new InvalidDataException("Observed fate source traits do not match native input character " + pair.Key);
            foreach (var sourceLine in entry.GetProperty("sourceLines").EnumerateArray())
                if (debugLines[sourceLine.GetProperty("line").GetInt32() - 1] != sourceLine.GetProperty("text").GetString())
                    throw new InvalidDataException("Observed fate log line changed for " + pair.Key);
            string[] traits = entry.GetProperty("expectedTraitMembership").EnumerateArray().Select(v => v.GetString()!).ToArray();
            if (traits.Any(t => !Regex.IsMatch(t, @"^\d+$"))) throw new InvalidDataException("Malformed observed trait evidence.");
            result.Add(pair.Key, new ResolvedCharacterFate(entry.GetProperty("isSlain").GetBoolean(), entry.GetProperty("isCaptured").GetBoolean(),
                "\ttraits={ " + string.Join(" ", traits) + " }"));
        }
        File.Copy(evidencePath, Path.Combine(output, "inputs", "original-fate-evidence.json"));
        return result;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
