using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CrusaderWars.StatFidelity;

public sealed record AttilaUnitCombatStats(
    string UnitKey, string LandUnitKey, string Category, int NominalSoldiers,
    int MeleeDamage, int ArmourPiercingDamage, int EntityHitPoints, int BonusHitPoints,
    bool IsMounted, bool HasSpecialEntity, bool HasMissileWeapon, bool IsNaval,
    int MeleeAttack, int MeleeDefence, int ChargeBonus, int? MountHitPoints = null)
{
    public int TotalMeleeDamage => checked(MeleeDamage + ArmourPiercingDamage);
    public bool CanScaleHitPoints => !IsNaval && !HasSpecialEntity && (!IsMounted || MountHitPoints.HasValue);
    public int? EffectiveInfantryHitPoints => CanScaleHitPoints && !IsMounted ? checked(EntityHitPoints + BonusHitPoints) : null;
    // Native card AND combat trials establish man+bonus+mount for ordinary cavalry.
    public int? EffectiveHitPoints => CanScaleHitPoints ? checked(EntityHitPoints + BonusHitPoints + (MountHitPoints ?? 0)) : null;
}

/// <summary>A single active-pack snapshot shared by planning queries and later battle generation.</summary>
public sealed partial class PreparedAttilaDatabase
{
    private readonly AttilaDatabase database;
    private readonly Action assertCurrent;
    public IReadOnlyList<string> SourcePacks { get; }

    internal PreparedAttilaDatabase(AttilaDatabase database, IReadOnlyList<string> sourcePacks, Action assertCurrent)
    {
        this.database = database;
        this.assertCurrent = assertCurrent;
        SourcePacks = Array.AsReadOnly(sourcePacks.ToArray());
    }

    public AttilaSiegeUnitStats QuerySiegeUnit(string unitKey) => AttilaSiegeUnitStats.Read(database, unitKey);

    public AttilaUnitCombatStats Query(string unitKey)
    {
        var main = database.Require("main_units_tables", unitKey);
        if (main.GetValueOrDefault("is_naval") == "true")
            return new(unitKey, main.GetValueOrDefault("land_unit") ?? "", "naval", Number(main, "num_men"),
                0, 0, 0, 0, false, true, false, true, 0, 0, 0);
        var land = database.Require("land_units_tables", main["land_unit"]);
        var entity = database.Require("battle_entities_tables", land["man_entity"]);
        var melee = database.Require("melee_weapons_tables", land["primary_melee_weapon"]);
        bool mounted = !string.IsNullOrWhiteSpace(land.GetValueOrDefault("mount"));
        bool special = new[] { "engine", "animal", "chariot", "articulated_record" }
            .Any(field => !string.IsNullOrWhiteSpace(land.GetValueOrDefault(field)));
        if (mounted && (land.GetValueOrDefault("category") != "cavalry" || OptionalNumber(land, "num_mounts") != Number(main, "num_men")))
            special = true;
        int? mountHp = null;
        if (mounted && !special)
        {
            var mount = database.Require("mounts_tables", land["mount"]);
            mountHp = Number(database.Require("battle_entities_tables", mount["entity"]), "hit_points");
        }
        return new(unitKey, main["land_unit"], land.GetValueOrDefault("category") ?? "", Number(main, "num_men"),
            Number(melee, "damage"), Number(melee, "ap_damage"), Number(entity, "hit_points"), Number(land, "bonus_hit_points"),
            mounted, special, !string.IsNullOrWhiteSpace(land.GetValueOrDefault("primary_missile_weapon")), false,
            OptionalNumber(land, "melee_attack"), OptionalNumber(land, "melee_defence"), OptionalNumber(land, "charge_bonus"), mountHp);
    }

    public IReadOnlyDictionary<string, string> Generate(IReadOnlyCollection<UnitStatProfile> profiles, string battleRoot)
    {
        AssertFresh();
        // Per-battle name overlays must not leak into another generation using this snapshot.
        var battleDatabase = new AttilaDatabase();
        foreach (var table in database.Tables.Values) battleDatabase.Merge(table);
        if (Directory.Exists(battleRoot))
            foreach (string path in Directory.GetFiles(battleRoot, "*.loc.tsv", SearchOption.AllDirectories)
                         .Where(p => !Path.GetFileName(p).Equals("cc_stat_fidelity.loc.tsv", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
                battleDatabase.Merge(TsvTable.Read(path));
        return StatFidelityBackend.GenerateFromDatabase(battleDatabase, profiles, battleRoot);
    }

    public void AssertFresh() => assertCurrent();

    private static int Number(Dictionary<string, string> row, string field) =>
        int.Parse(row[field], CultureInfo.InvariantCulture);
    private static int OptionalNumber(Dictionary<string, string> row, string field) =>
        row.TryGetValue(field, out string? value) && value.Length > 0 ? int.Parse(value, CultureInfo.InvariantCulture) : 0;
}
