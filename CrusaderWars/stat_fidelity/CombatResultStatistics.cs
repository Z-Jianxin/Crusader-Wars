using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CrusaderWars.data.save_file;
using CrusaderWars.data.battle_results.editors;

namespace CrusaderWars.StatFidelity;

public sealed record CombatResultStatisticsAggregation(
    IReadOnlyList<CombatResultStatisticsUpdate> Updates, IReadOnlyList<string> Diagnostics);

/// <summary>Native side/type statistics from exact deployed formations, without casualty/kill equalization.</summary>
public static class CombatResultStatistics
{
    private readonly record struct Key(string Side, string? Type, string? Knight);
    private sealed class Totals
    {
        public int Final, MainKills, PursuitKills;
        public long NormalPursuit, SiegePursuit;
    }

    public static CombatResultStatisticsAggregation Build(IEnumerable<Army> attackers, IEnumerable<Army> defenders,
        IReadOnlyList<DeployedFormation> roster, ValidatedBattleReports reports)
    {
        if (reports == null || reports.KillPhases.Count == 0 || reports.KillPhases.Count != reports.SurvivorPhases.Count)
            throw new InvalidDataException("Native combat statistics require complete raw survivor and kill phases.");
        if (roster == null || roster.Count == 0) throw new InvalidDataException("Native combat statistics lack the deployed roster.");
        var armies = new Dictionary<(string Side, string Id), Army>();
        var visited = new HashSet<Army>();
        void Visit(Army army, string side)
        {
            if (army == null || army.CombatSide != side) throw new InvalidDataException("Army side disagrees with combat-statistics input.");
            if (!visited.Add(army)) return;
            if (!armies.TryAdd((side, army.ID), army)) throw new InvalidDataException("Ambiguous native army identity " + army.ID);
            foreach (var merged in army.MergedArmies) Visit(merged, side);
        }
        foreach (var army in attackers ?? throw new InvalidDataException("Attacking armies are missing.")) Visit(army, "attacker");
        foreach (var army in defenders ?? throw new InvalidDataException("Defending armies are missing.")) Visit(army, "defender");

        var groups = new Dictionary<Key, Totals>();
        var native = new Dictionary<(string Side, string Id), ArmyRegiment>();
        var units = new Dictionary<int, Unit>();
        var casualtyReports = new Dictionary<string, UnitCasualitiesReport>(StringComparer.Ordinal);
        var diagnostics = new List<string> { "Kills are raw Attila credited kills, unscaled. Native losses are separate CK3 counts; they are not forced to equal credited kills." };
        Totals Group(Key key)
        {
            if (!groups.TryGetValue(key, out var value)) groups.Add(key, value = new Totals());
            return value;
        }
        foreach (var army in armies.Values)
        {
            foreach (var regiment in army.ArmyRegiments)
            {
                if (native.TryGetValue((army.CombatSide, regiment.ID), out var existing))
                {
                    if (!ReferenceEquals(existing, regiment)) throw new InvalidDataException("Conflicting native army regiment " + regiment.ID);
                    continue;
                }
                native.Add((army.CombatSide, regiment.ID), regiment);
                if (regiment.Type == RegimentType.Commander) continue;
                Key key = NativeKey(army.CombatSide, regiment);
                if (regiment.CurrentNum < 0 || key.Knight != null && regiment.CurrentNum > 1)
                    throw new InvalidDataException("Invalid current native count for army regiment " + regiment.ID);
                if (key.Knight != null && groups.ContainsKey(key))
                    throw new InvalidDataException("Knight is present in more than one native army regiment: " + key.Knight);
                Group(key).Final = checked(Group(key).Final + regiment.CurrentNum);
            }
            foreach (var unit in army.Units)
            {
                if (units.TryGetValue(unit.UniqueID, out var previous) && !ReferenceEquals(previous, unit))
                    throw new InvalidDataException("Ambiguous generated unit " + unit.UniqueID);
                units[unit.UniqueID] = unit;
            }
            foreach (var report in army.CasualitiesReports)
            {
                if (casualtyReports.TryGetValue(report.Script, out var previous) && !ReferenceEquals(previous, report) &&
                    (previous.GetStarting() != report.GetStarting() || previous.GetAliveBeforePursuit() != report.GetAliveBeforePursuit() ||
                     previous.GetAliveAfterPursuit() != report.GetAliveAfterPursuit() || previous.GetStartingMachines() != report.GetStartingMachines()))
                    throw new InvalidDataException("Conflicting normalized casualty report: " + report.Script);
                casualtyReports[report.Script] = report;
            }
        }

        int mainPhase = reports.SurvivorPhases.Count > 1 ? reports.SurvivorPhases.Count - 2 : 0;
        var poolCredits = new Dictionary<(string Side, string Army), (int Main, int Pursuit)>();
        var siegeResults = new Dictionary<(Key Group, string Source), List<(SiegeCrewResult Main, SiegeCrewResult Final)>>();
        var directKnights = new HashSet<Key>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var boundIds = new HashSet<int>();
        int excludedMain = 0, excludedPursuit = 0;
        var guardCohortIds = new HashSet<int>();
        foreach (var army in armies.Values)
        foreach (var outcome in StatFidelitySession.GetCommanderGuardCasualties(army))
        {
            var binding = outcome.Representation;
            Key donorKey = GuardDonorKey(army.CombatSide, binding);
            if (!groups.TryGetValue(donorKey, out var totals)) throw new InvalidDataException("Guard donor has no native result-statistics group.");
            totals.NormalPursuit = checked(totals.NormalPursuit + outcome.PursuitLosses);
            foreach (int id in binding.CohortUnitIds) guardCohortIds.Add(id);
        }
        foreach (var formation in roster)
        {
            var identity = Regex.Match(formation.ScriptName, @"_(?<side>attacker|defender)_army(?<army>\d+)_TYPE.*_(?<unit>\d+)_CULTURE", RegexOptions.CultureInvariant);
            if (!identity.Success || !int.TryParse(identity.Groups["unit"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int unitId) ||
                !names.Add(formation.ScriptName) || !boundIds.Add(unitId) || !units.TryGetValue(unitId, out var unit))
                throw new InvalidDataException("Unbound or duplicate deployed statistics identity: " + formation.ScriptName);
            string side = identity.Groups["side"].Value, armyId = identity.Groups["army"].Value;
            if (!armies.TryGetValue((side, armyId), out var army)) throw new InvalidDataException("Unknown deployed army: " + armyId);
            if (!reports.KillPhases[mainPhase].TryGetValue(formation.ScriptName, out int mainKills) ||
                !reports.FinalKills!.TryGetValue(formation.ScriptName, out int finalKills) || finalKills < mainKills)
                throw new InvalidDataException("Missing or decreasing credited kills: " + formation.ScriptName);
            int pursuitKills = finalKills - mainKills;
            Key key;
            switch (unit.GetRegimentType())
            {
                case RegimentType.Levy:
                    key = new(side, null, null);
                    break;
                case RegimentType.MenAtArms:
                    if (unit.SourceArmyRegimentId == null || !native.TryGetValue((side, unit.SourceArmyRegimentId), out var source) || source.Type != RegimentType.MenAtArms)
                        throw new InvalidDataException("MAA formation has no exact native regiment binding: " + formation.ScriptName);
                    key = NativeKey(side, source);
                    break;
                case RegimentType.Knight:
                    string? knightId = unit.GetCharacterID();
                    if (!string.IsNullOrEmpty(knightId))
                    {
                        key = new(side, null, knightId); directKnights.Add(key);
                        AddCredits(key, mainKills, pursuitKills);
                    }
                    else
                    {
                        var poolKey = (side, armyId);
                        poolCredits.TryGetValue(poolKey, out var credits);
                        poolCredits[poolKey] = (checked(credits.Main + mainKills), checked(credits.Pursuit + pursuitKills));
                    }
                    continue;
                case RegimentType.Commander:
                    if (unit.CommanderGuardRepresentation is { } guard)
                    {
                        Key donorKey = GuardDonorKey(side, guard);
                        var mainShares = CommanderGuardAccounting.SplitCredits(mainKills, guard.PersonalDamageShare);
                        var pursuitShares = CommanderGuardAccounting.SplitCredits(pursuitKills, guard.PersonalDamageShare);
                        AddCredits(donorKey, mainShares.Donor, pursuitShares.Donor);
                        if (guard.NativeKnightId != null)
                        {
                            Key knightKey = new(side, null, guard.NativeKnightId);
                            if (!directKnights.Add(knightKey)) throw new InvalidDataException("Commander knight credit is duplicated.");
                            AddCredits(knightKey, mainShares.Personal, pursuitShares.Personal);
                        }
                        else if (mainShares.Personal != 0 || pursuitShares.Personal != 0)
                            throw new InvalidDataException("Commander guard personal kills lack a native knight.");
                        diagnostics.Add("Funded commander guard credits return to its ordinary donor; any native knight share is modeled from the separately evidenced aggregate damage budget, not measured personal kills.");
                        continue;
                    }
                    key = new(side, null, unit.GetCharacterID());
                    if (key.Knight != null && groups.ContainsKey(key))
                    {
                        directKnights.Add(key); AddCredits(key, mainKills, pursuitKills);
                        diagnostics.Add("Commander formation credit is attributed to its native knight record " + key.Knight + "; it is formation credit, not measured personal kills.");
                    }
                    else
                    {
                        excludedMain = checked(excludedMain + mainKills);
                        excludedPursuit = checked(excludedPursuit + pursuitKills);
                    }
                    continue;
                default:
                    throw new InvalidDataException("No native combat-statistics mapping for formation type " + unit.GetRegimentType());
            }
            AddCredits(key, mainKills, pursuitKills);
            if (guardCohortIds.Contains(unit.UniqueID)) continue;
            if (unit.IsSiege())
            {
                var representation = unit.SiegeRepresentation;
                if (representation == null || representation.DeployedBodies != formation.InitialSoldiers
                    || representation.SourceMachines < 0 || unit.SourceArmyId != armyId
                    || string.IsNullOrEmpty(unit.SourceArmyRegimentId) || string.IsNullOrEmpty(unit.SourceRegimentId)
                    || !reports.SurvivorPhases[mainPhase].TryGetValue(formation.ScriptName, out int mainCrew)
                    || !reports.FinalSurvivors.TryGetValue(formation.ScriptName, out int finalCrew)
                    || mainCrew < finalCrew)
                    throw new InvalidDataException("Siege pursuit lacks an exact source and deployed crew representation: " + formation.ScriptName);
                string sourceKey = SiegeSourceBudget.Key(unit.SourceArmyId, unit.SourceArmyRegimentId, unit.SourceRegimentId, unit.SourceChunkIndex);
                var identityKey = (key, sourceKey);
                if (!siegeResults.TryGetValue(identityKey, out var results)) siegeResults.Add(identityKey, results = new());
                results.Add((new(representation.SourceMachines, representation.DeployedBodies, mainCrew),
                    new(representation.SourceMachines, representation.DeployedBodies, finalCrew)));
                continue;
            }
            if (!casualtyReports.TryGetValue(formation.ScriptName, out var normalized))
                throw new InvalidDataException("Missing exact normalized casualty report: " + formation.ScriptName);
            int before = normalized.GetAliveBeforePursuit();
            int after = normalized.GetAliveAfterPursuit() < 0 ? before : normalized.GetAliveAfterPursuit();
            if (before < 0 || after < 0 || after > before || before > normalized.GetStarting())
                throw new InvalidDataException("Invalid normalized pursuit counts: " + formation.ScriptName);
            var totals = groups[key];
            totals.NormalPursuit = checked(totals.NormalPursuit + before - after);
        }
        if (!names.SetEquals(reports.FinalSurvivors.Keys) || !names.SetEquals(reports.FinalKills!.Keys))
            throw new InvalidDataException("Statistics report coverage differs from the exact deployed roster.");

        foreach (var source in siegeResults)
        {
            int original = checked((int)source.Value.Sum(result => (long)result.Main.SourceMachines));
            if (StatFidelitySession.HasBoundExport)
            {
                if (!StatFidelitySession.SiegeSources.TryGetValue(source.Key.Source, out var budget))
                    throw new InvalidDataException("Siege statistics lack the registered native source budget: " + source.Key.Source);
                original = budget.OriginalMachines;
            }
            // Omitted equipment is included in both phases and therefore cancels.
            // Round per exact source (including its split batteries), never per
            // battery or a pooled side/type crew ratio from different templates.
            int mainMachines = SiegeCasualtyAccounting.Survivors(original, source.Value.Select(result => result.Main).ToArray());
            int finalMachines = SiegeCasualtyAccounting.Survivors(original, source.Value.Select(result => result.Final).ToArray());
            groups[source.Key.Group].SiegePursuit = checked(groups[source.Key.Group].SiegePursuit + mainMachines - finalMachines);
        }
        if (siegeResults.Count > 0)
            diagnostics.Add("Siege pursuit losses are modeled per exact native source from raw deployed crew survival, rounded once per source and phase; they are not observed engine destruction counts.");

        foreach (var pool in poolCredits.OrderBy(p => p.Key.Side, StringComparer.Ordinal).ThenBy(p => p.Key.Army, StringComparer.Ordinal))
        {
            var army = armies[pool.Key];
            var knights = (army.Knights?.GetKnightsList() ?? new List<Knight>()).OrderBy(k => k.GetID(), StringComparer.Ordinal).ToArray();
            if (knights.Length == 0 || knights.Select(k => k.GetID()).Distinct(StringComparer.Ordinal).Count() != knights.Length)
                throw new InvalidDataException("Pooled knight credits lack an unambiguous native knight roster.");
            int[] main = AllocateCredits(knights, pool.Value.Main), pursuit = AllocateCredits(knights, pool.Value.Pursuit);
            for (int i = 0; i < knights.Length; i++)
            {
                var key = new Key(pool.Key.Side, null, knights[i].GetID());
                if (!directKnights.Add(key)) throw new InvalidDataException("Knight credit belongs to more than one formation pool: " + key.Knight);
                AddCredits(key, main[i], pursuit[i]);
            }
            diagnostics.Add($"Knight credits for {pool.Key.Side} army {pool.Key.Army} are deterministic modeled shares of pooled Attila credits ({pool.Value.Main} main/{pool.Value.Pursuit} pursuit), weighted by established retinue bodies with ordinal character-ID ties; not observed personal kills.");
        }
        diagnostics.Add($"Excluded abstract commander/guard credits with no native regiment_stats counterpart: {excludedMain} main/{excludedPursuit} pursuit Attila kills.");
        var updates = groups.OrderBy(g => g.Key.Side, StringComparer.Ordinal).ThenBy(g => g.Key.Type ?? "", StringComparer.Ordinal)
            .ThenBy(g => g.Key.Knight ?? "", StringComparer.Ordinal).Select(g => {
                return new CombatResultStatisticsUpdate(g.Key.Side, g.Key.Type, g.Key.Knight,
                    g.Value.MainKills, g.Value.PursuitKills, g.Value.Final, checked((int)(g.Value.NormalPursuit + g.Value.SiegePursuit)));
            }).ToArray();
        return new(Array.AsReadOnly(updates), diagnostics.AsReadOnly());

        void AddCredits(Key key, int main, int pursuit)
        {
            if (!groups.TryGetValue(key, out var value)) throw new InvalidDataException("Formation credit has no exact native side/type/knight record.");
            value.MainKills = checked(value.MainKills + main);
            value.PursuitKills = checked(value.PursuitKills + pursuit);
        }
        Key GuardDonorKey(string side, CommanderGuardRepresentation guard)
        {
            if (guard.DonorType == RegimentType.Levy) return new(side, null, null);
            if (guard.DonorType != RegimentType.MenAtArms || guard.SourceArmyRegimentId == null ||
                !native.TryGetValue((side, guard.SourceArmyRegimentId), out var regiment) || regiment.Type != RegimentType.MenAtArms)
                throw new InvalidDataException("Commander guard lacks its exact native donor source.");
            return NativeKey(side, regiment);
        }
    }

    private static Key NativeKey(string side, ArmyRegiment regiment) => regiment.Type switch {
        RegimentType.Levy => new(side, null, null),
        RegimentType.MenAtArms when !string.IsNullOrWhiteSpace(regiment.MAA_Name) => new(side, regiment.MAA_Name, null),
        RegimentType.Knight when !string.IsNullOrWhiteSpace(regiment.MAA_Name) => new(side, null, regiment.MAA_Name),
        _ => throw new InvalidDataException("Unsupported or missing native regiment-statistics identity.")
    };

    private static int[] AllocateCredits(Knight[] knights, int total)
    {
        var weights = knights.Select(k => k.GetSoldiers()).ToArray();
        if (weights.Any(w => w < 0) || total < 0) throw new InvalidDataException("Negative knight credit weight or total.");
        long sum = weights.Sum(w => (long)w);
        var result = new int[weights.Length];
        if (total == 0) return result;
        if (sum <= 0) throw new InvalidDataException("Positive pooled knight credits have no positive native weights.");
        var fractions = new List<(int Index, decimal Remainder)>();
        int assigned = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            decimal numerator = (decimal)weights[i] * total, remainder = numerator % sum;
            result[i] = checked((int)((numerator - remainder) / sum));
            assigned = checked(assigned + result[i]); fractions.Add((i, remainder));
        }
        foreach (var fraction in fractions.OrderByDescending(f => f.Remainder).ThenBy(f => f.Index).Take(total - assigned))
            result[fraction.Index]++;
        return result;
    }
}
