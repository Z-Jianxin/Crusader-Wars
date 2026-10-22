using System;
using System.Collections.Generic;
using System.Linq;

namespace CrusaderWars.StatFidelity
{
    /// <summary>Nominal weapon/health units, not measured damage per second or effective durability.</summary>
    public sealed record KnightBudgetAnchor(string SourceIdentity, string UnitKey,
        double Ck3Damage, double Ck3Toughness, double AttilaDamageBeforeCommander, double AttilaHitPoints);

    public sealed record KnightBudgetTemplate(string UnitKey, double NormalMeleeDamage, double ArmourPiercingDamage, double HitPoints,
        bool HitPointScalingVerified)
    {
        public double MeleeDamage => NormalMeleeDamage + ArmourPiercingDamage;
    }

    public sealed class KnightBudgetLimits
    {
        public double MinimumMultiplier { get; set; } = 0.01;
        public double MaximumMultiplier { get; set; } = 25;
        public int MinimumBodies { get; set; } = 2;
        public int MaximumBodies { get; set; } = 10000;
        // Response factors are independent. A value of 1 is nominal-stat matching,
        // not a claim that different animations/armour/charge produce equal DPS/TTK.
        public double DamageResponseFactor { get; set; } = 1;
        public double ToughnessResponseFactor { get; set; } = 1;
        public string CalibrationEvidence { get; set; } = "nominal-stat anchor only; combat response not fitted";
    }

    public sealed record KnightBodyAllocation(string CharacterId, int Bodies, double Ck3Damage, double Ck3Toughness);

    public sealed record KnightBudgetPlan(KnightBudgetAnchor Anchor, string TemplateKey, int Bodies,
        double Ck3Damage, double Ck3Toughness, double TargetAttilaDamage, double TargetAttilaHitPoints,
        double DamageMultiplier, double HitPointMultiplier, double RoundedWeaponDamageSum, double RoundedHitPointSum, double CommanderMultiplier,
        double RepresentationScale, IReadOnlyList<KnightBodyAllocation> Characters, bool HitPointScalingVerified, string CalibrationEvidence)
    {
        public void RequireVerifiedHitPoints()
        {
            if (!HitPointScalingVerified && Bodies > 0)
                throw new InvalidOperationException("Knight budget for '" + TemplateKey + "' requires HP factor " + HitPointMultiplier +
                    ", but its mounted/special entity health conversion has not been verified. Use a verified template or finish its entity calibration.");
        }
    }

    public static class KnightBudgetPlanner
    {
        public static KnightBudgetPlan Plan(IReadOnlyList<KnightStats> knights, KnightBudgetAnchor anchor,
            KnightBudgetTemplate template, double commanderMultiplier, KnightBudgetLimits limits, double representationScale = 1)
        {
            Positive(anchor.Ck3Damage, nameof(anchor.Ck3Damage)); Positive(anchor.Ck3Toughness, nameof(anchor.Ck3Toughness));
            Positive(anchor.AttilaDamageBeforeCommander, nameof(anchor.AttilaDamageBeforeCommander)); Positive(anchor.AttilaHitPoints, nameof(anchor.AttilaHitPoints));
            Positive(template.MeleeDamage, nameof(template.MeleeDamage)); Positive(template.HitPoints, nameof(template.HitPoints));
            Nonnegative(template.NormalMeleeDamage, nameof(template.NormalMeleeDamage)); Nonnegative(template.ArmourPiercingDamage, nameof(template.ArmourPiercingDamage));
            Positive(commanderMultiplier, nameof(commanderMultiplier));
            Positive(representationScale, nameof(representationScale));
            Positive(limits.MinimumMultiplier, nameof(limits.MinimumMultiplier)); Positive(limits.MaximumMultiplier, nameof(limits.MaximumMultiplier));
            Positive(limits.DamageResponseFactor, nameof(limits.DamageResponseFactor)); Positive(limits.ToughnessResponseFactor, nameof(limits.ToughnessResponseFactor));
            if (limits.MaximumMultiplier < limits.MinimumMultiplier || limits.MinimumBodies < 2 || limits.MaximumBodies < limits.MinimumBodies)
                throw new InvalidOperationException("Invalid knight budget bounds.");
            if (knights.Select(k => k.CharacterId).Distinct(StringComparer.Ordinal).Count() != knights.Count)
                throw new InvalidOperationException("A knight character cannot contribute twice to one budget.");
            foreach (var knight in knights)
            {
                Nonnegative(knight.Prowess, nameof(knight.Prowess)); Nonnegative(knight.Effectiveness, nameof(knight.Effectiveness));
                Positive(knight.DamagePerProwess, nameof(knight.DamagePerProwess)); Positive(knight.ToughnessPerProwess, nameof(knight.ToughnessPerProwess));
            }
            double damage = knights.Sum(k => k.Prowess * k.Effectiveness * k.DamagePerProwess);
            double toughness = knights.Sum(k => k.Prowess * k.Effectiveness * k.ToughnessPerProwess);
            Nonnegative(damage, nameof(damage)); Nonnegative(toughness, nameof(toughness));
            if (damage == 0 && toughness == 0)
                return new KnightBudgetPlan(anchor, template.UnitKey, 0, 0, 0, 0, 0, 1, 1, 0, 0, commanderMultiplier, representationScale,
                    knights.Select(k => new KnightBodyAllocation(k.CharacterId, 0, 0, 0)).ToArray(), template.HitPointScalingVerified, limits.CalibrationEvidence);

            double targetDamage = damage * anchor.AttilaDamageBeforeCommander / anchor.Ck3Damage * limits.DamageResponseFactor * commanderMultiplier * representationScale;
            double targetHitPoints = toughness * anchor.AttilaHitPoints / anchor.Ck3Toughness * limits.ToughnessResponseFactor * representationScale;
            Positive(targetDamage, nameof(targetDamage)); Positive(targetHitPoints, nameof(targetHitPoints));
            // Use the fewest bodies satisfying BOTH independent budgets and bounds.
            // No arbitrary cap tied to the anchor's per-soldier weapon damage is added.
            double lower = Math.Max(Math.Max(limits.MinimumBodies, knights.Count), Math.Max(
                Math.Ceiling(targetDamage / (template.MeleeDamage * limits.MaximumMultiplier)),
                Math.Ceiling(targetHitPoints / (template.HitPoints * limits.MaximumMultiplier))));
            double upper = Math.Min(limits.MaximumBodies, Math.Min(
                Math.Floor(targetDamage / (template.MeleeDamage * limits.MinimumMultiplier)),
                Math.Floor(targetHitPoints / (template.HitPoints * limits.MinimumMultiplier))));
            if (lower > upper || lower > int.MaxValue)
                throw new InvalidOperationException("No knight body count satisfies the independent damage/HP bounds. Budgets were not clamped or discarded.");
            int bodies = checked((int)lower);
            double damageMultiplier = targetDamage / (bodies * template.MeleeDamage);
            double hitPointMultiplier = targetHitPoints / (bodies * template.HitPoints);
            double roundedDamage = Math.Round(template.NormalMeleeDamage * damageMultiplier, MidpointRounding.AwayFromZero) +
                Math.Round(template.ArmourPiercingDamage * damageMultiplier, MidpointRounding.AwayFromZero);
            double roundedHitPoints = Math.Round(template.HitPoints * hitPointMultiplier, MidpointRounding.AwayFromZero);
            if (roundedDamage < 1 || roundedHitPoints < 1)
                throw new InvalidOperationException("The positive knight budget cannot be represented by integer weapon damage/HP at the chosen body floor.");
            var allocations = Allocate(knights, bodies, toughness);
            return new KnightBudgetPlan(anchor, template.UnitKey, bodies, damage, toughness, targetDamage, targetHitPoints,
                damageMultiplier, hitPointMultiplier, bodies * roundedDamage, bodies * roundedHitPoints, commanderMultiplier, representationScale, allocations, template.HitPointScalingVerified, limits.CalibrationEvidence);
        }

        private static IReadOnlyList<KnightBodyAllocation> Allocate(IReadOnlyList<KnightStats> knights, int bodies, double toughness)
        {
            // One representative per listed character, then largest remainders weighted
            // by native toughness. This is an abstract retinue, not extra CK3 characters.
            int remainderBodies = bodies - knights.Count;
            var allocations = knights.Select(k => new
            {
                knight = k,
                share = remainderBodies * (k.Prowess * k.Effectiveness * k.ToughnessPerProwess) / toughness
            }).ToArray();
            var counts = allocations.ToDictionary(a => a.knight.CharacterId, a => 1 + (int)Math.Floor(a.share), StringComparer.Ordinal);
            int remaining = bodies - counts.Values.Sum();
            foreach (var item in allocations.OrderByDescending(a => a.share - Math.Floor(a.share)).ThenBy(a => a.knight.CharacterId, StringComparer.Ordinal).Take(remaining))
                counts[item.knight.CharacterId]++;
            return knights.Select(k => new KnightBodyAllocation(k.CharacterId, counts[k.CharacterId],
                k.Prowess * k.Effectiveness * k.DamagePerProwess, k.Prowess * k.Effectiveness * k.ToughnessPerProwess)).ToArray();
        }

        private static void Positive(double value, string name)
        {
            if (!double.IsFinite(value) || value <= 0) throw new InvalidOperationException("Knight budget requires finite positive " + name + ".");
        }
        private static void Nonnegative(double value, string name)
        {
            if (!double.IsFinite(value) || value < 0) throw new InvalidOperationException("Knight budget requires finite nonnegative " + name + ".");
        }
    }
}
