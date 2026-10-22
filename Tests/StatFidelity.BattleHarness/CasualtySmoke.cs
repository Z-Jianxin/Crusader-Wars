using CrusaderWars;
using CrusaderWars.data.battle_results;
using CrusaderWars.data.battle_results.editors;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
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

    public static object Run(List<Army> attackers, List<Army> defenders, string gamestate, string battleXml, string output, string mode = "partial", bool attackerWins = true)
    {
        if (mode != "none" && mode != "partial") throw new ArgumentException("Synthetic casualty mode must be none or partial.");
        if (!StatFidelitySession.HasNativePopulationBudgets) throw new InvalidOperationException("Active/soft smoke requires native population evidence.");
        string root = Path.Combine(output, "casualty-smoke");
        Directory.CreateDirectory(root);
        string inputHash = Hash(gamestate);
        var armies = attackers.Concat(defenders).SelectMany(a => new[] { a }.Concat(a.MergedArmies)).DistinctBy(a => a.ID).ToArray();
        var sourceUnits = armies.SelectMany(a => a.Units.Where(u => u.SourceIdentity != null).Select(u => (army: a, unit: u))).ToArray();
        var guards = armies.SelectMany(a => a.Units.Where(u => u.CommanderGuardRepresentation != null).Select(u => (army: a, unit: u, metadata: u.CommanderGuardRepresentation!))).ToArray();
        var levyUnits = armies.SelectMany(a => a.Units.Where(u => u.GetRegimentType() == RegimentType.Levy).Select(u => (army: a, unit: u))).ToArray();
        var allSourceStarts = armies.SelectMany(a => a.ArmyRegiments.SelectMany(r => r.Regiments)).Distinct().ToDictionary(r => r, r => r.CurrentNum);
        if (sourceUnits.Length == 0 && guards.Length == 0 && levyUnits.Length == 0)
            throw new InvalidOperationException("Casualty smoke requires at least one real source formation or funded guard.");
        var sources = armies.SelectMany(a => a.ArmyRegiments.Where(r => r.Type is RegimentType.MenAtArms or RegimentType.Levy)
            .SelectMany(r => r.Regiments.Select(s => new Source(Key(a.ID, r.ID, s.ID, s.Index), a, r, s, int.Parse(s.CurrentNum, CultureInfo.InvariantCulture)))))
            .ToArray();
        var identities = sources.Select(s => s.Identity).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var ratios = identities.Select((id, index) => (id, ratio: new[] { 0.25, 0.5, 0.75 }[index % 3])).ToDictionary(x => x.id, x => x.ratio);
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
            if (unitId.Success && mode == "partial")
            {
                int id = int.Parse(unitId.Groups[1].Value);
                if (guardByUniqueId.TryGetValue(id, out var guard))
                {
                    bool personalKnight = StatFidelitySession.NativePopulationBudgets.Any(b => b.ArmyId == guard.army.ID && b.IsKnight &&
                        b.InitialActive > 0 && b.NativeType == guard.unit.GetCharacterID());
                    if (!personalKnight) remaining = checked((int)Math.Round(deployed * 0.6));
                }
                else if (byUniqueId.TryGetValue(id, out var source))
                    remaining = checked((int)Math.Round(deployed * ratios[Key(source.unit)]));
                else if (levyUnits.Any(u => u.unit.UniqueID == id))
                    remaining = checked((int)Math.Round(deployed * new[] {0.25, 0.5, 0.75}[id % 3]));
            }
            rows.Add(new Row(parsed.Groups["army"].Value, script, parsed.Groups["type"].Value,
                parsed.Groups["culture"].Value, deployed, remaining.ToString(CultureInfo.InvariantCulture)));
        }
        if (rows.Count == 0) throw new InvalidOperationException("No generated XML unit scripts were available for the casualty smoke.");
        // Independent expectation: reconstruct exact A/S source shares from immutable
        // aggregate native inputs and emitted raw bodies. Do not call production Project.
        var expectedPopulation = new Dictionary<string, (decimal Active, decimal Soft, int Stock)>();
        foreach (var budget in StatFidelitySession.NativePopulationBudgets.Where(b => !b.IsKnight))
        {
            var active = new PopulationFraction[budget.Sources.Count];
            var weights = new PopulationFraction[budget.Sources.Count];
            for (int index = 0; index < budget.Sources.Count; index++)
            {
                var source = budget.Sources[index];
                var share = budget.Stock == 0 ? default : F(source.Stock) / F(budget.Stock);
                var ratio = budget.InitialActive == 0 || source.Stock == 0 ? default : Ratio(budget, source);
                active[index] = PopulationFraction.FromDecimal(budget.InitialActive) * share * ratio;
                weights[index] = active[index] + PopulationFraction.FromDecimal(budget.PreservedSoft) * share;
            }
            decimal remainingActive = active.Aggregate(default(PopulationFraction), (sum, n) => sum + n).TruncateNative5();
            int stock = checked((int)decimal.Ceiling(remainingActive + budget.PreservedSoft));
            var allocated = ExpectedAllocation(weights, budget.Sources.Select(s => s.Stock).ToArray(), stock);
            for (int index = 0; index < allocated.Length; index++) expected[budget.Sources[index].Identity] = allocated[index];
            expectedPopulation.Add(budget.Identity, (remainingActive, budget.PreservedSoft, stock));
        }
        foreach (var equipment in StatFidelitySession.NativeEquipmentInputs)
        foreach (var nativeSource in equipment.Sources)
        {
            string identity = Key(equipment.ArmyId, equipment.ArmyRegimentId, nativeSource.RegimentId, nativeSource.ChunkIndex);
            var emitted = sourceUnits.Where(s => Key(s.unit) == identity).ToArray();
            int represented = emitted.Sum(e => e.unit.SiegeRepresentation?.SourceMachines ?? throw new InvalidDataException("Missing exact equipment metadata."));
            var alive = emitted.Aggregate(default(PopulationFraction), (sum, e) => {
                var row = ForUnit(e.unit); return sum + F(e.unit.SiegeRepresentation!.SourceMachines) * F(int.Parse(row.Remaining)) / F(row.Deployed);
            });
            expected[identity] = checked(nativeSource.Stock - represented + (alive + PopulationFraction.FromDecimal(0.5m)).FloorInt32());
        }
        if (sources.Any(s => !expected.ContainsKey(s.Identity))) throw new InvalidDataException("Synthetic expectation lacks native source coverage.");
        if (mode == "none" && sources.Any(s => expected[s.Identity] != s.Starting)) throw new InvalidDataException("No-loss control failed original source-stock identity.");

        Row ForUnit(Unit unit) => rows.Single(r => r.Type.EndsWith("_" + unit.UniqueID, StringComparison.Ordinal));
        PopulationFraction Ratio(NativePopulationBudget budget, NativePopulationSourceBudget source)
        {
            var units = budget.Kind == RegimentType.Levy
                ? levyUnits.Where(u => u.army.ID == budget.ArmyId && u.unit.GetObjCulture()?.ID == source.CultureId).Select(u => u.unit).ToArray()
                : sourceUnits.Where(u => Key(u.unit) == source.Identity).Select(u => u.unit).ToArray();
            var donorGuards = guards.Where(g => g.army.ID == budget.ArmyId && g.metadata.DonorType == budget.Kind &&
                (budget.Kind == RegimentType.Levy ? g.metadata.DonorCultureId == source.CultureId :
                    Key(g.metadata.ArmyId,g.metadata.SourceArmyRegimentId!,g.metadata.SourceRegimentId!,g.metadata.SourceChunkIndex ?? "0") == source.Identity)).ToArray();
            int bodies = units.Sum(u => u.GetSoldiers()) + donorGuards.Sum(g => g.metadata.BorrowedBodies);
            if (bodies <= 0) throw new InvalidDataException("Positive active source has no synthetic raw bodies: " + source.Identity);
            var alive = units.Aggregate(default(PopulationFraction), (sum,u) => sum + F(int.Parse(ForUnit(u).Remaining)));
            foreach (var guard in donorGuards)
            {
                var row = ForUnit(guard.unit); alive += F(guard.metadata.BorrowedBodies) * F(int.Parse(row.Remaining)) / F(row.Deployed);
            }
            return alive / F(bodies);
        }
        var chosen = sourceUnits.Length > 0 ? sourceUnits[0] : guards.Length > 0 ? (army: guards[0].army, unit: guards[0].unit) : levyUnits[0];
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
            catch (Exception error) when (error is InvalidOperationException || error is FormatException || error is InvalidDataException || error is TargetInvocationException)
            {
                var actual = error is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : error;
                rejected = actual is InvalidOperationException || actual is FormatException || actual is InvalidDataException;
                reason = actual.Message;
            }
            bool unchanged = allSourceStarts.All(s => s.Key.CurrentNum == s.Value) && Hash(gamestate) == inputHash;
            if (!rejected || !unchanged) throw new InvalidOperationException("Negative casualty smoke did not reject atomically: " + kind);
            negative.Add(new { kind, rejected, sourceCountsUnchanged = unchanged, reason });
            chosen.army.SetCasualitiesReport(new());
        }

        string positiveLog = Path.Combine(root, "positive.synthetic.log");
        WriteLog(positiveLog, rows);
        var raw = BattleResultReader.GetRemainingAndKills(positiveLog);
        StatFidelitySession.ValidateResultReports(raw.AliveList, raw.KillsList);
        foreach (var army in armies) Consume(army, positiveLog);
        foreach (var source in sources)
            if (int.Parse(source.Regiment.CurrentNum, CultureInfo.InvariantCulture) != expected[source.Identity])
                throw new InvalidOperationException("Synthetic casualty allocation mismatch for " + source.Identity);

        File.WriteAllText(Path.Combine(output, "development-runtime.json"), "{\"syntheticOfflinePopulationControl\":true}");
        StatFidelitySession.FinalizeNativePopulationOutcomes(armies, syntheticUnharmedControl: true);
        foreach (var outcome in StatFidelitySession.NativePopulationOutcomes.Where(o => !o.IsKnight))
        {
            var e = expectedPopulation[outcome.Budget.Identity];
            if (outcome.FinalActiveRemaining != e.Active || outcome.FinalSoft != e.Soft || outcome.ReturnedStock != e.Stock)
                throw new InvalidDataException("Independent active/soft expectation differs: " + outcome.Budget.Identity);
        }
        StatFidelitySession.WriteCoverageReport(output);
        // Populate all untouched replacement blocks before invoking the real writer.
        // Everything remains under this new replay directory; no Finish()/SaveGame call.
        foreach (string target in Directory.GetFiles(Path.Combine(output, "data", "save_file_data", "temp"), "*.txt"))
        {
            string original = Path.Combine(output, "data", "save_file_data", Path.GetFileName(target));
            if (File.Exists(original)) File.Copy(original, target, true);
        }
        CrusaderWars.Properties.Settings.Default.VAR_log_attila = positiveLog;
        BattleResult.IsAttackerVictorious = attackerWins;
        BattleResult.EditCombatFile(attackers, defenders);
        BattleResult.EditCombatResultsFile(attackers, defenders);
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
        var reloaded = armies.SelectMany(a => a.ArmyRegiments.Where(r => r.Type is RegimentType.MenAtArms or RegimentType.Levy)
            .SelectMany(r => r.Regiments.Select(s => new { identity = Key(a.ID, r.ID, s.ID, s.Index), count = int.Parse(s.CurrentNum, CultureInfo.InvariantCulture) })))
            .Where(s => expected.ContainsKey(s.identity)).ToDictionary(s => s.identity, s => s.count);
        foreach (var pair in expected)
            if (!reloaded.TryGetValue(pair.Key, out int count) || count != pair.Value)
                throw new InvalidOperationException("Serialized casualty roundtrip mismatch for " + pair.Key);
        foreach (var army in armies)
            foreach (var regiment in army.ArmyRegiments.Where(r => (r.Type is RegimentType.MenAtArms or RegimentType.Levy) &&
                         r.Regiments.Any(s => expected.ContainsKey(Key(army.ID, r.ID, s.ID, s.Index)))))
            {
                int sum = regiment.Regiments.Sum(s => int.Parse(s.CurrentNum, CultureInfo.InvariantCulture));
                if (regiment.CurrentNum != sum)
                    throw new InvalidOperationException("Serialized army-regiment total differs from its source chunks: " + regiment.ID);
            }
        var report = new
        {
            success = true, mode, winner = attackerWins ? "attacker" : "defender", syntheticResults = true, source = "Real converted battle XML plus deterministic artificial survivor reports",
            negativeCases = negative,
            nativePopulation = expectedPopulation.Select(p => new {identity=p.Key, expectedActive=p.Value.Active, preservedSoft=p.Value.Soft, expectedStock=p.Value.Stock}),
            fundedGuards = guards.Select(g => new { g.unit.UniqueID, g.metadata,
                syntheticSurvivors = rows.Single(r => r.Type.EndsWith("_" + g.unit.UniqueID, StringComparison.Ordinal)).Remaining }),
            sources = sources.Select(s => new { s.Identity, regimentType = s.ArmyRegiment.Type.ToString(), starting = s.Starting,
                survivalFraction = mode == "none" ? 1 : s.ArmyRegiment.Type == RegimentType.Levy ? (double?)null : ratios[s.Identity], expected = expected[s.Identity], reloaded = reloaded[s.Identity],
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

    private static PopulationFraction F(int value) => PopulationFraction.FromInt(value);
    private static int[] ExpectedAllocation(PopulationFraction[] weights, int[] caps, int target)
    {
        var result = weights.Select(w => w.FloorInt32()).ToArray();
        int left = target - result.Sum();
        if (left < 0 || target > caps.Sum()) throw new InvalidDataException("Invalid independent joint-stock target.");
        while (left > 0)
        {
            int[] order = Enumerable.Range(0, weights.Length).Where(i => result[i] < caps[i])
                .OrderByDescending(i => weights[i] - F(result[i])).ThenBy(i => i).ToArray();
            if (order.Length == 0) throw new InvalidDataException("Independent source allocation exceeded native capacity.");
            foreach (int index in order.Take(left)) { result[index]++; left--; }
        }
        return result;
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
