using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using CrusaderWars.data.save_file;
using static CrusaderWars.data.save_file.Writter;
using CrusaderWars.twbattle; // Added for BattleFile access
using System.Globalization; // Added for CultureInfo
using CrusaderWars.armies; // Added for List<Army>
using CrusaderWars.unit_mapper;
using CrusaderWars.locs;
using CrusaderWars.client;
using System.Runtime.Versioning;
using System.Windows.Forms;


namespace CrusaderWars.data.battle_results
{
    public static class CasualtyProcessor
    {
        private enum DataType
        {
            Alive,
            Kills
        }

        [SupportedOSPlatform("windows")]
        public static void ReadAttilaResults(Army army, string path_attila_log)
        {
            Program.Logger.Debug($"Reading Attila results for army {army.ID} from log: {path_attila_log}");
            try
            {
                UnitsResults units = new UnitsResults();
                List<(string Script, string Type, string CultureID, string Remaining)> Alive_MainPhase =
                    new List<(string Script, string Type, string CultureID, string Remaining)>();
                List<(string Script, string Type, string CultureID, string Remaining)> Alive_PursuitPhase =
                    new List<(string Script, string Type, string CultureID, string Remaining)>();
                List<(string Script, string Type, string CultureID, string Kills)> Kills_MainPhase =
                    new List<(string Name, string Type, string CultureID, string Kills)>();
                List<(string Script, string Type, string CultureID, string Kills)> Kills_PursuitPhase =
                    new List<(string Script, string Type, string CultureID, string Kills)>();

                var (AliveList, KillsList) = BattleResultReader.GetRemainingAndKills(path_attila_log);
                StatFidelity.StatFidelitySession.ValidateResultReports(AliveList, KillsList);
                if (AliveList.Count == 0)
                {
                    Program.Logger.Debug(
                        $"Warning: No battle reports found in Attila log for army {army.ID}. Assuming no survivors or battle did not generate logs.");
                }
                else if (AliveList.Count == 1)
                {
                    Program.Logger.Debug($"Single battle phase detected for army {army.ID}.");
                    Alive_MainPhase = ReturnList(army, AliveList.Last(), DataType.Alive);
                    units.SetAliveMainPhase(Alive_MainPhase);
                    Kills_MainPhase = ReturnList(army, KillsList.Last(), DataType.Kills);
                    units.SetKillsMainPhase(Kills_MainPhase);

                }
                else if (AliveList.Count > 1)
                {
                    Program.Logger.Debug(
                        $"Multiple battle reports found for army {army.ID}. Using the last two reports for Main and Pursuit phases.");
                    // Use the second-to-last report for the Main Phase
                    Alive_MainPhase = ReturnList(army, AliveList[AliveList.Count - 2], DataType.Alive);
                    units.SetAliveMainPhase(Alive_MainPhase);

                    // Use the very last report for the Pursuit Phase
                    Alive_PursuitPhase = ReturnList(army, AliveList.Last(), DataType.Alive);
                    units.SetAlivePursuitPhase(Alive_PursuitPhase);

                    Kills_MainPhase = ReturnList(army, KillsList[KillsList.Count - 2], DataType.Kills);
                    units.SetKillsMainPhase(Kills_MainPhase);

                    Kills_PursuitPhase = ReturnList(army, KillsList.Last(), DataType.Kills);
                    units.SetKillsPursuitPhase(Kills_PursuitPhase);
                }

                army.UnitsResults = units;
                army.UnitsResults.ScaleTo100Porcent();

                CreateUnitsReports(army);
                ChangeRegimentsSoldiers(army);
            }
            catch (Exception e)
            {
                Program.Logger.Debug($"Error reading Attila results for army {army.ID}: {e.ToString()}");
                MessageBox.Show($"Error reading Attila results: {e.ToString()}",
                    "Crusader Conflicts: Battle Results Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.DefaultDesktopOnly);
                throw new Exception();

            }

        }

        private static List<(string, string, string, string)> ReturnList(Army army, string text, DataType list_type)
        {
            Program.Logger.Debug($"Entering ReturnList for army {army.ID}, data type: {list_type}");
            var list = new List<(string, string, string, string)>();

            MatchCollection pattern;
            switch (list_type)
            {
                case DataType.Alive:
                    pattern = Regex.Matches(text, StatFidelity.StatFidelitySession.HasBoundExport
                        ? $@"(?m)^(?<Unit>.+_army{army.ID}_TYPE(?<Type>.+?)_CULTURE(?<Culture>.*?)_.+)-(?<Remaining>\d+)\r?$"
                        : $@"(?<Unit>.+_army{army.ID}_TYPE(?<Type>.+?)_CULTURE(?<Culture>.+?)_.+)-(?<Remaining>.+)");
                    foreach (Match match in pattern)
                    {
                        string unit_script = match.Groups["Unit"].Value;
                        string remaining = match.Groups["Remaining"].Value;
                        string culture_id = match.Groups["Culture"].Value;
                        string type = match.Groups["Type"].Value;

                        list.Add((unit_script, type, culture_id, remaining));
                    }

                    break;
                case DataType.Kills:
                    pattern = Regex.Matches(text, StatFidelity.StatFidelitySession.HasBoundExport
                        ? $@"(?m)^(?<Unit>kills_.+_army{army.ID}_TYPE(?<Type>.+?)_CULTURE(?<Culture>.*?)_.+)-(?<Kills>\d+)\r?$"
                        : $@"(?<Unit>kills_.+_army{army.ID}_TYPE(?<Type>.+?)_CULTURE(?<Culture>.+?)_.+)-(?<Kills>.+)");
                    foreach (Match match in pattern)
                    {

                        string unit_script = match.Groups["Unit"].Value;
                        string kills = match.Groups["Kills"].Value;
                        string culture_id = match.Groups["Culture"].Value;
                        string type = match.Groups["Type"].Value;

                        list.Add((unit_script, type, culture_id, kills));
                    }

                    break;
            }

            Program.Logger.Debug($"Found {list.Count} entries for army {army.ID}, data type: {list_type}.");
            return list;
        }


        static void ChangeRegimentsSoldiers(Army army)
        {
            Program.Logger.Debug($"Entering ChangeRegimentsSoldiers for army {army.ID}.");
            // Validate every exact siege source before mutating any source counts.
            // Crew counts remain raw at every battle scale; native equipment is
            // weighted by explicit per-formation representation, not template size.
            var siegeSurvivors = GetSiegeSourceSurvivors(army);

            // Separate ArmyRegiments into levy and non-levy regiments
            var levyArmyRegiments = new List<ArmyRegiment>();
            var nonLevyArmyRegiments = new List<ArmyRegiment>();

            foreach (var armyRegiment in army.ArmyRegiments)
            {
                if (armyRegiment.Type == data.save_file.RegimentType.Commander ||
                    armyRegiment.Type == data.save_file.RegimentType.Knight) continue;

                if (armyRegiment.Type == RegimentType.Levy)
                {
                    levyArmyRegiments.Add(armyRegiment);
                }
                else
                {
                    nonLevyArmyRegiments.Add(armyRegiment);
                }
            }

            // Process non-levy regiments (Men-at-Arms and Garrison)
            foreach (ArmyRegiment armyRegiment in nonLevyArmyRegiments)
            {
                bool isSiegeType = false;
                string unitIdentifier = armyRegiment.MAA_Name;

                var firstRegiment = armyRegiment.Regiments.FirstOrDefault();
                string? cultureId = firstRegiment?.Culture?.ID;

                Unit? correspondingUnit = army.Units.FirstOrDefault(u =>
                    u.GetRegimentType() == armyRegiment.Type &&
                    u.GetName() == unitIdentifier &&
                    u.GetObjCulture()?.ID == cultureId
                );

                if (correspondingUnit != null && correspondingUnit.IsSiege())
                {
                    isSiegeType = true;
                }

                var unitReport = army.CasualitiesReports.FirstOrDefault(x =>
                    x.GetUnitType() == armyRegiment.Type && x.GetCulture() != null &&
                    x.GetCulture().ID == cultureId && x.GetTypeName() == unitIdentifier);

                foreach (Regiment regiment in armyRegiment.Regiments)
                {
                    string siegeKey = StatFidelity.SiegeSourceBudget.Key(army.ID, armyRegiment.ID, regiment.ID, regiment.Index);
                    if (siegeSurvivors.TryGetValue(siegeKey, out int survivingMachines))
                    {
                        regiment.SetSoldiers(survivingMachines.ToString(CultureInfo.InvariantCulture));
                        continue;
                    }
                    if (regiment.Culture is null || string.IsNullOrEmpty(regiment.CurrentNum)) continue;

                    // Direct-stat variants of identical culture/type can have different losses.
                    // Sum every split unit belonging to this exact save regiment chunk.
                    var sourceUnits = army.Units.Where(u => u.SourceArmyId == army.ID && u.SourceArmyRegimentId == armyRegiment.ID &&
                        u.SourceRegimentId == regiment.ID && u.SourceChunkIndex == regiment.Index && !u.IsSiege()).ToList();
                    if (sourceUnits.Count > 0)
                    {
                        var exactReports = sourceUnits.Select(u => army.CasualitiesReports.Where(r =>
                            r.Script.Contains("_" + u.UniqueID + "_CULTURE")).ToList()).ToList();
                        if (exactReports.Any(r => r.Count != 1))
                            throw new InvalidOperationException("Missing or ambiguous stat-fidelity casualty report for source regiment " + army.ID + ":" + regiment.ID + ":" + regiment.Index + ". Save update aborted.");
                        long starting = exactReports.Sum(r => (long)r[0].GetStarting());
                        long remaining = exactReports.Sum(r => (long)(r[0].GetAliveAfterPursuit() >= 0 ? r[0].GetAliveAfterPursuit() : r[0].GetAliveBeforePursuit()));
                        int survivors = StatFidelity.StatScaling.SourceSurvivors(Int32.Parse(regiment.CurrentNum), starting, remaining);
                        regiment.SetSoldiers(survivors.ToString(CultureInfo.InvariantCulture));
                        continue;
                    }

                    if (isSiegeType)
                    {
                        int finalMachineCount = 0;
                        int originalMachines = Int32.Parse(regiment.Max);

                        if (unitReport != null && unitReport.GetStarting() > 0)
                        {
                            int finalMenCount = unitReport.GetAliveAfterPursuit() != -1 ? unitReport.GetAliveAfterPursuit() : unitReport.GetAliveBeforePursuit();
                            double survivalRate = (double)finalMenCount / unitReport.GetStarting();
                            if (double.IsNaN(survivalRate) || double.IsInfinity(survivalRate)) survivalRate = 0;
                            finalMachineCount = (int)Math.Round(originalMachines * survivalRate);
                        }

                        regiment.SetSoldiers(Math.Min(originalMachines, finalMachineCount).ToString());
                    }
                    else
                    {
                        int originalSoldiers = Int32.Parse(regiment.CurrentNum);
                        int finalSoldierCount = 0;

                        if (unitReport != null && unitReport.GetStarting() > 0)
                        {
                            int finalMenCount = unitReport.GetAliveAfterPursuit() != -1 ? unitReport.GetAliveAfterPursuit() : unitReport.GetAliveBeforePursuit();
                            double survivalRate = (double)finalMenCount / unitReport.GetStarting();
                            if (double.IsNaN(survivalRate) || double.IsInfinity(survivalRate)) survivalRate = 0;
                            finalSoldierCount = (int)Math.Round(originalSoldiers * survivalRate);
                        }

                        regiment.SetSoldiers(Math.Min(originalSoldiers, finalSoldierCount).ToString());
                    }
                }
            }

            // Process levy regiments correctly by culture group
            var levyRegimentsByCulture = levyArmyRegiments
                .SelectMany(ar => ar.Regiments.Where(r => r.Culture?.ID != null))
                .GroupBy(r => r.Culture.ID);

            foreach (var cultureGroup in levyRegimentsByCulture)
            {
                string cultureId = cultureGroup.Key;

                var levyReports = army.CasualitiesReports.Where(x =>
                    x.GetUnitType() == RegimentType.Levy &&
                    x.GetCulture() != null &&
                    x.GetCulture().ID == cultureId);

                int totalCasualtiesToApply = levyReports.Sum(r => r.GetCasualties());

                var regimentsInGroup = cultureGroup.ToList();
                int totalOriginalSoldiers = regimentsInGroup.Sum(r => int.TryParse(r.CurrentNum, out int num) ? num : 0);

                if (totalOriginalSoldiers == 0) continue;

                Program.Logger.Debug($"Processing levy casualties for culture {cultureId}: {totalCasualtiesToApply} total casualties to distribute among {totalOriginalSoldiers} soldiers.");

                if (StatFidelity.StatFidelitySession.HasBoundExport)
                {
                    int[] originals = regimentsInGroup.Select(r => int.Parse(r.CurrentNum, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
                    long survivors = originals.Sum(n => (long)n) - levyReports.Sum(r => (long)r.GetCasualties());
                    int[] allocated = StatFidelity.SurvivorApportionment.Allocate(originals, survivors);
                    for (int index = 0; index < regimentsInGroup.Count; index++)
                        regimentsInGroup[index].SetSoldiers(allocated[index].ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                int casualtiesAppliedSoFar = 0;
                for (int i = 0; i < regimentsInGroup.Count; i++)
                {
                    var regiment = regimentsInGroup[i];
                    if (string.IsNullOrEmpty(regiment.CurrentNum)) continue;

                    int originalSoldiers = int.Parse(regiment.CurrentNum);
                    int finalSoldiers;
                    int casualtiesForThisRegiment;

                    if (i == regimentsInGroup.Count - 1)
                    {
                        // For the last regiment, assign all remaining casualties to prevent rounding errors.
                        casualtiesForThisRegiment = totalCasualtiesToApply - casualtiesAppliedSoFar;
                    }
                    else
                    {
                        double proportion = (double)originalSoldiers / totalOriginalSoldiers;
                        casualtiesForThisRegiment = (int)Math.Round(totalCasualtiesToApply * proportion);
                    }

                    finalSoldiers = Math.Max(0, originalSoldiers - casualtiesForThisRegiment);
                    casualtiesAppliedSoFar += (originalSoldiers - finalSoldiers);

                    regiment.SetSoldiers(finalSoldiers.ToString());
                    Program.Logger.Debug(
                        $"Levy Regiment {regiment.ID} (Culture: {cultureId}): Soldiers changed from {originalSoldiers} to {finalSoldiers}. Casualties applied: {originalSoldiers - finalSoldiers}.");
                }
            }

            // Update ArmyRegiment totals at the end
            foreach (ArmyRegiment armyRegiment in army.ArmyRegiments)
            {
                if (armyRegiment.Type == data.save_file.RegimentType.Commander ||
                    armyRegiment.Type == data.save_file.RegimentType.Knight) continue;

                int army_regiment_total = armyRegiment.Regiments.Where(reg => !string.IsNullOrEmpty(reg.CurrentNum))
                    .Sum(x => Int32.Parse(x.CurrentNum!));
                armyRegiment.CurrentNum = army_regiment_total;
                Program.Logger.Debug(
                    $"Updated ArmyRegiment {armyRegiment.ID} total soldiers to: {army_regiment_total}");
            }
        }

        private static Dictionary<string, int> GetSiegeSourceSurvivors(Army army)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!StatFidelity.StatFidelitySession.HasBoundExport) return result;
            foreach (var unit in army.Units.Where(u => u.IsSiege()))
            {
                string key = StatFidelity.SiegeSourceBudget.Key(unit.SourceArmyId ?? "", unit.SourceArmyRegimentId ?? "", unit.SourceRegimentId ?? "", unit.SourceChunkIndex);
                if (unit.SiegeRepresentation == null || !StatFidelity.StatFidelitySession.SiegeSources.ContainsKey(key))
                    throw new InvalidOperationException("Missing exact siege deployment/source metadata; regenerate this battle before importing results: " + key);
            }
            foreach (var budget in StatFidelity.StatFidelitySession.SiegeSources.Values.Where(b => b.ArmyId == army.ID))
            {
                var source = army.ArmyRegiments.Where(ar => ar.ID == budget.ArmyRegimentId).SelectMany(ar => ar.Regiments)
                    .Single(r => r.ID == budget.RegimentId && (string.IsNullOrEmpty(r.Index) ? "0" : r.Index) == budget.ChunkIndex);
                if (int.Parse(source.CurrentNum, CultureInfo.InvariantCulture) != budget.OriginalMachines)
                    throw new InvalidOperationException("Native siege source population changed before import: " + budget.Identity);
                var formations = new List<StatFidelity.SiegeCrewResult>();
                foreach (var unit in army.Units.Where(u => u.SiegeRepresentation != null &&
                    StatFidelity.SiegeSourceBudget.Key(u.SourceArmyId ?? "", u.SourceArmyRegimentId ?? "", u.SourceRegimentId ?? "", u.SourceChunkIndex) == budget.Identity))
                {
                    var metadata = unit.SiegeRepresentation!;
                    var reports = army.CasualitiesReports.Where(r => r.Script.Contains("_" + unit.UniqueID + "_CULTURE", StringComparison.Ordinal)).ToArray();
                    if (reports.Length != 1 || reports[0].GetStarting() != metadata.DeployedBodies)
                        throw new InvalidOperationException("Missing or inconsistent raw crew report for exact siege source: " + budget.Identity);
                    int remaining = reports[0].GetAliveAfterPursuit() >= 0 ? reports[0].GetAliveAfterPursuit() : reports[0].GetAliveBeforePursuit();
                    formations.Add(new(metadata.SourceMachines, metadata.DeployedBodies, remaining));
                }
                result.Add(budget.Identity, StatFidelity.SiegeCasualtyAccounting.Survivors(budget.OriginalMachines, formations));
            }
            return result;
        }

        static void CreateUnitsReports(Army army)
        {
            var identifiedUnits = army.Units.Where(u => u.SourceIdentity != null ||
                (u.GetOriginalSoldiers() > 0 && StatFidelity.StatFidelitySession.IsCalibratedKnightUnit("knights_" + u.UniqueID))).ToArray();
            if (army.UnitsResults == null)
            {
                if (identifiedUnits.Length > 0)
                    throw new InvalidOperationException("Missing stat-fidelity unit results for army " + army.ID + ". Save update aborted.");
                Program.Logger.Debug($"Warning: army.UnitsResults is null for army {army.ID}. Skipping unit reports creation.");
                return;
            }

            // Validate the whole expected roster before mutating any casualty records.
            // Otherwise an entirely absent calibrated knight report could bypass the
            // per-record checks and silently leave all its characters alive.
            foreach (var unit in identifiedUnits)
            {
                int matches = army.UnitsResults.Alive_MainPhase.Count(r =>
                    r.Type.EndsWith("_" + unit.UniqueID, StringComparison.Ordinal) &&
                    r.Script.Contains("_" + unit.UniqueID + "_CULTURE", StringComparison.Ordinal));
                if (matches != 1)
                    throw new InvalidOperationException("Missing or ambiguous stat-fidelity survivor report for unit " + unit.UniqueID + " in army " + army.ID + ". Save update aborted.");
            }

            Program.Logger.Debug($"Entering CreateUnitsReports for army {army.ID}. Processing each Attila unit record individually.");
            List<UnitCasualitiesReport> reportsList = new List<UnitCasualitiesReport>();

            // Process every individual unit record from the Main Phase
            foreach (var mainUnit in army.UnitsResults.Alive_MainPhase)
            {
                string script = mainUnit.Script;
                string typeIdentifier = mainUnit.Type;
                string cultureId = mainUnit.CultureID;

                if (!int.TryParse(mainUnit.Remaining, out int remaining))
                {
                    Program.Logger.Debug($"Warning: Could not parse remaining soldiers '{mainUnit.Remaining}' for script '{script}'. Defaulting to 0.");
                    remaining = 0;
                }

                Program.Logger.Debug($"Processing individual unit report: Script='{script}', Type='{typeIdentifier}', CultureID='{cultureId}', Remaining={remaining}.");

                RegimentType unitType;
                string reportTypeName;
                int? uniqueId = null;
                string? commanderIdToMatch = null;

                // Parse the type identifier to find the corresponding CK3 unit
                // New format: {Something}_{UniqueID}
                Match uniqueIdMatch = Regex.Match(typeIdentifier, @"_(\d+)$");
                if (uniqueIdMatch.Success)
                {
                    uniqueId = int.Parse(uniqueIdMatch.Groups[1].Value);
                }

                if (typeIdentifier.StartsWith("Levy"))
                {
                    unitType = RegimentType.Levy;
                }
                else if (typeIdentifier.StartsWith("Garrison"))
                {
                    unitType = RegimentType.Garrison;
                }
                else if (typeIdentifier.Contains("commander"))
                {
                    unitType = RegimentType.Commander;
                    Match idMatch = Regex.Match(typeIdentifier, @"commander(\d+)");
                    if (idMatch.Success) commanderIdToMatch = idMatch.Groups[1].Value;
                }
                else if (typeIdentifier.StartsWith("knights"))
                {
                    unitType = RegimentType.Knight;
                }
                else
                {
                    unitType = RegimentType.MenAtArms;
                }

                // Find the matching Unit object in the army
                Unit? matchingUnit = null;
                if (uniqueId.HasValue)
                {
                    matchingUnit = army.Units.FirstOrDefault(x => x.UniqueID == uniqueId.Value);
                }
                else if (commanderIdToMatch != null)
                {
                    matchingUnit = army.Units.FirstOrDefault(x => x != null && x.GetCharacterID() == commanderIdToMatch);
                }
                else if (unitType == RegimentType.Knight)
                {
                    matchingUnit = army.Units.FirstOrDefault(u => u.GetRegimentType() == RegimentType.Knight && u.GetObjCulture()?.ID == cultureId);
                }
                else
                {
                    matchingUnit = army.Units.FirstOrDefault(x => x != null && x.GetRegimentType() == unitType && x.GetObjCulture()?.ID == cultureId && x.GetName() == typeIdentifier);
                }

                if (matchingUnit == null)
                {
                    Program.Logger.Debug($"Warning: Could not find matching Unit object for script '{script}'. Skipping.");
                    continue;
                }

                bool requiresExactReport = matchingUnit.SourceIdentity != null || StatFidelity.StatFidelitySession.IsCalibratedKnightUnit(typeIdentifier);
                if (requiresExactReport && (!int.TryParse(mainUnit.Remaining, out int verifiedRemaining) || verifiedRemaining < 0))
                    throw new InvalidOperationException("Invalid survivor count for identified stat-fidelity unit " + script + ". Save update aborted.");

                // Determine the display name for the report
                // Use the screen name from the unit mapper if available to ensure 1:1 matching in the AAR
                string attilaKey = matchingUnit.GetAttilaUnitKey();
                var mapperName = UnitMappers_BETA.GetLoadedUnitMapperName();
                var unitScreenNames = mapperName != null
                    ? UnitsCardsNames.GetUnitScreenNames(mapperName)
                    : null;

                if (unitType == RegimentType.Levy || unitType == RegimentType.Garrison || unitType == RegimentType.Knight)
                {
                    string? screenName = null;
                    if (unitScreenNames != null && unitScreenNames.TryGetValue(attilaKey, out screenName))
                    {
                        reportTypeName = $"Levy {screenName}";
                    }
                    else
                    {
                        reportTypeName = $"Levy {attilaKey}";
                    }
                }
                else if (unitType == RegimentType.Commander)
                {
                    reportTypeName = "General";
                }
                else
                {
                    reportTypeName = matchingUnit.GetName();
                }

                Culture? culture = matchingUnit.GetObjCulture();
                string attilaFaction = matchingUnit.GetAttilaFaction();
                if (culture == null) continue;

                // Calculate starting numbers
                int starting;
                int startingMachines = 0;
                int effectiveNumGuns = matchingUnit.GetNumGuns();
                if (matchingUnit.IsSiegeEnginePerUnit() && effectiveNumGuns <= 0) effectiveNumGuns = 1;

                if (matchingUnit.IsSiege() && StatFidelity.StatFidelitySession.HasBoundExport)
                {
                    var metadata = matchingUnit.SiegeRepresentation ?? throw new InvalidOperationException(
                        "Siege formation lacks exact deployed crew/equipment metadata; regenerate the battle before result import: " + script);
                    if (metadata.DeployedBodies <= 0 || metadata.SourceMachines < 0)
                        throw new InvalidOperationException("Invalid siege representation metadata: " + script);
                    starting = metadata.DeployedBodies;
                    startingMachines = metadata.SourceMachines;
                }
                else if (matchingUnit.IsSiege() && effectiveNumGuns > 0)
                {
                    startingMachines = effectiveNumGuns;
                    starting = unit_mapper.UnitMappers_BETA.ConvertMachinesToMen(startingMachines);
                }
                else if (matchingUnit.IsSiege())
                {
                    startingMachines = matchingUnit.GetOriginalSoldiers();
                    starting = unit_mapper.UnitMappers_BETA.ConvertMachinesToMen(startingMachines);
                }
                else
                {
                    // For non-siege units, we use the soldiers count from the Attila unit record.
                    // We assume the 'starting' is the same as 'remaining' if no casualties, 
                    // but since we don't have the individual starting count per Attila unit record in the log,
                    // we use the Unit object's soldiers count as a base, distributed if necessary.
                    if (unitType == RegimentType.Knight || unitType == RegimentType.Commander)
                        starting = matchingUnit.GetOriginalSoldiers();
                    else if (matchingUnit.SourceIdentity != null)
                    {
                        // Split units were constructed after battlefield scaling. Results have
                        // already been restored to 100%, so restore their starting denominator too.
                        double scale = ModOptions.GetBattleScale() / 100.0;
                        if (scale <= 0) throw new InvalidOperationException("Invalid battle scale for source-regiment casualties.");
                        starting = (int)Math.Round(matchingUnit.GetOriginalSoldiers() / scale);
                    }
                    else starting = (int)Math.Round(matchingUnit.GetOriginalSoldiers() * (ArmyProportions.BattleScale / 100.0));
                }

                // Find corresponding kills for this specific script
                int kills = 0;
                var killRecord = army.UnitsResults.Kills_MainPhase.FirstOrDefault(k => k.Script == "kills_" + script);
                if (killRecord != default) Int32.TryParse(killRecord.Kills, out kills);

                // Find corresponding pursuit remaining for this specific script
                int? pursuitRemaining = null;
                var pursuitRecord = army.UnitsResults.Alive_PursuitPhase?.FirstOrDefault(p => p.Script == script);
                if (requiresExactReport && army.UnitsResults.Alive_PursuitPhase?.Count > 0 &&
                    (pursuitRecord == null || pursuitRecord.Value.Script != script || !int.TryParse(pursuitRecord.Value.Remaining, out int verifiedPursuit) || verifiedPursuit < 0))
                    throw new InvalidOperationException("Missing or invalid pursuit report for identified stat-fidelity unit " + script + ". Save update aborted.");
                if (pursuitRecord != null)
                {
                    if (int.TryParse(pursuitRecord.Value.Remaining, out int pRemaining))
                    {
                        pursuitRemaining = pRemaining;
                    }
                    else
                    {
                        Program.Logger.Debug($"Warning: Could not parse pursuit remaining soldiers '{pursuitRecord.Value.Remaining}' for script '{script}'.");
                    }
                }

                UnitCasualitiesReport unitReport;
                int tolerance = matchingUnit.SiegeRepresentation != null ? 0 : 1;
                if (requiresExactReport && (remaining > starting + tolerance || pursuitRemaining > remaining + tolerance))
                    throw new InvalidOperationException("Impossible survivor totals for identified stat-fidelity unit " + script + ". Save update aborted.");
                if (pursuitRemaining.HasValue)
                {
                    unitReport = new UnitCasualitiesReport(unitType, reportTypeName, culture, starting, remaining, pursuitRemaining.Value, startingMachines, attilaFaction, script);
                }
                else
                {
                    unitReport = new UnitCasualitiesReport(unitType, reportTypeName, culture, starting, remaining, startingMachines, attilaFaction, script);
                }

                unitReport.SetKills(kills);
                unitReport.PrintReport();

                // Ensure we add every individual unit record to the report list
                reportsList.Add(unitReport);
            }

            // Replace the army's casualty reports with our 1:1 list
            army.SetCasualitiesReport(reportsList);
            Program.Logger.Debug($"Created {reportsList.Count} individual unit casualty reports for army {army.ID}.");
        }

        public static void CheckForSlainCommanders(Army army, string path_attila_log)
        {
            Program.Logger.Debug($"Checking for slain commander in army {army.ID}");
            if (army.Commander != null)
            {
                army.Commander.HasGeneralFallen(path_attila_log);
                if (army.Commander.hasFallen)
                {
                    Program.Logger.Debug($"Commander {army.Commander.ID} in army {army.ID} has fallen.");
                }
                else
                {
                    Program.Logger.Debug($"Commander {army.Commander.ID} in army {army.ID} has NOT fallen.");
                }
            }
        }

        public static void CheckForSlainKnights(Army army)
        {
            Program.Logger.Debug($"Checking for slain knights in army {army.ID}");
            if (army.Knights == null || !army.Knights.HasKnights()) return;

            // --- Part 1: Handle prominent knights leading MAA units ---
            if (army.Units != null && army.CasualitiesReports != null)
            {
                foreach (var unit in army.Units.Where(u => u.KnightCommander != null))
                {
                    var report = army.CasualitiesReports.FirstOrDefault(r =>
                        r.GetUnitType() == unit.GetRegimentType() &&
                        r.GetTypeName() == unit.GetName() &&
                        r.GetCulture()?.ID == unit.GetObjCulture()?.ID);

                    if (report != null && report.GetStarting() > 0)
                    {
                        int finalSoldiers = report.GetAliveAfterPursuit() != -1 ? report.GetAliveAfterPursuit() : report.GetAliveBeforePursuit();
                        int casualties = report.GetStarting() - finalSoldiers;
                        double casualtyPercentage = (double)casualties / report.GetStarting();

                        // New probabilistic logic
                        unit.KnightCommander.CalculateMAACommanderFate(casualtyPercentage);
                    }
                }
            }

            // --- Part 2: Handle the combined knights unit AND prominent bodyguard units ---
            if (army.UnitsResults != null)
            {
                List<(string Script, string Type, string CultureID, string Remaining)> allKnightUnitReports = new();

                // First, try to get reports from the pursuit phase
                if (army.UnitsResults.Alive_PursuitPhase != null && army.UnitsResults.Alive_PursuitPhase.Any())
                {
                    allKnightUnitReports = army.UnitsResults.Alive_PursuitPhase.Where(x => x.Type.StartsWith("knight")).ToList();
                    Program.Logger.Debug($"Found {allKnightUnitReports.Count} knight reports in pursuit phase for army {army.ID}.");
                }

                // If no reports in pursuit phase, try the main phase
                if (!allKnightUnitReports.Any() && army.UnitsResults.Alive_MainPhase != null && army.UnitsResults.Alive_MainPhase.Any())
                {
                    allKnightUnitReports = army.UnitsResults.Alive_MainPhase.Where(x => x.Type.StartsWith("knight")).ToList();
                    Program.Logger.Debug($"Found {allKnightUnitReports.Count} knight reports in main phase for army {army.ID}.");
                }

                // Handle combined unit first
                var poolReports = allKnightUnitReports.Where(r => StatFidelity.StatFidelitySession.IsKnightPoolType(r.Type)).ToArray();
                if (poolReports.Any())
                {
                    int remaining = 0;
                    foreach (var report in poolReports)
                    {
                        if (!Int32.TryParse(report.Remaining, out int value) || value < 0)
                            throw new InvalidOperationException("Invalid knight survivor report.");
                        remaining = checked(remaining + value);
                    }
                    // One character pool can be represented by several formations.
                    // Sum once before one fate pass; these counts already describe the
                    // actual retinue bodies and must not be unscaled a second time.
                    army.Knights.GetKilled(remaining, alreadyUnscaled: true);
                }
            }


            // --- Final Log ---
            foreach (var knight in army.Knights.GetKnightsList().Where(k => k.HasFallen()))
            {
                Program.Logger.Debug($"Knight {knight.GetID()} ({knight.GetName()}) in army {army.ID} has fallen.");
            }
        }

        public static void CheckKnightsKills(Army army)
        {
            Program.Logger.Debug($"Checking knight kills for army {army.ID}");
            if (army.Knights != null && army.Knights.HasKnights())
            {
                int kills = 0;
                foreach (var report in army.UnitsResults?.Kills_MainPhase.Where(x => StatFidelity.StatFidelitySession.IsKnightPoolType(x.Type)) ?? Enumerable.Empty<(string Script, string Type, string CultureID, string Kills)>())
                {
                    if (Int32.TryParse(report.Kills, out int value) && value >= 0) kills = checked(kills + value);
                }

                army.Knights.GetKills(kills);
                Program.Logger.Debug($"Total knight kills for army {army.ID}: {kills}");
            }

        }

        public static void LogPostBattleReport(List<Army> armies, Dictionary<string, int> originalSizes, string side)
        {
            Program.Logger.Debug($"******************** POST-BATTLE CASUALTY REPORT: {side}S ********************");

            foreach (var army in armies)
            {
                Program.Logger.Debug($"--- Army ID: {army.ID} ({army.CombatSide.ToUpper()}) ---");

                // Regular Regiments (Levy, MenAtArms, Garrison)
                Program.Logger.Debug("  REGIMENTS:");
                if (army.ArmyRegiments != null)
                {
                    foreach (var armyRegiment in army.ArmyRegiments)
                    {
                        if (armyRegiment == null || armyRegiment.Type == RegimentType.Commander || armyRegiment.Type == RegimentType.Knight) continue;

                        string regimentTypeName = armyRegiment.Type == RegimentType.Levy ? "Levy" : armyRegiment.MAA_Name;
                        if (armyRegiment.Type == RegimentType.Garrison) regimentTypeName = "Garrison"; // Explicitly set for garrison

                        // Calculate aggregate casualties for this regiment group
                        int totalOriginalSize = 0;
                        int totalFinalSize = 0;
                        if (armyRegiment.Regiments != null && armyRegiment.Regiments.Any())
                        {
                            foreach (var regiment in armyRegiment.Regiments)
                            {
                                if (regiment == null || string.IsNullOrEmpty(regiment.CurrentNum)) continue;

                                string key = $"{army.ID}_{regiment.ID}";
                                totalOriginalSize += originalSizes.ContainsKey(key) ? originalSizes[key] : 0;
                                totalFinalSize += Int32.Parse(regiment.CurrentNum);
                            }
                        }
                        else if (armyRegiment.Type == RegimentType.Levy)
                        {
                            // For Levies that don't have sub-regiments in the list
                            totalFinalSize = armyRegiment.CurrentNum;
                            // Note: totalOriginalSize should have been captured during initialization
                        }
                        int totalCasualties = totalOriginalSize - totalFinalSize;

                        // Log the high-level summary for the regiment group
                        Program.Logger.Debug($"    Type: {regimentTypeName}, Original: {totalOriginalSize}, Casualties: {totalCasualties}, Remaining: {totalFinalSize}");

                        if (armyRegiment.Regiments != null)
                        {
                            foreach (var regiment in armyRegiment.Regiments)
                            {
                                if (regiment == null || string.IsNullOrEmpty(regiment.CurrentNum)) continue;

                                string key = $"{army.ID}_{regiment.ID}";
                                int originalSize = originalSizes.ContainsKey(key) ? originalSizes[key] : 0;
                                int finalSize = Int32.Parse(regiment.CurrentNum);
                                int casualties = originalSize - finalSize;

                                Program.Logger.Debug($"      - ID: {regiment.ID}, Culture: {regiment.Culture?.ID ?? "N/A"}, Original: {originalSize}, Casualties: {casualties}, Remaining: {finalSize}");
                            }
                        }
                    }
                }

                // Knights
                if (army.Knights != null && army.Knights.HasKnights())
                {
                    Program.Logger.Debug("  KNIGHTS:");
                    int originalKnightCount = army.Knights.GetKnightsList().Count;
                    int fallenKnightCount = army.Knights.GetKnightsList().Count(k => k.HasFallen());
                    int remainingKnightCount = originalKnightCount - fallenKnightCount;
                    Program.Logger.Debug($"    Total Knights: {originalKnightCount}, Fallen: {fallenKnightCount}, Remaining: {remainingKnightCount}");

                    foreach (var knight in army.Knights.GetKnightsList())
                    {
                        if (knight.HasFallen())
                        {
                            Program.Logger.Debug($"      - Fallen Knight: {knight.GetName()} (ID: {knight.GetID()})");
                        }
                    }
                }

                // Commander
                if (army.Commander != null)
                {
                    Program.Logger.Debug("  COMMANDER:");
                    if (army.Commander.hasFallen)
                    {
                        Program.Logger.Debug($"    Commander {army.Commander.Name} (ID: {army.Commander.ID}) has fallen.");
                    }
                    else
                    {
                        Program.Logger.Debug($"    Commander {army.Commander.Name} (ID: {army.Commander.ID}) survived.");
                    }
                }
            }
            Program.Logger.Debug("************************************************************************************");
        }
    }
}
