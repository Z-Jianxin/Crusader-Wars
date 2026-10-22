using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CrusaderWars.StatFidelity;

/// <summary>Effective active-pack facts. Reference keys are not guessed numeric armour or balance ratings.</summary>
public sealed record AttilaUnitSemanticStats
{
    public string UnitKey { get; init; } = "";
    public string LandUnitKey { get; init; } = "";
    public string ScreenName { get; init; } = "";
    public bool IsNaval { get; init; }
    public string Category { get; init; } = "";
    public string UnitClass { get; init; } = "";
    public string Weight { get; init; } = "";
    public string Caste { get; init; } = "";
    public int NominalSoldiers { get; init; }
    public string ManEntityKey { get; init; } = "";
    public string ManEntityType { get; init; } = "";
    public int? EntityHitPoints { get; init; }
    public int BonusHitPoints { get; init; }
    public string MountKey { get; init; } = "";
    public string MountEntityKey { get; init; } = "";
    public string MountEntityType { get; init; } = "";
    public string MountVariant { get; init; } = "";
    public int MountCount { get; init; }
    public int? MountHitPoints { get; init; }
    public string EngineKey { get; init; } = "";
    public string EngineType { get; init; } = "";
    public int EngineCount { get; init; }
    public string AnimalKey { get; init; } = "";
    public string ChariotKey { get; init; } = "";
    public string ArticulatedKey { get; init; } = "";
    public string MeleeWeaponKey { get; init; } = "";
    public string MeleeWeaponType { get; init; } = "";
    public int? MeleeDamage { get; init; }
    public int? MeleeApDamage { get; init; }
    public string MissileWeaponKey { get; init; } = "";
    public bool? MissilePrecursor { get; init; }
    public string MissileAudioType { get; init; } = "";
    public string DefaultProjectileKey { get; init; } = "";
    public string ProjectileCategory { get; init; } = "";
    public string ProjectileShotType { get; init; } = "";
    public int? ProjectileDamage { get; init; }
    public int? ProjectileApDamage { get; init; }
    public int Ammunition { get; init; }
    public string ArmourKey { get; init; } = "";
    public string ShieldKey { get; init; } = "";
    public string AttributeGroup { get; init; } = "";
    public int MeleeAttack { get; init; }
    public int MeleeDefence { get; init; }
    public int ChargeBonus { get; init; }
    public bool HasVisualVariant { get; init; }
    public IReadOnlyList<string> MissingReferences { get; init; } = Array.Empty<string>();
    public bool IsMounted => MountKey.Length > 0;
    public bool HasMissileWeapon => MissileWeaponKey.Length > 0;
    public bool HasSpecialEntity => EngineKey.Length > 0 || AnimalKey.Length > 0 || ChariotKey.Length > 0 || ArticulatedKey.Length > 0;
    public bool HasOrdinaryMount => IsMounted && Category == "cavalry" && MountCount == NominalSoldiers && !HasSpecialEntity && MountHitPoints.HasValue;
    public bool CanScaleHitPoints => !IsNaval && !HasSpecialEntity && EntityHitPoints.HasValue && (!IsMounted || HasOrdinaryMount);
    public int? EffectiveHitPoints => CanScaleHitPoints ? checked(EntityHitPoints!.Value + BonusHitPoints + (MountHitPoints ?? 0)) : null;
}

public sealed partial class PreparedAttilaDatabase
{
    private HashSet<string>? semanticVisualUnits;
    public IReadOnlyList<string> UnitKeys => database.Tables.TryGetValue("main_units_tables", out var table)
        ? Array.AsReadOnly(table.Rows.Select(row => row["unit"]).OrderBy(key => key, StringComparer.Ordinal).ToArray())
        : Array.Empty<string>();

    public AttilaUnitSemanticStats QuerySemanticUnit(string unitKey)
    {
        var main = database.Require("main_units_tables", unitKey);
        string landKey = main.GetValueOrDefault("land_unit") ?? "";
        bool naval = main.GetValueOrDefault("is_naval") == "true";
        if (naval) return new() { UnitKey = unitKey, LandUnitKey = landKey, IsNaval = true,
            Category = "naval", NominalSoldiers = OptionalNumber(main, "num_men"), Weight = Value(main, "weight"), Caste = Value(main, "caste") };
        var land = database.Require("land_units_tables", landKey);
        var missing = new List<string>();
        Dictionary<string, string>? Reference(string table, string key)
        {
            if (key.Length == 0) return null;
            var found = database.Lookup(table, key);
            if (found == null) missing.Add(table + ":" + key);
            return found;
        }
        string manKey = Value(land, "man_entity"), mountKey = Value(land, "mount"), engineKey = Value(land, "engine");
        var man = Reference("battle_entities_tables", manKey);
        var mount = Reference("mounts_tables", mountKey);
        string mountEntityKey = Value(mount, "entity");
        var mountEntity = Reference("battle_entities_tables", mountEntityKey);
        var engine = Reference("battlefield_engines_tables", engineKey);
        string meleeKey = Value(land, "primary_melee_weapon");
        var melee = Reference("melee_weapons_tables", meleeKey);
        // Artillery has its missile weapon on the linked engine, not on land_units.
        string missileKey = Value(land, "primary_missile_weapon");
        if (missileKey.Length == 0) missileKey = Value(engine, "missile_weapon");
        var missile = Reference("missile_weapons_tables", missileKey);
        string projectileKey = Value(missile, "default_projectile");
        var projectile = Reference("projectiles_tables", projectileKey);
        semanticVisualUnits ??= database.Tables.TryGetValue("unit_variants_tables", out var visuals)
            ? visuals.Rows.Select(row => row["unit"]).ToHashSet(StringComparer.Ordinal) : new(StringComparer.Ordinal);
        return new() {
            UnitKey = unitKey, LandUnitKey = landKey, ScreenName = Value(database.Lookup("Loc", "land_units_onscreen_name_" + landKey), "text"),
            Category = Value(land, "category"), UnitClass = Value(land, "class"), Weight = Value(main, "weight"), Caste = Value(main, "caste"),
            NominalSoldiers = OptionalNumber(main, "num_men"), ManEntityKey = manKey, ManEntityType = Value(man, "type"),
            EntityHitPoints = NumberOrNull(man, "hit_points"), BonusHitPoints = OptionalNumber(land, "bonus_hit_points"),
            MountKey = mountKey, MountEntityKey = mountEntityKey, MountEntityType = Value(mountEntity, "type"), MountVariant = Value(mount, "variant"),
            MountCount = OptionalNumber(land, "num_mounts"), MountHitPoints = NumberOrNull(mountEntity, "hit_points"),
            EngineKey = engineKey, EngineType = Value(engine, "engine_type"), EngineCount = OptionalNumber(land, "num_guns"),
            AnimalKey = Value(land, "animal"), ChariotKey = Value(land, "chariot"), ArticulatedKey = Value(land, "articulated_record"),
            MeleeWeaponKey = meleeKey, MeleeWeaponType = Value(melee, "melee_weapon_type"), MeleeDamage = NumberOrNull(melee, "damage"), MeleeApDamage = NumberOrNull(melee, "ap_damage"),
            MissileWeaponKey = missileKey, MissilePrecursor = bool.TryParse(Value(missile, "precursor"), out bool precursor) ? precursor : null,
            MissileAudioType = Value(missile, "audio_type"), DefaultProjectileKey = projectileKey,
            ProjectileCategory = Value(projectile, "category"), ProjectileShotType = Value(projectile, "shot_type"),
            ProjectileDamage = NumberOrNull(projectile, "damage"), ProjectileApDamage = NumberOrNull(projectile, "ap_damage"),
            Ammunition = OptionalNumber(land, "ammo"), ArmourKey = Value(land, "armour"), ShieldKey = Value(land, "shield"), AttributeGroup = Value(land, "attribute_group"),
            MeleeAttack = OptionalNumber(land, "melee_attack"), MeleeDefence = OptionalNumber(land, "melee_defence"), ChargeBonus = OptionalNumber(land, "charge_bonus"),
            HasVisualVariant = semanticVisualUnits.Contains(landKey), MissingReferences = missing.AsReadOnly()
        };
    }

    private static string Value(Dictionary<string, string>? row, string field) => row?.GetValueOrDefault(field) ?? "";
    private static int? NumberOrNull(Dictionary<string, string>? row, string field) =>
        row != null && row.TryGetValue(field, out string? value) && value.Length > 0 ? int.Parse(value, CultureInfo.InvariantCulture) : null;
}
