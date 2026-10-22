using System;
using System.Collections.Generic;
using System.Linq;
using CrusaderWars.data.save_file;

namespace CrusaderWars.StatFidelity;

public sealed record NativePopulationDeployment(string Identity, string ArmyId, RegimentType Kind, string CultureId,
    int Stock, decimal Active, decimal Soft, int DeployedBodies, int BattleScale, IReadOnlyList<string> SourceIdentities);

public static partial class StatFidelitySession
{
    private static readonly Dictionary<string, NativePopulationDeployment> nativePopulationDeployments = new(StringComparer.Ordinal);
    public static IReadOnlyList<NativePopulationDeployment> NativePopulationDeployments => nativePopulationDeployments.Values.ToArray();
    public static bool HasNativePopulationForArmy(string armyId) => HasBoundExport && nativePopulationBudgets.Values.Any(b => b.ArmyId == armyId);
    public static bool TryGetNativeMaaDeployment(string sourceIdentity, out NativePopulationDeployment deployment) => nativePopulationDeployments.TryGetValue("maa:" + sourceIdentity, out deployment!);
    public static bool TryGetNativeLevyDeployment(string armyId, string culture, out NativePopulationDeployment deployment) => nativePopulationDeployments.TryGetValue("levy:" + armyId + ":" + culture, out deployment!);
    public static bool IsInactiveNativeKnight(string side, string regimentId) => TryGetNativePopulationBudget(side, regimentId, out var b) && b.IsKnight && b.InitialActive == 0;

    /// <summary>Reader mapping may inspect only actors; keep inactive stock placeholders for the later strength proof.</summary>
    public static bool IsInactiveNativePopulationPlaceholder(Army army, Unit unit)
    {
        if (!HasBoundExport) return false;
        if (unit.GetRegimentType() == RegimentType.MenAtArms)
        {
            if (TryGetNativePopulationBudget(army.CombatSide, unit.SourceArmyRegimentId ?? "", out var budget))
                return budget.ArmyId == army.ID && budget.Sources.Any(s => s.Identity ==
                    MaaSourceBudget.Key(unit.SourceArmyId ?? "", unit.SourceArmyRegimentId ?? "", unit.SourceRegimentId ?? "", unit.SourceChunkIndex)) && budget.InitialActive == 0;
            return false; // Equipment follows its separately validated reserve/generation path.
        }
        if (unit.GetRegimentType() != RegimentType.Levy || !HasNativePopulationForArmy(army.ID)) return false;
        string culture = !string.IsNullOrEmpty(unit.GetObjCulture()?.ID) ? unit.GetObjCulture()!.ID : army.Owner?.GetCulture()?.ID ?? "";
        var sources = nativePopulationBudgets.Values.Where(b => b.ArmyId == army.ID && b.Kind == RegimentType.Levy)
            .SelectMany(b => b.Sources).Where(s => s.CultureId == culture).ToArray();
        return sources.Length > 0 && sources.All(s => s.Active == 0);
    }

    public static void ApplyNativeActiveDeployment(Army army)
    {
        if (!HasBoundExport || (!HasNativePopulationForArmy(army.ID) && !nativeEquipmentInputs.Values.Any(b => b.ArmyId == army.ID))) return;
        var ordinary = nativePopulationBudgets.Values.Where(b => b.ArmyId == army.ID && !b.IsKnight).ToArray();
        foreach (var budget in ordinary.Where(b => b.Kind == RegimentType.MenAtArms))
        {
            int target = NativePopulationLedger.RoundedDeployment(budget.InitialActive, ArmyProportions.BattleScale);
            int[] allocations = SurvivorApportionment.Allocate(budget.Sources.Select(s => s.Stock).ToArray(), target);
            for (int index = 0; index < budget.Sources.Count; index++)
            {
                var source = budget.Sources[index];
                if (source.Active > 0 && allocations[index] == 0)
                    throw Failure("A positive active MAA source is too small to represent separately at this scale: " + source.Identity + ". Increase the battle scale.");
                var units = army.Units.Where(u => u.GetRegimentType() == RegimentType.MenAtArms &&
                    MaaSourceBudget.Key(u.SourceArmyId ?? "", u.SourceArmyRegimentId ?? "", u.SourceRegimentId ?? "", u.SourceChunkIndex) == source.Identity).ToArray();
                Deploy("maa:" + source.Identity, RegimentType.MenAtArms, source.CultureId, source.Stock, source.Active, source.Soft, new[] {source.Identity}, units, allocations[index]);
            }
        }
        foreach (var group in ordinary.Where(b => b.Kind == RegimentType.Levy).SelectMany(b => b.Sources).GroupBy(s => s.CultureId))
        {
            var units = army.Units.Where(u => u.GetRegimentType() == RegimentType.Levy &&
                (!string.IsNullOrEmpty(u.GetObjCulture()?.ID) ? u.GetObjCulture()!.ID : army.Owner?.GetCulture()?.ID) == group.Key).ToArray();
            var exactActive = group.Aggregate(default(PopulationFraction), (sum, source) => {
                var budget = nativePopulationBudgets.Values.Single(b => b.ArmyId == army.ID && b.ArmyRegimentId == source.ArmyRegimentId);
                return budget.Stock == 0 ? sum : sum + PopulationFraction.FromDecimal(budget.InitialActive) * PopulationFraction.FromInt(source.Stock) / PopulationFraction.FromInt(budget.Stock);
            });
            int target = (exactActive * PopulationFraction.FromInt(ArmyProportions.BattleScale) / PopulationFraction.FromInt(100) + PopulationFraction.FromDecimal(0.5m)).FloorInt32();
            if (!exactActive.IsZero && target == 0) throw Failure("Positive active levy cohort is too small at the requested battle scale: " + army.ID + ":" + group.Key);
            Deploy("levy:" + army.ID + ":" + group.Key, RegimentType.Levy, group.Key, checked(group.Sum(s => s.Stock)),
                exactActive.ToDecimal(), group.Sum(s => s.Soft), group.Select(s => s.Identity).ToArray(), units, target);
        }
        if (!nativePopulationBudgets.Values.Any(b => b.ArmyId == army.ID && b.InitialActive > 0) &&
            !nativeEquipmentInputs.Values.Any(b => b.ArmyId == army.ID && b.Active > 0))
            throw Failure("Army " + army.ID + " has only reserved/soft population. An engine-only general cannot substitute for active combatants; this handoff is unsupported.");

        void Deploy(string key, RegimentType type, string culture, int stock, decimal active, decimal soft, string[] identities, Unit[] units, int bodies)
        {
            int originals = checked(units.Sum(u => u.GetOriginalSoldiers()));
            if (nativePopulationDeployments.TryGetValue(key, out var prior))
            {
                if (prior.Stock != stock || prior.Active != active || prior.Soft != soft || prior.BattleScale != ArmyProportions.BattleScale ||
                    prior.DeployedBodies != bodies || originals != bodies)
                    throw Failure("Active-population regeneration requires the same native input, scale and complete prior formations: " + key + ". Rebuild from the native handoff.");
            }
            else if (originals != stock)
                throw Failure("Initial unit population omits or adds original native stock before active/reserve separation: " + key);
            int[] allocation = SurvivorApportionment.Allocate(units.Select(u => u.GetOriginalSoldiers()).ToArray(), bodies);
            for (int index = 0; index < units.Length; index++) units[index].SetSoldiers(allocation[index]);
            foreach (var unit in units.Where(u => u.GetSoldiers() == 0)) army.Units.Remove(unit);
            nativePopulationDeployments[key] = new(key, army.ID, type, culture, stock, active, soft, bodies,
                ArmyProportions.BattleScale, Array.AsReadOnly(identities));
        }
    }
}
