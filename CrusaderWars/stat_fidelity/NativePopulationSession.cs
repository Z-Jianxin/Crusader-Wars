using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CrusaderWars.data.save_file;
using CrusaderWars.data.battle_results;

namespace CrusaderWars.StatFidelity;

public sealed record NativeEquipmentPopulationInput(string Side, string ArmyId, string ArmyRegimentId,
    string NativeType, int Stock, decimal Active, decimal Soft, IReadOnlyList<NativePopulationSourceStock> Sources);

public static partial class StatFidelitySession
{
    private static readonly Dictionary<string, NativePopulationBudget> nativePopulationBudgets = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, NativePopulationOutcome> nativePopulationOutcomes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, NativeEquipmentPopulationInput> nativeEquipmentInputs = new(StringComparer.Ordinal);
    private static bool syntheticUnharmedPopulationControl;
    private static readonly Dictionary<string, Army> nativePopulationReadArmies = new(StringComparer.Ordinal);
    public static IReadOnlyList<NativePopulationBudget> NativePopulationBudgets => nativePopulationBudgets.Values.ToArray();
    public static IReadOnlyList<NativePopulationOutcome> NativePopulationOutcomes => nativePopulationOutcomes.Values.ToArray();
    public static IReadOnlyList<NativeEquipmentPopulationInput> NativeEquipmentInputs => nativeEquipmentInputs.Values.ToArray();
    public static bool HasNativePopulationBudgets => HasBoundExport && nativePopulationBudgets.Count > 0;
    public static bool TryGetNativePopulationBudget(string side, string armyRegimentId, out NativePopulationBudget budget) =>
        nativePopulationBudgets.TryGetValue(side + ":" + armyRegimentId, out budget!);
    public static bool TryGetNativePopulationOutcome(string side, string armyRegimentId, out NativePopulationOutcome outcome) =>
        nativePopulationOutcomes.TryGetValue(side + ":" + armyRegimentId, out outcome!);
    public static bool TryGetNativeEquipmentInput(string side, string armyRegimentId, out NativeEquipmentPopulationInput input) =>
        nativeEquipmentInputs.TryGetValue(side + ":" + armyRegimentId, out input!);

    public static void RegisterNativePopulationBudget(NativePopulationBudget budget)
    {
        if (!HasBoundExport) throw Failure("Native population budgets require the selected bound handoff.");
        if (nativePopulationBudgets.TryGetValue(budget.Identity, out var prior))
        {
            if (prior.ArmyId != budget.ArmyId || prior.Kind != budget.Kind || prior.NativeType != budget.NativeType || prior.Stock != budget.Stock ||
                prior.InitialActive != budget.InitialActive || prior.PreservedSoft != budget.PreservedSoft || !prior.Sources.SequenceEqual(budget.Sources))
                throw Failure("Native population input changed within the same handoff: " + budget.Identity);
            return;
        }
        nativePopulationBudgets.Add(budget.Identity, budget);
    }

    public static void CaptureNativePopulations(IEnumerable<Army> armies)
    {
        if (!HasBoundExport) return;
        var capturedArmies = PopulationArmies(armies).ToArray();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var army in capturedArmies)
        {
            if (army.IsGarrisonArmy) throw Failure("Independent garrison population/stat participation is unsupported.");
            foreach (var regiment in army.ArmyRegiments.Where(r => r.Type is RegimentType.MenAtArms or RegimentType.Levy or RegimentType.Knight))
            {
                if (!observed.Add(army.CombatSide + ":" + regiment.ID)) throw Failure("Repeated native population identity in fresh reader.");
                var record = ArmiesReader.RequireNativeCombatParticipation(army, regiment.ID);
                if (!record.Current.HasValue || !record.SoftCasualties.HasValue)
                    throw Failure("Native active and soft-casualty fields must both be explicit for " + army.ID + ":" + regiment.ID + ". Soft was not inferred from stock.");
                var sources = regiment.Type == RegimentType.Knight ? Array.Empty<NativePopulationSourceStock>() : regiment.Regiments.Select(s =>
                    new NativePopulationSourceStock(s.ID, string.IsNullOrEmpty(s.Index) ? "0" : s.Index,
                        int.Parse(s.CurrentNum, NumberStyles.None, CultureInfo.InvariantCulture),
                        !string.IsNullOrEmpty(s.Culture?.ID) ? s.Culture.ID : army.Owner?.GetCulture()?.ID ?? "")).ToArray();
                if (regiment.Type == RegimentType.MenAtArms && ArmiesReader.TryGetNativeMaaDefinition(regiment.MAA_Name, out var definition) &&
                    (definition.IsSiegeEquipment || definition.IsFieldNoncombatant))
                {
                    if (sources.Sum(s => (long)s.Stock) != regiment.CurrentNum || record.Current < 0 || record.SoftCasualties < 0)
                        throw Failure("Native equipment stock/current evidence disagrees with its exact source stocks.");
                    string key = army.CombatSide + ":" + regiment.ID;
                    var input = new NativeEquipmentPopulationInput(army.CombatSide, army.ID, regiment.ID,
                        regiment.MAA_Name, regiment.CurrentNum, record.Current.Value, record.SoftCasualties.Value, Array.AsReadOnly(sources));
                    if (nativeEquipmentInputs.TryGetValue(key, out var old) && (old.ArmyId != input.ArmyId || old.NativeType != input.NativeType || old.Stock != input.Stock ||
                        old.Active != input.Active || old.Soft != input.Soft || !old.Sources.SequenceEqual(input.Sources)))
                        throw Failure("Native equipment input changed within the handoff: " + key);
                    nativeEquipmentInputs[key] = input;
                    continue;
                }
                RegisterNativePopulationBudget(NativePopulationLedger.Create(army.CombatSide, army.ID, regiment.ID, regiment.Type,
                    regiment.MAA_Name, regiment.CurrentNum, record.Current.Value, record.SoftCasualties.Value, sources));
            }
        }
        if (!observed.SetEquals(nativePopulationBudgets.Keys.Concat(nativeEquipmentInputs.Keys)))
            throw Failure("Fresh native reader did not preserve complete population/equipment coverage.");
        // A validated fresh read recreates stock-sized placeholders. Reset only its
        // deployment plans; same-object regeneration must still prove every prior body.
        foreach (var army in capturedArmies)
        {
            if (nativePopulationReadArmies.TryGetValue(army.ID, out var previous) && !ReferenceEquals(previous, army))
                foreach (var key in nativePopulationDeployments.Where(p => p.Value.ArmyId == army.ID).Select(p => p.Key).ToArray())
                    nativePopulationDeployments.Remove(key);
            nativePopulationReadArmies[army.ID] = army;
        }
    }

    public static IReadOnlyList<NativePopulationOutcome> ProjectOrdinaryNativePopulations(Army army)
    {
        var result = new List<NativePopulationOutcome>();
        foreach (var budget in nativePopulationBudgets.Values.Where(b => b.ArmyId == army.ID && !b.IsKnight))
        {
            var rows = budget.Sources.Where(s => s.Active > 0).Select(s => PopulationSurvival(army, budget.Kind, s)).ToArray();
            var outcome = NativePopulationLedger.Project(budget, rows);
            nativePopulationOutcomes[budget.Identity] = outcome; result.Add(outcome);
        }
        return result.AsReadOnly();
    }

    public static void FinalizeNativePopulationOutcomes(IEnumerable<Army> armies, bool syntheticUnharmedControl = false)
    {
        if (!HasBoundExport) return;
        if (syntheticUnharmedControl)
        {
            if (!client.DevelopmentRuntime.Enabled) throw Failure("Synthetic character control requires an explicitly isolated development runtime.");
            syntheticUnharmedPopulationControl = true;
        }
        foreach (var army in PopulationArmies(armies))
        {
            ProjectOrdinaryNativePopulations(army);
            foreach (var budget in nativePopulationBudgets.Values.Where(b => b.ArmyId == army.ID && b.IsKnight))
            {
                if (budget.InitialActive == 0)
                {
                    bool independentCommander = budget.Stock == 1 && army.Commander?.IsTrackedCharacter == true && army.Commander.ID == budget.NativeType &&
                        army.Units.Any(u => u.GetRegimentType() == RegimentType.Commander && u.GetCharacterID() == budget.NativeType);
                    bool removedCommander = independentCommander && BattleResult.CharacterFates.Entries.TryGetValue(budget.NativeType, out var commanderFate) &&
                        (commanderFate.Fate.IsSlain || commanderFate.Fate.IsCaptured);
                    var evidence = removedCommander ? SumPopulationReports(army, budget.Identity,
                        army.Units.Where(u => u.GetRegimentType() == RegimentType.Commander && u.GetCharacterID() == budget.NativeType).ToArray(), null) : null;
                    nativePopulationOutcomes[budget.Identity] = NativePopulationLedger.ProjectKnight(budget, evidence, removedCommander, removedCommander); continue;
                }
                var units = army.Units.Where(u => budget.NativeType == army.CommanderID
                    ? u.GetRegimentType() == RegimentType.Commander && u.GetCharacterID() == budget.NativeType
                    : u.GetRegimentType() == RegimentType.Knight).ToArray();
                var raw = SumPopulationReports(army, budget.Identity, units, null);
                bool removed; bool assumedUnharmed = false;
                if (BattleResult.CharacterFates.Entries.TryGetValue(budget.NativeType, out var fate)) removed = fate.Fate.IsSlain || fate.Fate.IsCaptured;
                else if (syntheticUnharmedPopulationControl && client.DevelopmentRuntime.Enabled && raw.MainSurvivingBodies == raw.DeployedBodies && raw.FinalSurvivingBodies == raw.DeployedBodies) { removed = false; assumedUnharmed = true; }
                else throw Failure("Participating knight lacks its resolved character fate before population return: " + budget.NativeType);
                nativePopulationOutcomes[budget.Identity] = NativePopulationLedger.ProjectKnight(budget, raw, removed) with { SyntheticUnharmedControl = assumedUnharmed };
            }
        }
    }

    private static KnightStats ParticipatingKnightStats(Army army, KnightStats row)
    {
        if (!HasNativePopulationForArmy(army.ID)) return row;
        var budget = nativePopulationBudgets.Values.SingleOrDefault(b => b.ArmyId == army.ID && b.IsKnight && b.NativeType == row.CharacterId)
            ?? throw Failure("Knight lacks its native active/soft identity budget: " + row.CharacterId);
        if (budget.InitialActive <= 0) throw Failure("An inactive knight cannot receive a Total War combat budget: " + row.CharacterId);
        Note("Knight " + row.CharacterId + " preserves one character identity; its nominal damage/HP budget is weighted by native active fraction " + budget.InitialActive.ToString(CultureInfo.InvariantCulture) + ". Retinue loss does not infer fractional personal health or new wounds.");
        return new KnightStats {Side = row.Side, CharacterId = row.CharacterId, Prowess = row.Prowess, Effectiveness = row.Effectiveness,
            DamagePerProwess = row.DamagePerProwess, ToughnessPerProwess = row.ToughnessPerProwess, ActiveFraction = (double)budget.InitialActive};
    }

    private static NativePopulationSourceSurvival PopulationSurvival(Army army, RegimentType type, NativePopulationSourceBudget source)
    {
        var units = army.Units.Where(u => u.GetRegimentType() == type && (type == RegimentType.Levy
            ? u.GetObjCulture()?.ID == source.CultureId
            : MaaSourceBudget.Key(u.SourceArmyId ?? "", u.SourceArmyRegimentId ?? "", u.SourceRegimentId ?? "", u.SourceChunkIndex) == source.Identity)).ToArray();
        var guards = army.Units.Where(u => u.CommanderGuardRepresentation is { } g && g.DonorType == type &&
            (type == RegimentType.Levy ? g.DonorCultureId == source.CultureId :
                MaaSourceBudget.Key(g.ArmyId, g.SourceArmyRegimentId!, g.SourceRegimentId!, g.SourceChunkIndex) == source.Identity)).ToArray();
        return SumPopulationReports(army, source.Identity, units, guards);
    }

    private static NativePopulationSourceSurvival SumPopulationReports(Army army, string identity, IReadOnlyList<Unit> units, IReadOnlyList<Unit>? guards)
    {
        decimal bodies = 0, main = 0, final = 0;
        var components = new List<NativePopulationBodySurvival>();
        foreach (var unit in units.Concat(guards ?? Array.Empty<Unit>()))
        {
            var matches = army.CasualitiesReports.Where(r => r.Script.Contains("_" + unit.UniqueID + "_CULTURE", StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].GetStarting() != unit.GetSoldiers())
                throw Failure("Active population lacks exact raw formation evidence: " + identity + "/" + unit.UniqueID);
            var report = matches[0]; int after = report.GetAliveAfterPursuit() < 0 ? report.GetAliveBeforePursuit() : report.GetAliveAfterPursuit();
            decimal weight = guards?.Contains(unit) == true ? unit.CommanderGuardRepresentation!.BorrowedBodies : unit.GetSoldiers();
            if (report.GetStarting() <= 0 || after < 0 || after > report.GetAliveBeforePursuit() || report.GetAliveBeforePursuit() > report.GetStarting())
                throw Failure("Invalid raw active population survivor counts: " + identity);
            bodies += weight;
            components.Add(new(report.Script, report.GetStarting(), report.GetAliveBeforePursuit(), after, weight));
            main += report.GetAliveBeforePursuit() == report.GetStarting() ? weight : weight * report.GetAliveBeforePursuit() / report.GetStarting();
            final += after == report.GetStarting() ? weight : weight * after / report.GetStarting();
        }
        if (bodies <= 0) throw Failure("Positive active native population has no represented actors: " + identity);
        return new(identity, bodies, main, final, components.AsReadOnly());
    }

    private static IEnumerable<Army> PopulationArmies(IEnumerable<Army> armies)
    {
        var seen = new HashSet<Army>(); var pending = new Stack<Army>(armies);
        while (pending.Count > 0)
        {
            var army = pending.Pop(); if (!seen.Add(army)) continue;
            yield return army;
            foreach (var merged in army.MergedArmies) pending.Push(merged);
        }
    }
}
