using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CrusaderWars.data.save_file;

namespace CrusaderWars.StatFidelity;

public sealed record NativeLevySource(string ArmyRegimentId, string RegimentId, string ChunkIndex, int Soldiers);
public sealed record LevyCohortBudget(string ArmyId, string Side, string CultureId, int NativeSoldiers,
    int DeployedBodies, IReadOnlyList<NativeLevySource> Sources);
public sealed record LevyCohortCasualties(LevyCohortBudget Budget, int MainSurvivors, int FinalSurvivors)
{
    public int PursuitLosses => MainSurvivors - FinalSurvivors;
}

public static partial class StatFidelitySession
{
    private static readonly Dictionary<string, LevyCohortBudget> levyCohorts = new(StringComparer.Ordinal);
    private sealed record LevyStrengthProof(string SourceSignature, int PlannedBodies, int BattleScale,
        decimal? Active, decimal? Soft);
    private static readonly Dictionary<string, LevyStrengthProof> levyStrengthProofs = new(StringComparer.Ordinal);
    public static IReadOnlyList<LevyCohortBudget> LevyCohorts => levyCohorts.Values.ToArray();

    public static void ValidateNativeUnitCultures(Army army)
    {
        if (!HasBoundExport) return;
        foreach (var unit in army.Units.Where(u => u.GetObjCulture() == null && (u.GetSoldiers() > 0 || u.GetOriginalSoldiers() > 0)))
        {
            var fallback = army.Owner?.GetCulture();
            if (fallback == null || string.IsNullOrEmpty(fallback.ID))
                throw Failure("Positive native unit " + (unit.SourceIdentity ?? unit.GetName()) +
                    " has no source or verified army-owner culture. Its soldiers cannot be merged into another role or population.");
            unit.ChangeCulture(fallback);
            Note("Unit " + (unit.SourceIdentity ?? unit.GetName()) + " uses its saved army owner's culture for appearance because its source culture is unavailable; source identity and strength are unchanged.");
        }
    }

    public static void RegisterLevyCohorts(Army army, bool verifyOriginalStrength = false)
    {
        if (!HasBoundExport || army.IsGarrisonArmy) return;
        var sources = army.ArmyRegiments.Where(r => r.Type == RegimentType.Levy).SelectMany(r => r.Regiments.Select(s => (Regiment: r, Source: s)))
            .Select(s => new { s.Regiment, s.Source, Culture = string.IsNullOrEmpty(s.Source.Culture?.ID) ? army.Owner?.GetCulture()?.ID : s.Source.Culture.ID }).ToArray();
        foreach (var group in sources.GroupBy(s => s.Culture))
        {
            if (string.IsNullOrEmpty(group.Key)) throw Failure("Native levy source lacks both source and owner culture; no troop cohort can be inferred.");
            string identity = army.ID + ":" + group.Key;
            if (!verifyOriginalStrength && levyCohorts.ContainsKey(identity)) continue;
            var rows = group.Select(s => {
                if (!int.TryParse(s.Source.CurrentNum, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                    throw Failure("Invalid current native levy strength for " + s.Source.ID);
                return new NativeLevySource(s.Regiment.ID, s.Source.ID, string.IsNullOrEmpty(s.Source.Index) ? "0" : s.Source.Index, count);
            }).OrderBy(s => s.ArmyRegimentId, StringComparer.Ordinal).ThenBy(s => s.RegimentId, StringComparer.Ordinal)
                .ThenBy(s => s.ChunkIndex, StringComparer.Ordinal).ToArray();
            if (rows.Select(s => (s.ArmyRegimentId, s.RegimentId, s.ChunkIndex)).Distinct().Count() != rows.Length)
                throw Failure("Duplicate exact native levy source in cohort " + identity);
            int native = checked(rows.Sum(s => s.Soldiers));
            int bodies = checked(army.Units.Where(u => u.GetRegimentType() == RegimentType.Levy && u.GetObjCulture()?.ID == group.Key).Sum(u => u.GetSoldiers()) +
                army.Units.Where(u => u.CommanderGuardRepresentation is { DonorType: RegimentType.Levy } guard && guard.DonorCultureId == group.Key)
                    .Sum(u => u.CommanderGuardRepresentation!.BorrowedBodies));
            bool hasPopulation = TryGetNativeLevyDeployment(army.ID, group.Key, out var population);
            if (HasNativePopulationForArmy(army.ID) && !hasPopulation)
                throw Failure("Native levy cohort lacks its active/reserve deployment plan: " + identity);
            if (hasPopulation && (population.Stock != native || population.DeployedBodies != bodies ||
                population.BattleScale != ArmyProportions.BattleScale))
                throw Failure("Native levy stock or active representation differs from its immutable population plan: " + identity);
            decimal active = hasPopulation ? population.Active : native;
            decimal? proofActive = hasPopulation ? population.Active : null, proofSoft = hasPopulation ? population.Soft : null;
            if (verifyOriginalStrength)
            {
                string signature = string.Join("|", rows.Select(r => r.ArmyRegimentId + ":" + r.RegimentId + ":" + r.ChunkIndex + ":" + r.Soldiers));
                if (levyStrengthProofs.TryGetValue(identity, out var prior))
                {
                    if (prior.SourceSignature != signature || prior.PlannedBodies != bodies || prior.BattleScale != ArmyProportions.BattleScale ||
                        prior.Active != proofActive || prior.Soft != proofSoft)
                        throw Failure("Levy regeneration changed its native sources, represented body total, or battle scale. Rebuild from the native handoff before generating another roster: " + identity);
                }
                else
                {
                    if (!hasPopulation)
                    {
                        int original = checked(army.Units.Where(u => u.GetRegimentType() == RegimentType.Levy && u.GetObjCulture()?.ID == group.Key)
                            .Sum(u => u.GetOriginalSoldiers()));
                        if (original != native)
                            throw Failure("Initial levy representation omits or adds native source strength in cohort " + identity + ": native " + native + ", represented original " + original + ".");
                    }
                    levyStrengthProofs.Add(identity, new(signature, bodies, ArmyProportions.BattleScale,
                        proofActive, proofSoft));
                }
            }
            if (levyCohorts.ContainsKey(identity)) continue;
            if (active > 0 && bodies <= 0)
                throw Failure("Positive native levy cohort " + identity + " has no represented troops. Repair its mapping or increase battle scale; no source was silently omitted.");
            if (active == 0 && bodies != 0) throw Failure("Inactive native levy cohort has positive generated strength: " + identity);
            levyCohorts.Add(identity, new(army.ID, army.CombatSide, group.Key, native, bodies, Array.AsReadOnly(rows)));
        }
        var nativeCultures = sources.Select(s => s.Culture).ToHashSet(StringComparer.Ordinal);
        if (army.Units.Any(u => u.GetRegimentType() == RegimentType.Levy && u.GetSoldiers() > 0 && !nativeCultures.Contains(u.GetObjCulture()?.ID)))
            throw Failure("Generated levy culture has no native source cohort in army " + army.ID + ". Culture reassignment cannot supply casualty identity.");
    }

    public static void ValidateLevyCohortDeployment(Army army)
    {
        if (!HasBoundExport || army.IsGarrisonArmy) return;
        RegisterLevyCohorts(army);
        foreach (var budget in levyCohorts.Values.Where(b => b.ArmyId == army.ID))
        {
            var units = army.Units.Where(u => u.GetRegimentType() == RegimentType.Levy && u.GetObjCulture()?.ID == budget.CultureId).ToArray();
            if (units.Any(u => u.IsSiege())) throw Failure("Native levy troops cannot be interpreted as siege machines without an explicit separate representation budget.");
            int represented = checked(units.Sum(u => u.GetSoldiers()) + army.Units.Where(u =>
                u.CommanderGuardRepresentation is { DonorType: RegimentType.Levy } guard && guard.DonorCultureId == budget.CultureId)
                .Sum(u => u.CommanderGuardRepresentation!.BorrowedBodies));
            bool hasPopulation = TryGetNativeLevyDeployment(army.ID, budget.CultureId, out var population);
            decimal active = hasPopulation ? population.Active : budget.NativeSoldiers;
            if (represented != budget.DeployedBodies || active > 0 && represented <= 0 ||
                hasPopulation && (population.Stock != budget.NativeSoldiers || population.DeployedBodies != represented))
                throw Failure("Generated levy cohort does not preserve its planned native representation: army " + army.ID + ", culture " + budget.CultureId);
        }
    }

    public static IReadOnlyList<LevyCohortCasualties> GetLevyCohortCasualties(Army army)
    {
        if (!HasBoundExport || army.IsGarrisonArmy) return Array.Empty<LevyCohortCasualties>();
        ValidateLevyCohortDeployment(army);
        var guards = GetCommanderGuardCasualties(army);
        var result = new List<LevyCohortCasualties>();
        foreach (var budget in levyCohorts.Values.Where(b => b.ArmyId == army.ID))
        {
            var guard = guards.SingleOrDefault(g => g.Representation.DonorType == RegimentType.Levy && g.Representation.DonorCultureId == budget.CultureId);
            if (guard != null)
            {
                if (guard.Representation.NativeSoldiers != budget.NativeSoldiers || guard.Representation.CohortDeployedBodies != budget.DeployedBodies)
                    throw Failure("Guard and native levy cohort budgets disagree.");
                result.Add(new(budget, guard.MainSurvivors, guard.FinalSurvivors)); continue;
            }
            if (budget.NativeSoldiers == 0) { result.Add(new(budget, 0, 0)); continue; }
            long main = 0, final = 0, bodies = 0;
            foreach (var unit in army.Units.Where(u => u.GetRegimentType() == RegimentType.Levy && u.GetObjCulture()?.ID == budget.CultureId))
            {
                var rows = army.CasualitiesReports.Where(r => r.Script.Contains("_" + unit.UniqueID + "_CULTURE", StringComparison.Ordinal)).ToArray();
                if (rows.Length != 1 || rows[0].GetStarting() != unit.GetSoldiers())
                    throw Failure("Native levy cohort lacks exact raw casualty evidence for unit " + unit.UniqueID);
                var row = rows[0]; int after = row.GetAliveAfterPursuit() < 0 ? row.GetAliveBeforePursuit() : row.GetAliveAfterPursuit();
                if (row.GetAliveBeforePursuit() > row.GetStarting() || after > row.GetAliveBeforePursuit() || after < 0)
                    throw Failure("Native levy cohort has impossible raw casualty evidence.");
                bodies += unit.GetSoldiers(); main += row.GetAliveBeforePursuit(); final += after;
            }
            if (bodies != budget.DeployedBodies || bodies <= 0) throw Failure("Native levy cohort has incomplete deployed evidence.");
            int Count(long surviving) => NativeCohortProjection.Survivors(budget.NativeSoldiers, bodies, surviving);
            result.Add(new(budget, Count(main), Count(final)));
        }
        return result.AsReadOnly();
    }
}
