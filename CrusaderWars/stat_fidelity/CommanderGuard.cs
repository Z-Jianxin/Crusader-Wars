using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CrusaderWars.data.save_file;

namespace CrusaderWars.StatFidelity;

/// <summary>An ordinary detachment, plus at most one separately evidenced fighting character.</summary>
public sealed record CommanderGuardRepresentation(
    string ArmyId, RegimentType DonorType, string DonorTypeName, string DonorCultureId,
    string DonorBaseUnitKey, string? SourceArmyRegimentId, string? SourceRegimentId, string? SourceChunkIndex,
    int NativeSoldiers, int CohortDeployedBodies, int BorrowedBodies, int DeployedBodies,
    IReadOnlyList<int> CohortUnitIds, string? NativeKnightId, double PersonalDamageShare);

public sealed record CommanderGuardCasualtyProjection(CommanderGuardRepresentation Representation,
    int MainSurvivors, int FinalSurvivors)
{
    public int PursuitLosses => MainSurvivors - FinalSurvivors;
}

public static class CommanderGuardAccounting
{
    public static int Survivors(int nativeSoldiers, int cohortBodies,
        IReadOnlyList<(int Weight, int Bodies, int Remaining)> formations)
    {
        if (nativeSoldiers < 0 || cohortBodies <= 0 || formations.Count == 0 ||
            formations.Any(f => f.Weight <= 0 || f.Bodies <= 0 || f.Remaining < 0 || f.Remaining > f.Bodies) ||
            formations.Sum(f => (long)f.Weight) != cohortBodies)
            throw new InvalidDataException("Commander guard casualty evidence does not conserve its ordinary donor cohort.");
        decimal remaining = formations.Sum(f => (decimal)f.Weight * f.Remaining / f.Bodies);
        return checked((int)Math.Round(nativeSoldiers * remaining / cohortBodies, MidpointRounding.AwayFromZero));
    }

    public static (int Donor, int Personal) SplitCredits(int kills, double personalShare)
    {
        if (kills < 0 || !double.IsFinite(personalShare) || personalShare < 0 || personalShare > 1)
            throw new InvalidDataException("Invalid commander guard credit allocation.");
        int personal = checked((int)Math.Round(kills * personalShare, MidpointRounding.AwayFromZero));
        return (kills - personal, personal);
    }
}

public static partial class StatFidelitySession
{
    private sealed record GuardDonation(Army Army, Unit Donor, int OriginalBodies, Unit Guard);
    private static readonly Dictionary<string, GuardDonation> guardDonations = new(StringComparer.Ordinal);
    private static readonly HashSet<string> guardRawFormations = new(StringComparer.Ordinal);

    private static void ClearCommanderGuards() { guardDonations.Clear(); guardRawFormations.Clear(); }
    private static void RestoreAllCommanderGuardDonations()
    {
        foreach (var donation in guardDonations.Values.ToArray()) RestoreCommanderGuardDonation(donation.Army);
    }
    public static void RestoreCommanderGuardDonation(Army army)
    {
        if (!guardDonations.Remove(army.ID, out var donation)) return;
        donation.Donor.SetInitialSoldiers(donation.OriginalBodies);
        if (!army.Units.Contains(donation.Donor)) army.Units.Add(donation.Donor);
        donation.Guard.CommanderGuardRepresentation = null;
    }
    public static bool IsGuardBoundFormation(string script) => HasBoundExport && guardRawFormations.Contains(
        script.StartsWith("kills_", StringComparison.Ordinal) ? script[6..] : script);

    private static bool IsGuardCohortUnit(Unit unit, IEnumerable<Unit> units) => units.Any(g =>
        g.CommanderGuardRepresentation is { } guard && (ReferenceEquals(g, unit) || guard.CohortUnitIds.Contains(unit.UniqueID)));

    /// <summary>Runs after actual mapping/composition, before buffered XML and Lua registrations are published.</summary>
    public static void FundCommanderGuard(Army army, IList<XElement> emitted)
    {
        if (!HasBoundExport) return;
        var guard = army.Units.SingleOrDefault(u => u.GetRegimentType() == RegimentType.Commander);
        if (guard == null) return;
        XElement? Element(Unit unit) => emitted.SingleOrDefault(e =>
            Regex.IsMatch(e.Attribute("script_name")?.Value ?? "", "_" + unit.UniqueID + "_CULTURE", RegexOptions.CultureInvariant));
        var generalXml = Element(guard) ?? throw Failure("General has no buffered formation.");
        if (PreparedDatabase == null) throw Failure("Commander guard funding requires the active Attila database.");
        commanderKnightBudgets.TryGetValue(army.ID, out var personal);

        UnitStatProfile Profile(XElement element, Unit unit)
        {
            string key = element.Element("unit_type")?.Attribute("type")?.Value ?? "";
            return profiles.Values.SingleOrDefault(p => StatFidelityBackend.GeneratedKey(p) == key)
                ?? throw Failure("Commander guard donor lacks its exact effective emitted profile: " + unit.UniqueID);
        }
        var candidates = new List<(Unit Unit, XElement Xml, UnitStatProfile Profile, AttilaUnitCombatStats Stats)>();
        foreach (var unit in army.Units.Where(u => u.GetSoldiers() > 0 && !u.IsSiege() && u.KnightCommander == null &&
            (u.GetRegimentType() == RegimentType.MenAtArms || u.GetRegimentType() == RegimentType.Levy)))
        {
            if (unit.GetRegimentType() == RegimentType.MenAtArms && unit.SourceArmyId != army.ID) continue;
            if (unit.GetRegimentType() == RegimentType.Levy && (army.IsGarrisonArmy ||
                !Export.Levies.ContainsKey(army.CombatSide + ":" + army.Owner?.GetID()) ||
                !army.ArmyRegiments.Any(r => r.Type == RegimentType.Levy && r.Regiments.Any(s => s.Culture?.ID == unit.GetObjCulture()?.ID)))) continue;
            var element = Element(unit); if (element == null) continue;
            var profile = Profile(element, unit);
            var stats = PreparedDatabase.Query(profile.BaseUnitKey);
            if (stats.IsNaval || stats.HasSpecialEntity || !stats.CanScaleHitPoints || stats.EffectiveHitPoints is not > 0 ||
                stats.TotalMeleeDamage <= 0 || stats.Category is not ("inf_melee" or "cavalry") ||
                stats.HasMissileWeapon && (stats.Category != "inf_melee" || personal != null)) continue;
            candidates.Add((unit, element, profile, stats));
        }
        var donor = candidates.OrderBy(c => c.Unit.GetRegimentType() == RegimentType.MenAtArms ? 0 : 1)
            .ThenByDescending(c => c.Stats.IsMounted)
            .ThenByDescending(c => c.Stats.TotalMeleeDamage * c.Profile.DamageMultiplier * c.Stats.EffectiveHitPoints!.Value * c.Profile.HitPointMultiplier)
            .ThenBy(c => c.Unit.SourceIdentity ?? c.Unit.GetObjCulture()?.ID ?? "", StringComparer.Ordinal)
            .ThenBy(c => c.Profile.BaseUnitKey, StringComparer.Ordinal).ThenBy(c => c.Unit.UniqueID).FirstOrDefault();
        if (donor.Unit == null)
        {
            if (personal != null)
            {
                var plan = PlanKnightBudget(army, guard.GetAttilaUnitKey(), new[] {personal});
                if (plan.TargetAttilaDamage <= 0 && plan.TargetAttilaHitPoints <= 0)
                {
                    Note("Commander " + personal.CharacterId + " has zero native personal combat budget and no eligible ordinary donor; the minimum two-body engine representation remains.");
                    return;
                }
                int bodies = Math.Max(Settings.CommanderBodyguardSoldiers, plan.Bodies);
                var target = PreparedDatabase.Query(plan.TemplateKey);
                double damage = plan.TargetAttilaDamage / (bodies * target.TotalMeleeDamage);
                double hp = plan.TargetAttilaHitPoints / (bodies * target.EffectiveHitPoints!.Value);
                string key = Register(army.ID + ":commander-native-knight", plan.TemplateKey, damage, hp);
                guard.SetUnitKey(plan.TemplateKey); guard.SetInitialSoldiers(bodies);
                generalXml.SetAttributeValue("num_soldiers", bodies); generalXml.Element("unit_type")!.SetAttributeValue("type", key);
                Note("Commander " + personal.CharacterId + " has no eligible ordinary donor; its guard represents only its one native knight budget.");
            }
            else Note("Army " + army.ID + " has no eligible actual melee detachment; its general retains the minimum two-body engine representation, without invented prowess strength.");
            return;
        }

        var cohort = army.Units.Where(u => u.GetRegimentType() == donor.Unit.GetRegimentType() &&
            (donor.Unit.GetRegimentType() == RegimentType.MenAtArms ? u.SourceIdentity == donor.Unit.SourceIdentity : u.GetObjCulture()?.ID == donor.Unit.GetObjCulture()?.ID)).ToArray();
        if (cohort.Any(u => u.IsSiege() || Element(u) == null))
            throw Failure("Commander donor cohort contains an unrepresented or equipment formation; no mixed troop/equipment budget was borrowed.");
        int originalBodies = donor.Unit.GetSoldiers(), borrowed = Math.Min(Settings.CommanderBodyguardSoldiers, originalBodies);
        int cohortBodies = checked(cohort.Sum(u => u.GetSoldiers()));
        int nativeSoldiers;
        if (donor.Unit.GetRegimentType() == RegimentType.MenAtArms)
        {
            var source = army.ArmyRegiments.Single(r => r.ID == donor.Unit.SourceArmyRegimentId).Regiments.Single(r =>
                r.ID == donor.Unit.SourceRegimentId && (r.Index ?? "0") == (donor.Unit.SourceChunkIndex ?? "0"));
            nativeSoldiers = int.Parse(source.CurrentNum, CultureInfo.InvariantCulture);
        }
        else nativeSoldiers = army.ArmyRegiments.Where(r => r.Type == RegimentType.Levy).SelectMany(r => r.Regiments)
            .Where(r => r.Culture?.ID == donor.Unit.GetObjCulture()?.ID).Sum(r => int.Parse(r.CurrentNum, CultureInfo.InvariantCulture));
        if (nativeSoldiers <= 0 || cohortBodies <= 0) throw Failure("Commander guard donor has no positive native cohort budget.");
        double ordinaryDamage = borrowed * (Math.Round(donor.Stats.MeleeDamage * donor.Profile.DamageMultiplier, MidpointRounding.AwayFromZero) +
            Math.Round(donor.Stats.ArmourPiercingDamage * donor.Profile.DamageMultiplier, MidpointRounding.AwayFromZero));
        double ordinaryHp = borrowed * Math.Round(donor.Stats.EffectiveHitPoints!.Value * donor.Profile.HitPointMultiplier, MidpointRounding.AwayFromZero);
        var knightPlan = personal == null ? null : PlanKnightBudget(army, donor.Profile.BaseUnitKey, new[] {personal});
        double totalDamage = ordinaryDamage + (knightPlan?.TargetAttilaDamage ?? 0), totalHp = ordinaryHp + (knightPlan?.TargetAttilaHitPoints ?? 0);
        int guardBodies = Math.Max(2, borrowed);
        if (knightPlan != null)
        {
            double damageLimit = Math.Max(donor.Profile.DamageMultiplier, Math.Min(Settings.MaximumMultiplier, Settings.KnightMaximumMultiplier));
            double hpLimit = Math.Max(donor.Profile.HitPointMultiplier, Math.Min(Settings.MaximumMultiplier, Settings.KnightMaximumMultiplier));
            guardBodies = checked((int)Math.Max(guardBodies, Math.Max(Math.Ceiling(totalDamage / (donor.Stats.TotalMeleeDamage * damageLimit)),
                Math.Ceiling(totalHp / (donor.Stats.EffectiveHitPoints.Value * hpLimit)))));
            if (guardBodies > Settings.KnightMaximumBodies) throw Failure("Commander personal budget exceeds the configured safe representation size; no budget was discarded.");
        }
        double damageMultiplier = knightPlan == null && guardBodies == borrowed ? donor.Profile.DamageMultiplier : totalDamage / (guardBodies * donor.Stats.TotalMeleeDamage);
        double hpMultiplier = knightPlan == null && guardBodies == borrowed ? donor.Profile.HitPointMultiplier : totalHp / (guardBodies * donor.Stats.EffectiveHitPoints.Value);
        string generated = Register(army.ID + ":commander-funded:" + (donor.Unit.SourceIdentity ?? donor.Unit.GetObjCulture()?.ID) + ":" + donor.Profile.BaseUnitKey,
            donor.Profile.BaseUnitKey, damageMultiplier, hpMultiplier);
        guard.CommanderGuardRepresentation = new(army.ID, donor.Unit.GetRegimentType(), donor.Unit.GetName(), donor.Unit.GetObjCulture()!.ID,
            donor.Profile.BaseUnitKey, donor.Unit.SourceArmyRegimentId, donor.Unit.SourceRegimentId, donor.Unit.SourceChunkIndex,
            nativeSoldiers, cohortBodies, borrowed, guardBodies, Array.AsReadOnly(cohort.Select(u => u.UniqueID).ToArray()),
            personal?.CharacterId, totalDamage > 0 ? (knightPlan?.TargetAttilaDamage ?? 0) / totalDamage : 0);
        guard.SetUnitKey(donor.Profile.BaseUnitKey); guard.SetInitialSoldiers(guardBodies);
        donor.Unit.SetInitialSoldiers(originalBodies - borrowed);
        generalXml.SetAttributeValue("num_soldiers", guardBodies); generalXml.Element("unit_type")!.SetAttributeValue("type", generated);
        if (donor.Unit.GetSoldiers() == 0) { emitted.Remove(donor.Xml); army.Units.Remove(donor.Unit); }
        else donor.Xml.SetAttributeValue("num_soldiers", donor.Unit.GetSoldiers());
        guardDonations.Add(army.ID, new(army, donor.Unit, originalBodies, guard));
        Note("Commander guard in army " + army.ID + " detaches " + borrowed + " actual " + donor.Profile.BaseUnitKey +
            " bodies from its own " + donor.Unit.GetRegimentType() + " cohort; native ordinary budget is conserved. Personal prowess contributes " +
            (personal == null ? "no extra combat budget (commander is not a native fighting knight)." : "one native knight budget for " + personal.CharacterId + ", excluded from the pooled knights."));
    }

    public static IReadOnlyList<CommanderGuardCasualtyProjection> GetCommanderGuardCasualties(Army army)
    {
        if (!HasBoundExport) return Array.Empty<CommanderGuardCasualtyProjection>();
        var results = new List<CommanderGuardCasualtyProjection>();
        foreach (var guard in army.Units.Where(u => u.CommanderGuardRepresentation != null))
        {
            var binding = guard.CommanderGuardRepresentation!;
            if (binding.ArmyId != army.ID || guard.GetSoldiers() != binding.DeployedBodies)
                throw Failure("Commander guard metadata differs from its deployed army/count.");
            var main = new List<(int Weight, int Bodies, int Remaining)>();
            var final = new List<(int Weight, int Bodies, int Remaining)>();
            foreach (var unit in army.Units.Where(u => ReferenceEquals(u, guard) || binding.CohortUnitIds.Contains(u.UniqueID)))
            {
                var records = army.CasualitiesReports.Where(r => r.Script.Contains("_" + unit.UniqueID + "_CULTURE", StringComparison.Ordinal)).ToArray();
                if (records.Length != 1 || records[0].GetStarting() != unit.GetSoldiers())
                    throw Failure("Commander guard donor lacks exact raw casualty evidence for unit " + unit.UniqueID);
                var report = records[0]; int weight = ReferenceEquals(unit, guard) ? binding.BorrowedBodies : unit.GetSoldiers();
                int after = report.GetAliveAfterPursuit() < 0 ? report.GetAliveBeforePursuit() : report.GetAliveAfterPursuit();
                main.Add((weight, unit.GetSoldiers(), report.GetAliveBeforePursuit()));
                final.Add((weight, unit.GetSoldiers(), after));
            }
            int before = CommanderGuardAccounting.Survivors(binding.NativeSoldiers, binding.CohortDeployedBodies, main);
            int afterAll = CommanderGuardAccounting.Survivors(binding.NativeSoldiers, binding.CohortDeployedBodies, final);
            if (afterAll > before) throw Failure("Commander donor survivors increased during pursuit.");
            results.Add(new(binding, before, afterAll));
        }
        return results.AsReadOnly();
    }
}
