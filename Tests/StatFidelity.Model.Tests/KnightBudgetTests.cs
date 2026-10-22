using CrusaderWars.StatFidelity;
using Xunit;

public sealed class KnightBudgetTests
{
    private static KnightStats Knight(string id, double prowess, double efficiency = 2.35) => new()
        { CharacterId = id, Prowess = prowess, Effectiveness = efficiency, DamagePerProwess = 50, ToughnessPerProwess = 10 };
    private static readonly KnightBudgetAnchor Huscarl = new("18:55:17620", "cha_vik_warlords_companions", 60, 39.96, 75, 154);
    private static readonly KnightBudgetTemplate Horse = new("cha_gen_knights", 6, 4, 100, false);

    [Fact]
    public void FractionalActiveParticipationWeightsPowerWithoutChangingCharacterIdentity()
    {
        var whole = Knight("1", 20); var partial = Knight("1", 20); partial.ActiveFraction = 0.5;
        var full = KnightBudgetPlanner.Plan(new[] { whole }, Huscarl, Horse, 1, new());
        var half = KnightBudgetPlanner.Plan(new[] { partial }, Huscarl, Horse, 1, new());
        Assert.Equal(full.Ck3Damage / 2, half.Ck3Damage);
        Assert.Equal(full.Ck3Toughness / 2, half.Ck3Toughness);
        Assert.Equal("1", Assert.Single(half.Characters).CharacterId);
        Assert.Equal(20, partial.Prowess);
        partial.ActiveFraction = 0;
        Assert.Equal(0, KnightBudgetPlanner.Plan(new[] { partial }, Huscarl, Horse, 1, new()).Bodies);
        partial.ActiveFraction = 1.01;
        Assert.Throws<InvalidOperationException>(() => KnightBudgetPlanner.Plan(new[] { partial }, Huscarl, Horse, 1, new()));
    }

    [Fact]
    public void SnapshotSixBudgetsAreIndependentAndUseMinimumFeasibleBodyCount()
    {
        var plan = KnightBudgetPlanner.Plan(new[] { Knight("34252", 18), Knight("30961", 8), Knight("37088", 11), Knight("33709", 16), Knight("37572", 11) }, Huscarl, Horse, 1, new());
        Assert.Equal(7520, plan.Ck3Damage, 8);
        Assert.Equal(1504, plan.Ck3Toughness, 8);
        Assert.Equal(9400, plan.TargetAttilaDamage, 8);
        Assert.Equal(1504 * 154 / 39.96, plan.TargetAttilaHitPoints, 8);
        Assert.Equal(38, plan.Bodies);
        Assert.Equal(9386, plan.RoundedWeaponDamageSum);
        Assert.Equal(5814, plan.RoundedHitPointSum);
        Assert.Equal(plan.TargetAttilaDamage, plan.Bodies * Horse.MeleeDamage * plan.DamageMultiplier, 8);
        Assert.Equal(plan.TargetAttilaHitPoints, plan.Bodies * Horse.HitPoints * plan.HitPointMultiplier, 8);
        Assert.Equal(plan.Bodies, plan.Characters.Sum(c => c.Bodies));
        Assert.True(plan.TargetAttilaDamage / (37 * Horse.MeleeDamage) > 25);
        Assert.Throws<InvalidOperationException>(plan.RequireVerifiedHitPoints);
    }

    [Fact]
    public void CommanderMultipliesOnlyDamageExactlyOnce()
    {
        var unit = Horse with { HitPointScalingVerified = true };
        var baseline = KnightBudgetPlanner.Plan(new[] { Knight("1", 20) }, Huscarl, unit, 1, new());
        var commanded = KnightBudgetPlanner.Plan(new[] { Knight("1", 20) }, Huscarl, unit, 1.3, new());
        Assert.Equal(baseline.TargetAttilaDamage * 1.3, commanded.TargetAttilaDamage, 8);
        Assert.Equal(baseline.TargetAttilaHitPoints, commanded.TargetAttilaHitPoints, 8);
        commanded.RequireVerifiedHitPoints();
    }

    [Fact]
    public void FramedStationFixtureUsesVerifiedCompositeMountedHealth()
    {
        var anchor = new KnightBudgetAnchor("18:55:17620:", "cha_vik_warlords_companions", 78, 50.76, 98, 195);
        var mounted = new KnightBudgetTemplate("cha_gen_knights", 6, 4, 150, true);
        var plan = KnightBudgetPlanner.Plan(new[] { Knight("37572", 17), Knight("33709", 16), Knight("37088", 11), Knight("30961", 10), Knight("34252", 18) }, anchor, mounted, 1, new());
        Assert.Equal(8460, plan.Ck3Damage);
        Assert.Equal(1692, plan.Ck3Toughness);
        Assert.Equal(43, plan.Bodies);
        Assert.Equal(6500, plan.TargetAttilaHitPoints, 8);
        Assert.Equal(1.0077519379844961, plan.HitPointMultiplier, 10);
        Assert.Equal(10621, plan.RoundedWeaponDamageSum);
        Assert.Equal(6493, plan.RoundedHitPointSum);
        plan.RequireVerifiedHitPoints();
    }

    [Fact]
    public void IndependentResponseFactorsDoNotReapplyEfficiency()
    {
        var limits = new KnightBudgetLimits { DamageResponseFactor = 1.2, ToughnessResponseFactor = 0.8 };
        var plan = KnightBudgetPlanner.Plan(new[] { Knight("1", 10, 2) }, Huscarl, Horse, 1, limits);
        Assert.Equal(1000 * 75 / 60.0 * 1.2, plan.TargetAttilaDamage, 8);
        Assert.Equal(200 * 154 / 39.96 * 0.8, plan.TargetAttilaHitPoints, 8);
    }

    [Fact]
    public void InfeasibleBoundsFailInsteadOfClampingOrCreatingBudget()
    {
        Assert.Throws<InvalidOperationException>(() => KnightBudgetPlanner.Plan(new[] { Knight("1", 100) }, Huscarl, Horse, 1,
            new KnightBudgetLimits { MaximumBodies = 2, MaximumMultiplier = 1 }));
        Assert.Throws<InvalidOperationException>(() => KnightBudgetPlanner.Plan(new[] { Knight("1", 10), Knight("1", 20) }, Huscarl, Horse, 1, new()));
    }

    [Fact]
    public void ZeroBudgetHasNoCombatRepresentatives()
    {
        var plan = KnightBudgetPlanner.Plan(new[] { Knight("1", 0) }, Huscarl, Horse, 1, new());
        Assert.Equal(0, plan.Bodies);
        Assert.Equal(0, Assert.Single(plan.Characters).Bodies);
        plan.RequireVerifiedHitPoints();
    }

    [Fact]
    public void BattleScaleReducesBothBudgetsWithoutChangingCharacterIdentity()
    {
        var characters = new[] { Knight("1", 18), Knight("2", 8), Knight("3", 11), Knight("4", 16), Knight("5", 11) };
        var full = KnightBudgetPlanner.Plan(characters, Huscarl, Horse, 1, new());
        var half = KnightBudgetPlanner.Plan(characters, Huscarl, Horse, 1, new(), representationScale: 0.5);
        Assert.Equal(19, half.Bodies);
        Assert.Equal(full.TargetAttilaDamage / 2, half.TargetAttilaDamage, 8);
        Assert.Equal(full.TargetAttilaHitPoints / 2, half.TargetAttilaHitPoints, 8);
        Assert.Equal(full.DamageMultiplier, half.DamageMultiplier, 8);
        Assert.Equal(full.HitPointMultiplier, half.HitPointMultiplier, 8);
        Assert.Equal(5, half.Characters.Count);
    }

    [Fact]
    public void AnchorRegimentUpgradeDoesNotBecomeAnExtraKnightBonus()
    {
        var baseAnchor = new KnightBudgetAnchor("source", "foot_anchor", 40, 20, 50, 100);
        var upgradedAnchor = baseAnchor with { Ck3Damage = 80, Ck3Toughness = 40, AttilaDamageBeforeCommander = 100, AttilaHitPoints = 200 };
        var roster = new[] { Knight("1", 20) };
        var baseline = KnightBudgetPlanner.Plan(roster, baseAnchor, Horse, 1, new());
        var upgraded = KnightBudgetPlanner.Plan(roster, upgradedAnchor, Horse, 1, new());
        Assert.Equal(baseline.TargetAttilaDamage, upgraded.TargetAttilaDamage);
        Assert.Equal(baseline.TargetAttilaHitPoints, upgraded.TargetAttilaHitPoints);
        Assert.Equal(baseline.Bodies, upgraded.Bodies);
    }
}
