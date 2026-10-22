using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrusaderWars.StatFidelity;

internal static class InspectDatabase
{
    public static void Write(string schema, string game, string cacheRoot, string profileFile, string output)
    {
        string schemaHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schema)));
        var packs = PackSourceResolver.Resolve(Path.Combine(game, "Attila.exe"));
        var directories = new List<string>();
        foreach (string pack in packs)
        {
            var info = new FileInfo(pack);
            string signature = $"{StatFidelityBackend.CacheFormatVersion}\n{info.FullName}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n{schemaHash}";
            string bucket = Path.Combine(cacheRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))));
            string marker = Path.Combine(bucket, "complete");
            if (!File.Exists(marker)) throw new InvalidOperationException("Read-only inspection needs a completed cache for " + pack);
            string attempt = File.ReadAllText(marker);
            if (!System.Text.RegularExpressions.Regex.IsMatch(attempt, "^attempt-[a-f0-9]{32}$"))
                throw new InvalidDataException("Invalid completed-cache marker.");
            directories.Add(Path.Combine(bucket, attempt));
        }
        var db = AttilaDatabase.FromPackDirectories(directories);
        var prepared = new PreparedAttilaDatabase(db, packs, () => { });
        var profiles = JsonSerializer.Deserialize<UnitStatProfile[]>(File.ReadAllText(profileFile))!;
        var entries = new List<object>();
        foreach (string key in profiles.Select(p => p.BaseUnitKey).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            var main = db.Require("main_units_tables", key);
            var land = db.Require("land_units_tables", main["land_unit"]);
            var man = db.Require("battle_entities_tables", land["man_entity"]);
            var melee = db.Require("melee_weapons_tables", land["primary_melee_weapon"]);
            Dictionary<string, string>? missile = null, projectile = null;
            if (!string.IsNullOrEmpty(land.GetValueOrDefault("primary_missile_weapon")))
            {
                missile = db.Require("missile_weapons_tables", land["primary_missile_weapon"]);
                projectile = db.Require("projectiles_tables", missile["default_projectile"]);
            }
            entries.Add(new
            {
                key, main, land, man, melee, missile, projectile,
                combatStats = prepared.Query(key),
                riderOrInfantryHp = int.Parse(man["hit_points"], CultureInfo.InvariantCulture) + int.Parse(land["bonus_hit_points"], CultureInfo.InvariantCulture),
                meleeNormalPlusAp = int.Parse(melee["damage"], CultureInfo.InvariantCulture) + int.Parse(melee["ap_damage"], CultureInfo.InvariantCulture),
                mountedHpSemanticsUnverified = prepared.Query(key).IsMounted && !prepared.Query(key).CanScaleHitPoints,
                profiles = profiles.Where(p => p.BaseUnitKey == key)
            });
        }
        File.WriteAllText(Path.Combine(output, "active-base-stats.json"), JsonSerializer.Serialize(new
        {
            profileFile, packsLowToHigh = packs, readOnlyCache = cacheRoot, entries,
            limitation = "Base DB records only, before XP/campaign/battle effects. Infantry entity+bonus HP is not asserted to be mounted total health. No combat-equivalence claim."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Inspected {entries.Count} distinct mapped units from {packs.Count} active packs; source cache unchanged.");
    }
}
