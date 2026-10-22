using CrusaderWars;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ActualCasualtyReplay
{
    public static object Run(IEnumerable<Army> sourceArmies, string originalLog, string output)
    {
        string path = Path.Combine(output, "inputs", "native-attila-result.txt");
        File.Copy(originalLog, path);
        var (alive, kills) = BattleResultReader.GetRemainingAndKills(path);
        var validated = StatFidelitySession.ValidateResultReports(alive, kills)
            ?? throw new InvalidOperationException("Real result replay requires bound stat fidelity evidence.");
        var negative = new List<object>();
        var examples = new Dictionary<string, Func<string, bool>> {
            ["levy"] = s => s.Contains("_TYPELevy_"),
            ["general"] = s => s.Contains("_TYPEcommander") && !s.Contains("cc_untracked"),
            ["untracked-general"] = s => s.Contains("_TYPEcommandercc_untracked"),
            ["knights"] = s => s.Contains("_TYPEknights_"),
            ["maa"] = s => s.Contains("_TYPEarmored_footmen_")
        };
        foreach (var example in examples)
        {
            var formation = StatFidelitySession.DeployedRoster.FirstOrDefault(f => example.Value(f.ScriptName));
            if (formation == null) continue;
            var originalRows = alive[0].Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.TrimEnd('\r')).ToArray();
            string originalRow = originalRows.Single(l => l.StartsWith(formation.ScriptName + "-", StringComparison.Ordinal));
            foreach (string kind in new[] { "missing", "duplicate", "malformed", "out-of-bounds" })
            {
                var rows = originalRows.Where(l => l != originalRow).ToList();
                if (kind == "duplicate") { rows.Add(originalRow); rows.Add(originalRow); }
                if (kind == "malformed") rows.Add(formation.ScriptName + "-invalid");
                if (kind == "out-of-bounds") rows.Add(formation.ScriptName + "-" + (formation.InitialSoldiers + 1));
                var changed = alive.ToArray(); changed[0] = string.Join("\n", rows);
                bool rejected = false;
                try { BattleReportEvidence.Validate(StatFidelitySession.DeployedRoster, changed, kills); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Real-roster negative check was accepted: " + example.Key + "/" + kind);
                negative.Add(new { formationKind = example.Key, kind, rejected, syntheticMutation = true });
            }
        }
        var armies = sourceArmies.ToArray();
        var method = typeof(CasualtyProcessor).GetMethod("ReturnList", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dataType = typeof(CasualtyProcessor).GetNestedType("DataType", BindingFlags.NonPublic)!;
        foreach (var army in armies)
        {
            List<(string, string, string, string)> parse(string block, string type) =>
                (List<(string, string, string, string)>)method.Invoke(null, new[] { army, block, Enum.Parse(dataType, type) })!;
            int main = alive.Count > 1 ? alive.Count - 2 : 0;
            army.UnitsResults = new UnitsResults();
            army.UnitsResults.SetAliveMainPhase(parse(alive[main], "Alive"));
            army.UnitsResults.SetKillsMainPhase(parse(kills[main], "Kills"));
            if (alive.Count > 1)
            {
                army.UnitsResults.SetAlivePursuitPhase(parse(alive[^1], "Alive"));
                army.UnitsResults.SetKillsPursuitPhase(parse(kills[^1], "Kills"));
            }
            army.UnitsResults.ScaleTo100Porcent();
            typeof(CasualtyProcessor).GetMethod("CreateUnitsReports", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { army });
            int expected = StatFidelitySession.DeployedRoster.Count(f => f.ScriptName.Contains("_army" + army.ID + "_TYPE", StringComparison.Ordinal));
            if (army.CasualitiesReports.Count != expected)
                throw new InvalidOperationException("Actual deployed/report coverage mismatch in army " + army.ID + ": " + expected + " vs " + army.CasualitiesReports.Count);
            typeof(CasualtyProcessor).GetMethod("ChangeRegimentsSoldiers", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { army });
        }
        var report = new {
            actualNativeReports = true, syntheticOutcome = false, campaignSaveWritten = false, characterFatesReplayedInThisTroopPhase = false,
            nativeLogSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            snapshot = StatFidelitySession.Export.BattleId, formations = StatFidelitySession.DeployedRoster.Count,
            phases = validated.SurvivorPhases.Count, negative,
            armies = armies.Select(a => new {
                a.ID, a.CombatSide, emittedReports = a.CasualitiesReports.Count,
                deployed = a.CasualitiesReports.Sum(r => r.GetStarting()),
                survivors = a.CasualitiesReports.Sum(r => r.GetAliveAfterPursuit() >= 0 ? r.GetAliveAfterPursuit() : r.GetAliveBeforePursuit()),
                sources = a.ArmyRegiments.Where(ar => ar.Type is RegimentType.MenAtArms or RegimentType.Levy).SelectMany(ar => ar.Regiments.Select(r => new {
                    armyRegiment = ar.ID, source = r.ID, chunk = r.Index, type = ar.Type.ToString(),
                    survivingSoldiers = int.Parse(r.CurrentNum, CultureInfo.InvariantCulture)
                }))
            })
        };
        File.WriteAllText(Path.Combine(output, "actual-casualty-replay.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }
}
