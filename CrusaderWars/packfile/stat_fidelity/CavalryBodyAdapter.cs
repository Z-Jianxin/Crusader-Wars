using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CrusaderWars.StatFidelity;

/// <summary>An offline-reviewed one-rider graph; native engine confirmation remains pending.</summary>
public sealed record CavalryBodyAdapter(string Id, string Role, int SourceMen, int SourceMounts,
    int PlannedMen, int PlannedMounts, string GraphFingerprint,
    string VariantFingerprint = "", string RiderAnimationFingerprint = "", string MountAnimationFingerprint = "")
{
    public bool EngineValidated => false;
    public bool AssetGraphVerified => VariantFingerprint.Length > 0 && RiderAnimationFingerprint.Length > 0 && MountAnimationFingerprint.Length > 0;
    public bool ChangesCounts => SourceMounts != PlannedMounts;
    public string Evidence => "Earl 1212 active graph: rider/mount animations, horse variant, linked entities; private one-rider count normalization. Engine confirmation pending.";
}

internal static partial class CavalryBodyAdapters
{
    internal const string Version = "earl-single-rider-v1";
    private static readonly Dictionary<string, string[]> reviewed = Catalog.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim().Split('|')).ToDictionary(parts => parts[0], StringComparer.Ordinal);

    internal static CavalryBodyAdapter? Resolve(AttilaDatabase db, Dictionary<string, string> main, Dictionary<string, string> land)
    {
        if (!reviewed.TryGetValue(main["unit"], out var entry)) return null;
        if (main.GetValueOrDefault("is_naval") == "true" || land.GetValueOrDefault("category") != "cavalry" ||
            new[] { "engine", "animal", "chariot", "articulated_record" }.Any(f => !string.IsNullOrEmpty(land.GetValueOrDefault(f)))) return null;
        int men = Number(main, "num_men"), mounts = Number(land, "num_mounts");
        if (men <= 0 || mounts <= 0 || mounts > men) return null;
        var mount = db.Lookup("mounts_tables", land.GetValueOrDefault("mount") ?? "");
        var rider = db.Lookup("battle_entities_tables", land.GetValueOrDefault("man_entity") ?? "");
        var horse = db.Lookup("battle_entities_tables", mount?.GetValueOrDefault("entity") ?? "");
        if (mount == null || rider == null || horse == null || Number(rider, "hit_points") <= 0 || Number(horse, "hit_points") <= 0 ||
            !db.Find("unit_variants_tables", "unit", main["land_unit"]).Any()) return null;
        if (db.CavalryAssets == null || !db.CavalryAssets.Matches(mount.GetValueOrDefault("variant", ""), entry[3],
            land.GetValueOrDefault("man_animation", ""), entry[4], mount.GetValueOrDefault("animation", ""), entry[5])) return null;
        string fingerprint = Fingerprint(db, main, land);
        return fingerprint == entry[1] ? new(Version, entry[2], men, mounts, men, men, fingerprint, entry[3], entry[4], entry[5]) : null;
    }

    // Bind representation links and visual identity, not numeric combat values: active-pack
    // damage, HP, armour values, movement and mass are read afresh and preserved/scaled normally.
    internal static string Fingerprint(AttilaDatabase db, Dictionary<string, string> main, Dictionary<string, string> land)
    {
        var mount = db.Require("mounts_tables", land["mount"]);
        var rider = db.Require("battle_entities_tables", land["man_entity"]);
        var horse = db.Require("battle_entities_tables", mount["entity"]);
        var parts = new List<string> { Version };
        void Add(string table, Dictionary<string,string> row, params string[] fields)
        { foreach (string field in fields) parts.Add(table + ":" + field + "=" + row.GetValueOrDefault(field, "")); }
        Add("main", main, "unit", "land_unit", "is_naval");
        Add("land", land, "category", "class", "man_animation", "man_entity", "mount", "engine", "animal", "chariot", "articulated_record", "armour", "attribute_group", "primary_missile_weapon");
        Add("mount", mount, "key", "animation", "entity", "variant");
        Add("rider", rider, "key", "type"); Add("horse", horse, "key", "type", "shape");
        if (!string.IsNullOrEmpty(land.GetValueOrDefault("primary_missile_weapon")))
        {
            var missile = db.Require("missile_weapons_tables", land["primary_missile_weapon"]);
            Add("missile", missile, "key", "precursor", "default_projectile");
            Add("projectile", db.Require("projectiles_tables", missile["default_projectile"]), "key", "category", "shot_type");
        }
        foreach (var variant in db.Find("unit_variants_tables", "unit", main["land_unit"]).OrderBy(r => r.GetValueOrDefault("faction"), StringComparer.Ordinal))
            Add("visual", variant, "unit", "faction", "variant");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
    }
    private static int Number(Dictionary<string,string> row, string field) =>
        int.TryParse(row.GetValueOrDefault(field), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
}
