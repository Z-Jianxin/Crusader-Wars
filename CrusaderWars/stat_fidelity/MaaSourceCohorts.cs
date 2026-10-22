using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CrusaderWars.data.save_file;

namespace CrusaderWars.StatFidelity;

public sealed record MaaSourceBudget(string ArmyId, string Side, string ArmyRegimentId, string RegimentId,
    string ChunkIndex, int NativeSoldiers, int DeployedBodies)
{
    public string Identity => Key(ArmyId, ArmyRegimentId, RegimentId, ChunkIndex);
    public static string Key(string army, string armyRegiment, string source, string? chunk) =>
        army + ":" + armyRegiment + ":" + source + ":" + (string.IsNullOrEmpty(chunk) ? "0" : chunk);
}
public sealed record MaaSourceCasualties(MaaSourceBudget Budget, int MainSurvivors, int FinalSurvivors)
{
    public int PursuitLosses => MainSurvivors - FinalSurvivors;
}

public static partial class StatFidelitySession
{
    private static readonly Dictionary<string, MaaSourceBudget> maaSources = new(StringComparer.Ordinal);
    private sealed record MaaStrengthProof(int NativeSoldiers, int PlannedBodies, int BattleScale);
    private static readonly Dictionary<string, MaaStrengthProof> maaStrengthProofs = new(StringComparer.Ordinal);
    private static readonly HashSet<string> maaRawFormations = new(StringComparer.Ordinal);
    public static IReadOnlyList<MaaSourceBudget> MaaSourceCohorts => maaSources.Values.ToArray();
    public static bool IsMaaBoundFormation(string script) => HasBoundExport && maaRawFormations.Contains(
        script.StartsWith("kills_", StringComparison.Ordinal) ? script[6..] : script);

    public static void RegisterMaaSourceCohorts(Army army, bool verifyOriginalStrength = false)
    {
        if (!HasBoundExport || army.IsGarrisonArmy) return;
        foreach (var regiment in army.ArmyRegiments.Where(r => r.Type == RegimentType.MenAtArms))
        foreach (var source in regiment.Regiments)
        {
            if (ArmiesReader.TryGetNativeMaaDefinition(regiment.MAA_Name, out var definition) &&
                (definition.IsSiegeEquipment || definition.IsFieldNoncombatant)) continue;
            string identity = MaaSourceBudget.Key(army.ID, regiment.ID, source.ID, source.Index);
            if (!verifyOriginalStrength && maaSources.ContainsKey(identity)) continue;
            var units = army.Units.Where(u => u.GetRegimentType() == RegimentType.MenAtArms &&
                MaaSourceBudget.Key(u.SourceArmyId ?? "", u.SourceArmyRegimentId ?? "", u.SourceRegimentId ?? "", u.SourceChunkIndex) == identity).ToArray();
            if (siegeSources.ContainsKey(identity) || units.Any(u => u.IsSiege())) continue;
            if (!int.TryParse(source.CurrentNum, NumberStyles.None, CultureInfo.InvariantCulture, out int native))
                throw Failure("Invalid native MAA source strength: " + identity);
            var guards = army.Units.Where(u => u.CommanderGuardRepresentation is {DonorType: RegimentType.MenAtArms} g &&
                MaaSourceBudget.Key(g.ArmyId, g.SourceArmyRegimentId!, g.SourceRegimentId!, g.SourceChunkIndex) == identity).ToArray();
            int bodies = checked(units.Sum(u => u.GetSoldiers()) + guards.Sum(u => u.CommanderGuardRepresentation!.BorrowedBodies));
            if (verifyOriginalStrength)
            {
                if (maaStrengthProofs.TryGetValue(identity, out var prior))
                {
                    if (prior.NativeSoldiers != native || prior.PlannedBodies != bodies || prior.BattleScale != ArmyProportions.BattleScale)
                        throw Failure("MAA regeneration changed its native strength, represented body total, or battle scale. Rebuild from the native handoff: " + identity);
                }
                else
                {
                    int original = checked(units.Sum(u => u.GetOriginalSoldiers()));
                    if (original != native || guards.Length > 0)
                        throw Failure("Initial MAA representation does not prove its full native source strength: " + identity);
                    maaStrengthProofs.Add(identity, new(native, bodies, ArmyProportions.BattleScale));
                }
            }
            if (maaSources.ContainsKey(identity)) continue;
            if (native > 0 && bodies <= 0) throw Failure("Positive native MAA source has no represented formation: " + identity);
            if (native == 0 && bodies > 0) throw Failure("Zero native MAA source has positive generated strength: " + identity);
            maaSources.Add(identity, new(army.ID, army.CombatSide, regiment.ID, source.ID,
                string.IsNullOrEmpty(source.Index) ? "0" : source.Index, native, bodies));
        }
    }

    public static void ValidateMaaSourceDeployment(Army army)
    {
        if (!HasBoundExport || army.IsGarrisonArmy) return;
        RegisterMaaSourceCohorts(army);
        foreach (var budget in maaSources.Values.Where(b => b.ArmyId == army.ID))
        {
            int represented = checked(army.Units.Where(u => u.GetRegimentType() == RegimentType.MenAtArms &&
                MaaSourceBudget.Key(u.SourceArmyId ?? "", u.SourceArmyRegimentId ?? "", u.SourceRegimentId ?? "", u.SourceChunkIndex) == budget.Identity).Sum(u => u.GetSoldiers()) +
                army.Units.Where(u => u.CommanderGuardRepresentation is {DonorType: RegimentType.MenAtArms} g &&
                    MaaSourceBudget.Key(g.ArmyId, g.SourceArmyRegimentId!, g.SourceRegimentId!, g.SourceChunkIndex) == budget.Identity).Sum(u => u.CommanderGuardRepresentation!.BorrowedBodies));
            if (represented != budget.DeployedBodies) throw Failure("MAA source deployment changed its planned body count: " + budget.Identity);
        }
    }

    public static IReadOnlyList<MaaSourceCasualties> GetMaaSourceCasualties(Army army)
    {
        if (!HasBoundExport || army.IsGarrisonArmy) return Array.Empty<MaaSourceCasualties>();
        ValidateMaaSourceDeployment(army);
        var guards = GetCommanderGuardCasualties(army);
        var result = new List<MaaSourceCasualties>();
        foreach (var budget in maaSources.Values.Where(b => b.ArmyId == army.ID))
        {
            var guard = guards.SingleOrDefault(g => g.Representation.DonorType == RegimentType.MenAtArms &&
                MaaSourceBudget.Key(g.Representation.ArmyId, g.Representation.SourceArmyRegimentId!, g.Representation.SourceRegimentId!, g.Representation.SourceChunkIndex) == budget.Identity);
            if (guard != null)
            {
                if (guard.Representation.NativeSoldiers != budget.NativeSoldiers || guard.Representation.CohortDeployedBodies != budget.DeployedBodies)
                    throw Failure("Guard and native MAA source budgets disagree.");
                result.Add(new(budget, guard.MainSurvivors, guard.FinalSurvivors)); continue;
            }
            if (budget.NativeSoldiers == 0) { result.Add(new(budget, 0, 0)); continue; }
            long main = 0, final = 0, bodies = 0;
            foreach (var unit in army.Units.Where(u => u.GetRegimentType() == RegimentType.MenAtArms &&
                MaaSourceBudget.Key(u.SourceArmyId ?? "", u.SourceArmyRegimentId ?? "", u.SourceRegimentId ?? "", u.SourceChunkIndex) == budget.Identity))
            {
                var reports = army.CasualitiesReports.Where(r => r.Script.Contains("_" + unit.UniqueID + "_CULTURE", StringComparison.Ordinal)).ToArray();
                if (reports.Length != 1 || reports[0].GetStarting() != unit.GetSoldiers())
                    throw Failure("Native MAA source lacks exact raw casualty evidence for unit " + unit.UniqueID);
                var report = reports[0]; int after = report.GetAliveAfterPursuit() < 0 ? report.GetAliveBeforePursuit() : report.GetAliveAfterPursuit();
                if (after < 0 || after > report.GetAliveBeforePursuit() || report.GetAliveBeforePursuit() > report.GetStarting())
                    throw Failure("Native MAA source has impossible raw casualty evidence.");
                bodies += unit.GetSoldiers(); main += report.GetAliveBeforePursuit(); final += after;
            }
            if (bodies != budget.DeployedBodies || bodies <= 0) throw Failure("Native MAA source has incomplete deployed evidence.");
            result.Add(new(budget, NativeCohortProjection.Survivors(budget.NativeSoldiers, bodies, main),
                NativeCohortProjection.Survivors(budget.NativeSoldiers, bodies, final)));
        }
        return result.AsReadOnly();
    }
}
