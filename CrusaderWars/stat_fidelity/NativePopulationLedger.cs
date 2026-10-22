using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CrusaderWars.data.save_file;

namespace CrusaderWars.StatFidelity;

public sealed record NativePopulationSourceStock(string RegimentId, string ChunkIndex, int Stock, string CultureId);
public sealed record NativePopulationSourceBudget(string ArmyId, string ArmyRegimentId, string RegimentId, string ChunkIndex,
    string CultureId, int Stock, decimal Active, decimal Soft, decimal StockRoundingResidual)
{
    public string Identity => MaaSourceBudget.Key(ArmyId, ArmyRegimentId, RegimentId, ChunkIndex);
}
public sealed record NativePopulationBudget(string Side, string ArmyId, string ArmyRegimentId, RegimentType Kind,
    string NativeType, int Stock, decimal InitialActive, decimal PreservedSoft, decimal StockRoundingResidual,
    IReadOnlyList<NativePopulationSourceBudget> Sources)
{
    public bool IsKnight => Kind == RegimentType.Knight;
    public bool IsCharacterIdentityReserve => IsKnight && Stock == 1 && InitialActive == 0 && PreservedSoft == 0;
    public bool IsEquipmentReserve => false;
    public string Identity => Side + ":" + ArmyRegimentId;
    public string AllocationEvidence => "Aggregate native active/soft counts allocated jointly to exact source chunks by original stock weights; per-source wounds were not observed.";
}
public sealed record NativePopulationBodySurvival(string Script, int Bodies, int MainSurvivors, int FinalSurvivors, decimal Weight);
public sealed record NativePopulationSourceSurvival(string SourceIdentity, decimal DeployedBodies,
    decimal MainSurvivingBodies, decimal FinalSurvivingBodies, IReadOnlyList<NativePopulationBodySurvival>? Components = null);
public sealed record NativePopulationSourceOutcome(NativePopulationSourceBudget Budget,
    decimal MainActiveUnrounded, decimal FinalActiveUnrounded, int MainReturnedStock, int ReturnedStock,
    NativePopulationSourceSurvival? RawSurvival);
public sealed record NativePopulationOutcome(NativePopulationBudget Budget, decimal MainActiveRemaining,
    decimal FinalActiveRemaining, decimal FinalSoft, int MainReturnedStock, int ReturnedStock,
    decimal MainPrecisionDelta, decimal FinalPrecisionDelta, bool CharacterUnavailable,
    IReadOnlyList<NativePopulationSourceOutcome> SourceOutcomes, NativePopulationSourceSurvival? CharacterSurvival = null,
    bool IndependentCommanderRemoval = false, bool SyntheticUnharmedControl = false)
{
    public string Side => Budget.Side;
    public string ArmyId => Budget.ArmyId;
    public string ArmyRegimentId => Budget.ArmyRegimentId;
    public int Stock => Budget.Stock;
    public decimal InitialActive => Budget.InitialActive;
    public decimal PreservedSoft => Budget.PreservedSoft;
    public bool IsKnight => Budget.IsKnight;
    public bool IsEquipmentReserve => false;
}

/// <summary>Observed five-decimal native counts; no mortality or recovery rule is inferred.</summary>
public static class NativePopulationLedger
{
    public const decimal NativePrecisionFactor = 100000m;
    public static decimal NormalizeActive(decimal value)
    {
        if (value < 0 || value > int.MaxValue) throw Invalid("Native active count is outside supported bounds.");
        return decimal.Truncate(value * NativePrecisionFactor) / NativePrecisionFactor;
    }

    public static NativePopulationBudget Create(string side, string armyId, string armyRegimentId, RegimentType kind,
        string nativeType, int stock, decimal active, decimal soft, IReadOnlyList<NativePopulationSourceStock> sources)
    {
        if (side is not ("attacker" or "defender") || string.IsNullOrWhiteSpace(armyId) || string.IsNullOrWhiteSpace(armyRegimentId)
            || kind is not (RegimentType.MenAtArms or RegimentType.Levy or RegimentType.Knight) || stock < 0)
            throw Invalid("Invalid native population identity or stock.");
        if (NormalizeActive(active) != active || NormalizeActive(soft) != soft)
            throw Invalid("Native active/soft precision differs from the verified five-decimal contract; no input was silently rounded.");
        if (kind == RegimentType.Knight)
        {
            if (stock > 1 || active > 1 || soft > 1 || sources.Count != 0)
                throw Invalid("A native knight must remain one character identity, not an ordinary source population.");
            if (decimal.Ceiling(active + soft) != stock && !(stock == 1 && active == 0 && soft == 0))
                throw Invalid("Native character population is inconsistent with its saved identity stock.");
            return new(side, armyId, armyRegimentId, kind, nativeType, stock, active, soft, stock - active - soft,
                Array.Empty<NativePopulationSourceBudget>());
        }
        if (decimal.Ceiling(active + soft) != stock)
            throw Invalid("Native stock must equal ceil(explicit active + explicit soft); soft was not inferred from stock.");
        if (sources.Any(s => s.Stock < 0 || string.IsNullOrWhiteSpace(s.RegimentId)) || sources.Sum(s => (long)s.Stock) != stock ||
            sources.Select(s => (s.RegimentId, Chunk(s.ChunkIndex))).Distinct().Count() != sources.Count)
            throw Invalid("Exact native source stocks are missing, duplicated, or disagree with their army-regiment cache.");
        var ordered = sources.OrderBy(s => s.RegimentId, StringComparer.Ordinal).ThenBy(s => Chunk(s.ChunkIndex), StringComparer.Ordinal).ToArray();
        decimal[] totals = AllocateDecimal(active + soft, ordered.Select(s => (decimal)s.Stock).ToArray());
        decimal[] actives = AllocateDecimal(active, totals);
        var allocated = new List<NativePopulationSourceBudget>();
        decimal softAssigned = 0;
        int lastPositive = Array.FindLastIndex(totals, v => v > 0);
        for (int index = 0; index < ordered.Length; index++)
        {
            decimal reserved = index == lastPositive ? soft - softAssigned : totals[index] - actives[index];
            if (totals[index] == 0) reserved = 0;
            if (reserved < 0 || actives[index] < 0 || reserved + actives[index] > ordered[index].Stock)
                throw Invalid("Joint source active/soft allocation exceeded a native source stock.");
            softAssigned += reserved;
            allocated.Add(new(armyId, armyRegimentId, ordered[index].RegimentId, Chunk(ordered[index].ChunkIndex),
                ordered[index].CultureId, ordered[index].Stock, actives[index], reserved,
                ordered[index].Stock - actives[index] - reserved));
        }
        if (allocated.Sum(s => s.Active) != active || allocated.Sum(s => s.Soft) != soft)
            throw Invalid("Joint source allocation lost native decimal population.");
        return new(side, armyId, armyRegimentId, kind, nativeType, stock, active, soft, stock - active - soft, allocated.AsReadOnly());
    }

    public static NativePopulationOutcome Project(NativePopulationBudget budget,
        IReadOnlyList<NativePopulationSourceSurvival> survival, bool characterUnavailable = false)
    {
        if (budget.IsKnight) throw Invalid("Knight outcomes require the explicit character-identity projection.");
        if (characterUnavailable) throw Invalid("Ordinary soft reserves cannot be removed by a character fate.");
        var rows = survival.ToDictionary(s => s.SourceIdentity, StringComparer.Ordinal);
        if (rows.Keys.Any(key => !budget.Sources.Any(s => s.Identity == key))) throw Invalid("Survival evidence names an unrelated source.");
        var main = new PopulationFraction[budget.Sources.Count]; var final = new PopulationFraction[budget.Sources.Count];
        var sourceSoft = new PopulationFraction[budget.Sources.Count];
        var raw = new NativePopulationSourceSurvival?[budget.Sources.Count];
        for (int index = 0; index < budget.Sources.Count; index++)
        {
            var source = budget.Sources[index];
            var share = budget.Stock == 0 ? default : PopulationFraction.FromInt(source.Stock) / PopulationFraction.FromInt(budget.Stock);
            sourceSoft[index] = PopulationFraction.FromDecimal(budget.PreservedSoft) * share;
            if (source.Active == 0)
            {
                if (rows.ContainsKey(source.Identity)) throw Invalid("Inactive source unexpectedly supplied physical survival evidence: " + source.Identity);
                continue;
            }
            if (!rows.TryGetValue(source.Identity, out var evidence)) throw Invalid("Active source has no raw survival evidence: " + source.Identity);
            ValidateSurvival(evidence);
            var active = PopulationFraction.FromDecimal(budget.InitialActive) * share;
            main[index] = active * SurvivalRatio(evidence, false);
            final[index] = active * SurvivalRatio(evidence, true);
            raw[index] = evidence;
        }
        var mainUnrounded = main.Aggregate(default(PopulationFraction), (sum, value) => sum + value);
        var finalUnrounded = final.Aggregate(default(PopulationFraction), (sum, value) => sum + value);
        decimal mainActive = mainUnrounded.TruncateNative5(), finalActive = finalUnrounded.TruncateNative5();
        int mainStock = Stock(mainActive, budget.PreservedSoft, budget.Stock), finalStock = Stock(finalActive, budget.PreservedSoft, budget.Stock);
        var caps = budget.Sources.Select(s => s.Stock).ToArray();
        int[] mainParts = ApportionStock(main.Select((a, i) => a + sourceSoft[i]).ToArray(), caps, mainStock);
        int[] finalParts = ApportionStock(final.Select((a, i) => a + sourceSoft[i]).ToArray(), mainParts, finalStock, allowFractionAboveRoundedCap: true);
        var sourceOutcomes = budget.Sources.Select((source, i) => new NativePopulationSourceOutcome(source,
            main[i].ToDecimal(), final[i].ToDecimal(), mainParts[i], finalParts[i], raw[i])).ToArray();
        return new(budget, mainActive, finalActive, budget.PreservedSoft, mainStock, finalStock,
            (mainUnrounded - PopulationFraction.FromDecimal(mainActive)).ToDecimal(),
            (finalUnrounded - PopulationFraction.FromDecimal(finalActive)).ToDecimal(), false, Array.AsReadOnly(sourceOutcomes));
    }

    public static NativePopulationOutcome ProjectKnight(NativePopulationBudget budget,
        NativePopulationSourceSurvival? survival, bool characterUnavailable, bool independentCommanderRemoval = false)
    {
        if (!budget.IsKnight) throw Invalid("Character projection requires a native knight identity.");
        if (budget.InitialActive == 0)
        {
            if (characterUnavailable)
            {
                if (!independentCommanderRemoval || budget.Stock != 1 || survival == null)
                    throw Invalid("An inactive knight cannot receive a new battle-removal fate without an independent deployed commander role.");
                ValidateSurvival(survival);
                return new(budget, 0, 0, 0, 0, 0, 0, 0, true, Array.Empty<NativePopulationSourceOutcome>(), survival, true);
            }
            return new(budget, 0, 0, budget.PreservedSoft, budget.Stock, budget.Stock, 0, 0, false, Array.Empty<NativePopulationSourceOutcome>());
        }
        if (survival == null) throw Invalid("Active knight lacks its represented retinue survival evidence.");
        ValidateSurvival(survival);
        if (characterUnavailable)
            return new(budget, 0, 0, 0, 0, 0, 0, 0, true, Array.Empty<NativePopulationSourceOutcome>(), survival);
        // Retinue bodies are not fractional character health. An available character
        // keeps its original personal A/S/stock; only an explicit removal fate clears
        // that identity. No personal recovery or phase-of-death rule is invented.
        return new(budget, budget.InitialActive, budget.InitialActive, budget.PreservedSoft, budget.Stock, budget.Stock,
            0, 0, false, Array.Empty<NativePopulationSourceOutcome>(), survival);
    }

    public static int RoundedDeployment(decimal active, int scale)
    {
        if (active < 0 || scale is < 1 or > 100) throw Invalid("Invalid active deployment strength or scale.");
        int bodies = checked((int)Math.Round(active * scale / 100m, MidpointRounding.AwayFromZero));
        if (active > 0 && bodies == 0) throw Invalid("Positive active strength is too small to represent at the requested battle scale. Increase the scale; no free actor was added.");
        return bodies;
    }

    private static decimal[] AllocateDecimal(decimal total, IReadOnlyList<decimal> weights)
    {
        var result = new decimal[weights.Count]; decimal sum = weights.Sum();
        if (total == 0) return result;
        if (sum <= 0 || total < 0 || total > sum || weights.Any(w => w < 0)) throw Invalid("Invalid bounded decimal allocation.");
        if (total == sum) return weights.ToArray();
        int last = Enumerable.Range(0, weights.Count).Last(i => weights[i] > 0); decimal assigned = 0;
        for (int index = 0; index < weights.Count; index++)
        {
            result[index] = index == last ? total - assigned : total * weights[index] / sum;
            if (result[index] < 0 || result[index] > weights[index]) throw Invalid("Decimal allocation exceeded its source capacity.");
            assigned += result[index];
        }
        return result;
    }
    private static int[] ApportionStock(IReadOnlyList<PopulationFraction> weights, IReadOnlyList<int> capacities, int target, bool allowFractionAboveRoundedCap = false)
    {
        if (weights.Count != capacities.Count || target < 0 || target > capacities.Sum(n => (long)n) ||
            weights.Where((weight, index) => weight < default(PopulationFraction) ||
                (allowFractionAboveRoundedCap ? weight.FloorInt32() > capacities[index] : weight > PopulationFraction.FromInt(capacities[index]))).Any())
            throw Invalid("Invalid joint source-stock target or weights.");
        var result = weights.Select(w => w.FloorInt32()).ToArray();
        int remainder = target - result.Sum();
        if (remainder < 0) throw Invalid("Normalized population target is smaller than bounded source floors.");
        while (remainder > 0)
        {
            var eligible = Enumerable.Range(0, weights.Count).Where(i => result[i] < capacities[i])
                .OrderByDescending(i => weights[i] - PopulationFraction.FromInt(result[i])).ThenBy(i => i).ToArray();
            if (eligible.Length == 0) throw Invalid("Joint native stock exhausted its phase-monotone source capacities.");
            foreach (int index in eligible.Take(remainder)) { result[index]++; remainder--; }
        }
        if (result.Sum() != target) throw Invalid("Joint native stock could not be apportioned without exceeding original source stocks.");
        return result;
    }
    private static int Stock(decimal active, decimal soft, int original)
    {
        int result = checked((int)decimal.Ceiling(active + soft));
        if (result < 0 || result > original) throw Invalid("Returned stock would exceed its original native bound.");
        return result;
    }
    private static void ValidateSurvival(NativePopulationSourceSurvival row)
    {
        if (row.DeployedBodies <= 0 || row.FinalSurvivingBodies < 0 || row.MainSurvivingBodies < row.FinalSurvivingBodies || row.MainSurvivingBodies > row.DeployedBodies)
            throw Invalid("Invalid raw active-population survivor evidence.");
        if (row.Components != null && (row.Components.Count == 0 || row.Components.Any(c => c.Weight <= 0 || c.Bodies <= 0 ||
            c.FinalSurvivors < 0 || c.MainSurvivors < c.FinalSurvivors || c.MainSurvivors > c.Bodies) || row.Components.Sum(c => c.Weight) != row.DeployedBodies))
            throw Invalid("Invalid exact raw component population evidence.");
    }
    private static PopulationFraction SurvivalRatio(NativePopulationSourceSurvival row, bool final)
    {
        if (row.Components == null)
            return PopulationFraction.FromDecimal(final ? row.FinalSurvivingBodies : row.MainSurvivingBodies) / PopulationFraction.FromDecimal(row.DeployedBodies);
        var alive = row.Components.Aggregate(default(PopulationFraction), (sum, component) => sum +
            PopulationFraction.FromDecimal(component.Weight) * PopulationFraction.FromInt(final ? component.FinalSurvivors : component.MainSurvivors) / PopulationFraction.FromInt(component.Bodies));
        return alive / PopulationFraction.FromDecimal(row.DeployedBodies);
    }
    private static string Chunk(string? chunk) => string.IsNullOrEmpty(chunk) ? "0" : chunk;
    private static InvalidDataException Invalid(string message) => new(message);
}
