using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CrusaderWars.StatFidelity;

/// <summary>Creates private copies; never changes a shared source weapon, projectile or entity.</summary>
public sealed class StatVariantBuilder
{
    private readonly AttilaDatabase source;
    private readonly Dictionary<string, TsvTable> output = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> completed = new(StringComparer.Ordinal);
    public IReadOnlyCollection<TsvTable> Tables => output.Values;

    public StatVariantBuilder(AttilaDatabase source) { this.source = source; }

    public string Add(UnitStatProfile profile)
    {
        string key = StatFidelityBackend.GeneratedKey(profile);
        if (completed.TryGetValue(profile.Id, out var previous))
        {
            if (previous != key) throw new InvalidOperationException($"Conflicting stat profiles for '{profile.Id}'.");
            return key;
        }
        var originalMain = source.Require("main_units_tables", profile.BaseUnitKey);
        if (originalMain.GetValueOrDefault("is_naval") == "true")
            throw new InvalidOperationException($"Stat fidelity cannot change naval unit '{profile.BaseUnitKey}'.");
        string oldLandKey = originalMain["land_unit"];
        var originalLand = source.Require("land_units_tables", oldLandKey);
        if (profile.SiegeEngineCount.HasValue)
        {
            var siege = AttilaSiegeUnitStats.Read(source, profile.BaseUnitKey);
            if (profile.SiegeEngineCount > siege.NominalEngines || profile.SiegeCrewCount !=
                checked(profile.SiegeEngineCount.Value * (siege.NominalCrew / siege.NominalEngines)))
                throw new InvalidOperationException("Siege battery counts must preserve the active template's crew/engine ratio without exceeding its native battery size.");
        }
        bool mounted = !string.IsNullOrWhiteSpace(originalLand.GetValueOrDefault("mount"));
        bool special = new[] { "engine", "animal", "chariot", "articulated_record" }
            .Any(field => !string.IsNullOrWhiteSpace(originalLand.GetValueOrDefault(field)));
        var cavalryAdapter = mounted ? CavalryBodyAdapters.Resolve(source, originalMain, originalLand) : null;
        bool ordinaryMount = mounted && !special && originalLand.GetValueOrDefault("category") == "cavalry" &&
            (cavalryAdapter != null || Integer(originalLand, "num_mounts") == Integer(originalMain, "num_men"));
        if (ordinaryMount && cavalryAdapter == null)
        {
            var mount = source.Require("mounts_tables", originalLand["mount"]);
            var mountEntity = source.Require("battle_entities_tables", mount["entity"]);
            if (mountEntity.TryGetValue("type", out var type) && type != "horse") ordinaryMount = false;
        }
        if ((special || (mounted && !ordinaryMount)) && profile.HitPointMultiplier != 1)
            throw new InvalidOperationException($"Stat fidelity cannot validate multi-rider/chariot/animal/engine toughness for '{profile.BaseUnitKey}' (profile '{profile.Id}'). Only infantry and ordinary one-rider cavalry HP have engine evidence.");
        var land = Clone("land_units_tables", originalLand, "key", key);
        var main = Clone("main_units_tables", originalMain, "unit", key);
        main["land_unit"] = key;
        // One private mount per nominal rider. Source rows, XML cohort counts and CK3
        // active/soft/reserve population accounting are untouched by this adapter.
        if (cavalryAdapter != null) land["num_mounts"] = cavalryAdapter.PlannedMounts.ToString(CultureInfo.InvariantCulture);
        if (profile.SiegeEngineCount.HasValue)
        {
            land["num_guns"] = profile.SiegeEngineCount.Value.ToString(CultureInfo.InvariantCulture);
            main["num_men"] = profile.SiegeCrewCount!.Value.ToString(CultureInfo.InvariantCulture);
        }
        // This identifier is separate from the unit key. Keep generated records out of the encyclopedia.
        if (main.ContainsKey("unique_index")) main["unique_index"] = (Convert.ToInt64(key[6..18], 16) + 1000000000).ToString(CultureInfo.InvariantCulture);
        if (main.ContainsKey("in_encyclopedia")) main["in_encyclopedia"] = "false";

        var entity = source.Require("battle_entities_tables", Required(land, "man_entity"));
        int baseHealth = checked(Integer(entity, "hit_points") + Integer(land, "bonus_hit_points"));
        if (baseHealth <= 0) throw new InvalidOperationException($"Invalid effective hit points for '{oldLandKey}'.");
        int health = Scale(baseHealth, profile.HitPointMultiplier);
        if (health < 1) throw new InvalidOperationException($"Stat fidelity profile '{profile.Id}' produces less than one Attila hit point after integer rounding; adjust the configured range or mapping.");
        // A private entity avoids negative bonus HP when the desired total is below the entity baseline.
        var newEntity = Clone("battle_entities_tables", entity, "key", key + "_man");
        land["man_entity"] = key + "_man";
        if (profile.HitPointMultiplier != 1)
        {
            if (ordinaryMount)
            {
                var originalMount = source.Require("mounts_tables", originalLand["mount"]);
                var originalMountEntity = source.Require("battle_entities_tables", Required(originalMount, "entity"));
                int mountedBase = checked(baseHealth + Integer(originalMountEntity, "hit_points"));
                int mountedHealth = Scale(mountedBase, profile.HitPointMultiplier);
                int manHealth = Scale(Integer(entity, "hit_points"), profile.HitPointMultiplier);
                int mountHealth = checked(mountedHealth - health);
                if (manHealth < 1 || mountHealth < 1)
                    throw new InvalidOperationException($"Mounted profile '{profile.Id}' is below entity HP resolution after rounding.");
                // Preserve proportional components while rounding BOTH composite mounted HP and
                // dismounted (man+bonus) HP exactly. Residuals differ by at most integer rounding.
                newEntity["hit_points"] = manHealth.ToString(CultureInfo.InvariantCulture);
                land["bonus_hit_points"] = checked(health - manHealth).ToString(CultureInfo.InvariantCulture);
                var mount = Clone("mounts_tables", originalMount, "key", key + "_mount");
                var mountEntity = Clone("battle_entities_tables", originalMountEntity, "key", key + "_mount_entity");
                mountEntity["hit_points"] = mountHealth.ToString(CultureInfo.InvariantCulture);
                mount["entity"] = key + "_mount_entity";
                land["mount"] = key + "_mount";
            }
            else
            {
                newEntity["hit_points"] = health.ToString(CultureInfo.InvariantCulture);
                land["bonus_hit_points"] = "0";
            }
        }
        // Neutral/damage-only profiles preserve all HP fields and their original mount reference.

        string meleeKey = Required(land, "primary_melee_weapon");
        var melee = Clone("melee_weapons_tables", source.Require("melee_weapons_tables", meleeKey), "key", key + "_melee");
        ScaleDamage(melee, profile.DamageMultiplier);
        land["primary_melee_weapon"] = key + "_melee";

        if (!string.IsNullOrEmpty(land.GetValueOrDefault("primary_missile_weapon")))
            land["primary_missile_weapon"] = CloneMissile(land["primary_missile_weapon"], key + "_missile", profile.DamageMultiplier);
        if (!string.IsNullOrEmpty(land.GetValueOrDefault("engine")))
        {
            var engine = Clone("battlefield_engines_tables", source.Require("battlefield_engines_tables", land["engine"]), "key", key + "_engine");
            if (!string.IsNullOrEmpty(engine.GetValueOrDefault("missile_weapon")))
                engine["missile_weapon"] = CloneMissile(engine["missile_weapon"], key + "_engine_missile", profile.DamageMultiplier);
            land["engine"] = key + "_engine";
        }

        // Attila's unit_variants.unit foreign key targets land_units.key, not main_units.unit.
        // Overhauls may use different recruitment and land keys even though vanilla often does not.
        var variants = source.Find("unit_variants_tables", "unit", oldLandKey).ToList();
        if (variants.Count == 0) throw new InvalidOperationException($"Stat fidelity cannot resolve visual variants for '{profile.BaseUnitKey}'.");
        foreach (var variant in variants)
            // Keep variant/name/card assets and their colour relationships; only the unit FK changes.
            Clone("unit_variants_tables", variant, "unit", key);
        foreach (var ability in source.Find("land_units_to_unit_abilites_junctions_tables", "land_unit", oldLandKey))
            Clone("land_units_to_unit_abilites_junctions_tables", ability, "land_unit", key);

        string locKey = "land_units_onscreen_name_" + oldLandKey;
        var name = source.Require("Loc", locKey);
        Clone("Loc", name, "key", "land_units_onscreen_name_" + key);
        completed.Add(profile.Id, key);
        return key;
    }

    private string CloneMissile(string oldKey, string newKey, double multiplier)
    {
        var missile = Clone("missile_weapons_tables", source.Require("missile_weapons_tables", oldKey), "key", newKey);
        string defaultProjectile = Required(missile, "default_projectile");
        var alternatives = source.Find("missile_weapons_to_projectiles_tables", "missile_weapon", oldKey).ToList();
        var projectileKeys = alternatives.Select(r => r["projectile"]).Append(defaultProjectile).Distinct(StringComparer.Ordinal).ToList();
        for (int i = 0; i < projectileKeys.Count; i++)
        {
            string oldProjectile = projectileKeys[i]; string newProjectile = newKey + "_p" + i.ToString(CultureInfo.InvariantCulture);
            var projectile = Clone("projectiles_tables", source.Require("projectiles_tables", oldProjectile), "key", newProjectile);
            ScaleDamage(projectile, multiplier);
            if (oldProjectile == defaultProjectile) missile["default_projectile"] = newProjectile;
            foreach (var alternative in alternatives.Where(r => r["projectile"] == oldProjectile))
            {
                var junction = Clone("missile_weapons_to_projectiles_tables", alternative, "missile_weapon", newKey);
                junction["projectile"] = newProjectile;
            }
        }
        return newKey;
    }

    private Dictionary<string, string> Clone(string tableName, Dictionary<string, string> row, string keyColumn, string key)
    {
        if (!output.TryGetValue(tableName, out var table))
        {
            var definition = source.Tables[tableName];
            output[tableName] = table = new TsvTable(tableName, definition.Version, definition.Columns);
        }
        var copy = new Dictionary<string, string>(row, StringComparer.Ordinal) { [keyColumn] = key };
        table.Rows.Add(copy); return copy;
    }

    private static void ScaleDamage(Dictionary<string, string> row, double multiplier)
    {
        foreach (string column in new[] { "damage", "ap_damage" })
            row[column] = Scale(Integer(row, column), multiplier).ToString(CultureInfo.InvariantCulture);
        // Preserve AP share, armour interaction, projectile trajectory, splash and special effects.
    }

    private static int Scale(int value, double multiplier)
    {
        if (value < 0) throw new InvalidOperationException("Negative base combat stat in active Attila database.");
        if (value == 0) return 0;
        return checked((int)Math.Round(value * multiplier, MidpointRounding.AwayFromZero));
    }
    private static int Integer(Dictionary<string, string> row, string column) => int.Parse(Required(row, column), CultureInfo.InvariantCulture);
    private static string Required(Dictionary<string, string> row, string column) => row.TryGetValue(column, out string? value) && value.Length > 0
        ? value : throw new InvalidOperationException($"Attila schema is missing required field '{column}'.");
}
