using CrusaderWars;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.battle_results.editors;
using CrusaderWars.data.save_file;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class CasualtySmoke
{
    private sealed record Row(string ArmyId, string Script, string Type, string Culture, int Deployed, string Remaining);
    private sealed record Source(string Identity, Army Army, ArmyRegiment ArmyRegiment, Regiment Regiment, int Starting);

    public static object Run(List<Army> attackers, List<Army> defenders, string gamestate, string battleXml, string output)
    {
        string root = Path.Combine(output, "casualty-smoke");
        Directory.CreateDirectory(root);
        string inputHash = Hash(gamestate);
        var armies = attackers.Concat(defenders).SelectMany(a => new[] { a }.Concat(a.MergedArmies)).DistinctBy(a => a.ID).ToArray();
        var sourceUnits = armies.SelectMany(a => a.Units.Where(u => u.SourceIdentity != null).Select(u => (army: a, unit: u))).ToArray();
        var guards = armies.SelectMany(a => a.Units.Where(u => u.CommanderGuardRepresentation != null).Select(u => (army: a, unit: u, metadata: u.CommanderGuardRepresentation!))).ToArray();
        var allSourceStarts = armies.SelectMany(a => a.ArmyRegiments.SelectMany(r => r.Regiments)).Distinct().ToDictionary(r => r, r => r.CurrentNum);
        if (sourceUnits.Length == 0 && !guards.Any(g => g.metadata.DonorType == RegimentType.MenAtArms))
            throw new InvalidOperationException("Casualty smoke requires at least one real source MAA unit or funded MAA guard.");
        var identities = sourceUnits.Select(x => Key(x.unit)).Concat(CrusaderWars.StatFidelity.StatFidelitySession.SiegeSources.Keys)
            .Concat(guards.Where(g => g.metadata.DonorType == RegimentType.MenAtArms).Select(g =>
                Key(g.metadata.ArmyId, g.metadata.SourceArmyRegimentId!, g.metadata.SourceRegimentId!, g.metadata.SourceChunkIndex ?? "0")))
            .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var ratios = identities.Select((id, index) => (id, ratio: new[] { 0.25, 0.5, 0.75 }[index % 3])).ToDictionary(x => x.id, x => x.ratio);
        var sources = armies.SelectMany(a => a.ArmyRegiments.Where(r => r.Type == RegimentType.MenAtArms)
            .SelectMany(r => r.Regiments.Select(s => new Source(Key(a.ID, r.ID, s.ID, s.Index), a, r, s, int.Parse(s.CurrentNum, CultureInfo.InvariantCulture)))))
            .Where(s => ratios.ContainsKey(s.Identity)).ToArray();
        var expected = new Dictionary<string, int>();
        var byUniqueId = sourceUnits.ToDictionary(x => x.unit.UniqueID);
        var guardByUniqueId = guards.ToDictionary(g => g.unit.UniqueID);
        var rows = new List<Row>();
        foreach (var element in XDocument.Load(battleXml).Descendants("unit"))
        {
            string script = element.Attribute("script_name")?.Value ?? "";
            var parsed = Regex.Match(script, @"_army(?<army>\d+)_TYPE(?<type>.+?)_CULTURE(?<culture>[^_]*)_");
            if (!parsed.Success) continue;
            int deployed = int.Parse(element.Attribute("num_soldiers")?.Value ?? "0", CultureInfo.InvariantCulture);
            int remaining = deployed;
            var unitId = Regex.Match(parsed.Groups["type"].Value, @"_(\d+)$");
            if (unitId.Success)
            {
                int id = int.Parse(unitId.Groups[1].Value);
                if (guardByUniqueId.ContainsKey(id)) remaining = checked((int)Math.Round(deployed * 0.6));
                else if (byUniqueId.TryGetValue(id, out var source))
                    remaining = checked((int)Math.Round(deployed * ratios[Key(source.unit)]));
            }
            rows.Add(new Row(parsed.Groups["army"].Value, script, parsed.Groups["type"].Value,
                parsed.Groups["culture"].Value, deployed, remaining.ToString(CultureInfo.InvariantCulture)));
        }
        if (rows.Count == 0) throw new InvalidOperationException("No generated XML unit scripts were available for the casualty smoke.");
        // Independent expected native counts from the actual emitted body reports.
        // Raw crew denominators remain raw at50%, and partial batteries carry their
        // own equipment budget. Unrepresented registered sources remain untouched.
        foreach (var source in sources)
        {
            var emitted = sourceUnits.Where(s => Key(s.unit) == source.Identity).Select(s =>
                (s.unit, row: rows.Single(r => r.Type.EndsWith("_" + s.unit.UniqueID, StringComparison.Ordinal)))).ToArray();
            var sourceGuards = guards.Where(g => g.metadata.DonorType == RegimentType.MenAtArms &&
                Key(g.metadata.ArmyId, g.metadata.SourceArmyRegimentId!, g.metadata.SourceRegimentId!, g.metadata.SourceChunkIndex ?? "0") == source.Identity).ToArray();
            if (sourceGuards.Length > 0)
            {
                var funded = sourceGuards.Single();
                if (funded.metadata.NativeSoldiers != source.Starting) throw new InvalidDataException("Guard source budget differs from the captured native source.");
                Row guardRow = rows.Single(r => r.Type.EndsWith("_" + funded.unit.UniqueID, StringComparison.Ordinal));
                decimal equivalentSurvivors = emitted.Sum(e => int.Parse(e.row.Remaining, CultureInfo.InvariantCulture)) +
                    (decimal)funded.metadata.BorrowedBodies * int.Parse(guardRow.Remaining, CultureInfo.InvariantCulture) / funded.metadata.DeployedBodies;
                expected[source.Identity] = checked((int)decimal.Round(source.Starting * equivalentSurvivors / funded.metadata.CohortDeployedBodies, 0, MidpointRounding.AwayFromZero));
            }
            else if (CrusaderWars.StatFidelity.StatFidelitySession.SiegeSources.ContainsKey(source.Identity))
            {
                int represented = emitted.Sum(e => e.unit.SiegeRepresentation?.SourceMachines ?? throw new InvalidDataException("Synthetic siege fixture lacks exact metadata."));
                decimal survivors = emitted.Sum(e => (decimal)e.unit.SiegeRepresentation!.SourceMachines *
                    int.Parse(e.row.Remaining, CultureInfo.InvariantCulture) / e.row.Deployed);
                expected[source.Identity] = checked(source.Starting - represented + (int)decimal.Round(survivors, 0, MidpointRounding.AwayFromZero));
            }
            else
            {
                double scale = CrusaderWars.client.ModOptions.GetBattleScale() / 100.0;
                long starting = emitted.Sum(e => (long)Math.Round(e.row.Deployed / scale));
                long remaining = emitted.Sum(e => (long)Math.Round(int.Parse(e.row.Remaining, CultureInfo.InvariantCulture) / scale));
                if (starting <= 0) throw new InvalidDataException("Synthetic regular-source fixture has no deployed denominator.");
                expected[source.Identity] = Math.Min(source.Starting, checked((int)Math.Round(source.Starting * Math.Min(1.0, (double)remaining / starting))));
            }
        }
        var chosen = sourceUnits.Length > 0 ? sourceUnits[0] : (army: guards[0].army, unit: guards[0].unit);
        Row chosenRow = rows.Single(r => r.Type.EndsWith("_" + chosen.unit.UniqueID, StringComparison.Ordinal));
        var negative = new List<object>();
        foreach (string kind in new[] { "missing-report", "duplicate-report", "malformed-count" })
        {
            var variant = rows.Where(r => r.ArmyId == chosen.army.ID).ToList();
            if (kind == "missing-report") variant.Remove(chosenRow);
            else if (kind == "duplicate-report") variant.Add(chosenRow);
            else variant[variant.IndexOf(chosenRow)] = chosenRow with { Remaining = "invalid" };
            string logPath = Path.Combine(root, kind + ".synthetic.log");
            WriteLog(logPath, variant);
            bool rejected = false;
            string? reason = null;
            try { Consume(chosen.army, logPath); }
            catch (Exception error) when (error is InvalidOperationException || error is FormatException || error is TargetInvocationException)
            {
                var actual = error is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : error;
                rejected = actual is InvalidOperationException || actual is FormatException;
                reason = actual.Message;
            }
            bool unchanged = allSourceStarts.All(s => s.Key.CurrentNum == s.Value) && Hash(gamestate) == inputHash;
            if (!rejected || !unchanged) throw new InvalidOperationException("Negative casualty smoke did not reject atomically: " + kind);
            negative.Add(new { kind, rejected, sourceCountsUnchanged = unchanged, reason });
            chosen.army.SetCasualitiesReport(new());
        }

        string positiveLog = Path.Combine(root, "positive.synthetic.log");
        WriteLog(positiveLog, rows);
        foreach (var army in armies) Consume(army, positiveLog);
        foreach (var source in sources)
            if (int.Parse(source.Regiment.CurrentNum, CultureInfo.InvariantCulture) != expected[source.Identity])
                throw new InvalidOperationException("Synthetic casualty allocation mismatch for " + source.Identity);

        // Populate all untouched replacement blocks before invoking the real writer.
        // Everything remains under this new replay directory; no Finish()/SaveGame call.
        foreach (string target in Directory.GetFiles(Path.Combine(output, "data", "save_file_data", "temp"), "*.txt"))
        {
            string original = Path.Combine(output, "data", "save_file_data", Path.GetFileName(target));
            if (File.Exists(original)) File.Copy(original, target, true);
        }
        BattleResult.EditRegimentsFile(attackers, defenders);
        ArmyRegimentsFileEditor.EditArmyRegimentsFile(attackers, defenders);
        Writter.SendDataToFile(gamestate);
        string rewritten = Path.Combine(root, "roundtrip.synthetic.gamestate");
        File.Copy(Path.Combine(output, "data", "save_file_data", "gamestate"), rewritten);
        if (Hash(gamestate) != inputHash) throw new InvalidOperationException("The isolated input gamestate was unexpectedly changed.");
        string[] eligibilityBefore = File.ReadLines(gamestate).Where(l => l.TrimStart().StartsWith("can_get_achievements=", StringComparison.Ordinal)).ToArray();
        string[] eligibilityAfter = File.ReadLines(rewritten).Where(l => l.TrimStart().StartsWith("can_get_achievements=", StringComparison.Ordinal)).ToArray();
        if (!eligibilityBefore.SequenceEqual(eligibilityAfter)) throw new InvalidOperationException("Casualty serialization changed an achievement eligibility flag.");

        // Re-extract the produced full save and read actual serialized source counts.
        Data.Reset();
        Reader.ReadFile(rewritten);
        typeof(ArmiesReader).GetMethod("ReadArmyRegiments", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
        typeof(ArmiesReader).GetMethod("ReadRegiments", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
        var reloaded = armies.SelectMany(a => a.ArmyRegiments.Where(r => r.Type == RegimentType.MenAtArms)
            .SelectMany(r => r.Regiments.Select(s => new { identity = Key(a.ID, r.ID, s.ID, s.Index), count = int.Parse(s.CurrentNum, CultureInfo.InvariantCulture) })))
            .Where(s => expected.ContainsKey(s.identity)).ToDictionary(s => s.identity, s => s.count);
        foreach (var pair in expected)
            if (!reloaded.TryGetValue(pair.Key, out int count) || count != pair.Value)
                throw new InvalidOperationException("Serialized casualty roundtrip mismatch for " + pair.Key);
        foreach (var army in armies)
            foreach (var regiment in army.ArmyRegiments.Where(r => r.Type == RegimentType.MenAtArms &&
                         r.Regiments.Any(s => expected.ContainsKey(Key(army.ID, r.ID, s.ID, s.Index)))))
            {
                int sum = regiment.Regiments.Sum(s => int.Parse(s.CurrentNum, CultureInfo.InvariantCulture));
                if (regiment.CurrentNum != sum)
                    throw new InvalidOperationException("Serialized army-regiment total differs from its source chunks: " + regiment.ID);
            }
        var report = new
        {
            success = true, syntheticResults = true, source = "Real converted battle XML plus deterministic artificial survivor reports",
            negativeCases = negative,
            fundedGuards = guards.Select(g => new { g.unit.UniqueID, g.metadata,
                syntheticSurvivors = rows.Single(r => r.Type.EndsWith("_" + g.unit.UniqueID, StringComparison.Ordinal)).Remaining }),
            sources = sources.Select(s => new { s.Identity, starting = s.Starting, survivalFraction = ratios[s.Identity], expected = expected[s.Identity], reloaded = reloaded[s.Identity],
                siege = sourceUnits.Any(u => Key(u.unit) == s.Identity && u.unit.SiegeRepresentation != null) || CrusaderWars.StatFidelity.StatFidelitySession.SiegeSources.ContainsKey(s.Identity),
                formations = sourceUnits.Where(u => Key(u.unit) == s.Identity).Select(u => new {u.unit.UniqueID,u.unit.SiegeRepresentation}) }),
            inputGamestateUnchanged = Hash(gamestate) == inputHash,
            armyRegimentTotalsMatch = true,
            achievementEligibilityFlagsPreserved = true,
            achievementEligibilityFlagsChecked = eligibilityBefore.Length,
            outputGamestateSha256 = Hash(rewritten), personalCampaignWritten = false,
            limitation = "Verifies source attribution and isolated save serialization only; these are not Attila battle outcomes or a game-load test."
        };
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }

    private static void Consume(Army army, string log)
    {
        var (alive, kills) = BattleResultReader.GetRemainingAndKills(log);
        var returnList = typeof(CasualtyProcessor).GetMethod("ReturnList", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dataType = typeof(CasualtyProcessor).GetNestedType("DataType", BindingFlags.NonPublic)!;
        army.UnitsResults = new UnitsResults();
        army.UnitsResults.SetAliveMainPhase((List<(string, string, string, string)>)returnList.Invoke(null, new[] { army, alive.Single(), Enum.Parse(dataType, "Alive") })!);
        army.UnitsResults.SetKillsMainPhase((List<(string, string, string, string)>)returnList.Invoke(null, new[] { army, kills.Single(), Enum.Parse(dataType, "Kills") })!);
        army.UnitsResults.ScaleTo100Porcent();
        typeof(CasualtyProcessor).GetMethod("CreateUnitsReports", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { army });
        typeof(CasualtyProcessor).GetMethod("ChangeRegimentsSoldiers", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { army });
    }

    private static void WriteLog(string path, IEnumerable<Row> records)
    {
        var rows = records.ToArray();
        var text = new StringBuilder("SYNTHETIC OFFLINE TEST RESULTS - NOT AN ATTILA BATTLE\n-----REMAINING SOLDIERS-----!!\n");
        foreach (var row in rows) text.AppendLine(row.Script + "-" + row.Remaining);
        text.AppendLine("-----NUMBERS OF KILLS-----!!");
        foreach (var row in rows) text.AppendLine("kills_" + row.Script + "-0");
        text.AppendLine("-----PRINT ENDED-----!!");
        File.WriteAllText(path, text.ToString());
    }
    private static string Key(Unit unit) => Key(unit.SourceArmyId!, unit.SourceArmyRegimentId!, unit.SourceRegimentId!, unit.SourceChunkIndex ?? "");
    private static string Key(string army, string armyRegiment, string regiment, string index) => army + ":" + armyRegiment + ":" + regiment + ":" + (string.IsNullOrEmpty(index) ? "0" : index);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
