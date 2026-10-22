using System.Security.Cryptography;
using System.Text;
using CrusaderWars.StatFidelity;
using Xunit;

public sealed class SemanticQueryTests
{
    [Fact]
    public void SemanticQueryUsesActualPrecursorAndRoleFieldsRatherThanKeyNames()
    {
        var prepared = new PreparedAttilaDatabase(Fixture(), [], () => { });
        var foot = prepared.QuerySemanticUnit("foot");
        Assert.Equal("inf_melee", foot.Category); Assert.Equal("inf_spr", foot.UnitClass);
        Assert.Equal("very_heavy", foot.Weight); Assert.Equal("mail", foot.ArmourKey);
        Assert.Equal("misleading_bow_name", foot.MissileWeaponKey); Assert.True(foot.MissilePrecursor);
        Assert.Equal("javelin", foot.MissileAudioType); Assert.Equal("piercing", foot.ProjectileShotType);
        Assert.Equal(20, foot.ProjectileDamage); Assert.Equal(7, foot.ProjectileApDamage);
        Assert.Equal(107, foot.EffectiveHitPoints); Assert.True(foot.HasVisualVariant);
        Assert.Equal("Foot spears", foot.ScreenName); Assert.Empty(foot.MissingReferences);
        Assert.Equal(new[] { "cavalry", "foot", "siege" }, prepared.UnitKeys);
    }

    [Fact]
    public void MountingAndEngineMetadataRemainDistinctAndEngineMissilesAreFollowed()
    {
        var prepared = new PreparedAttilaDatabase(Fixture(), [], () => { });
        var cavalry = prepared.QuerySemanticUnit("cavalry");
        Assert.True(cavalry.HasOrdinaryMount); Assert.Equal(157, cavalry.EffectiveHitPoints);
        Assert.Equal("light", cavalry.Weight); Assert.Equal("horse", cavalry.MountEntityType);
        Assert.Equal("horse_mesh", cavalry.MountVariant); Assert.Equal(80, cavalry.MountCount);
        var siege = prepared.QuerySemanticUnit("siege");
        Assert.True(siege.HasSpecialEntity); Assert.False(siege.CanScaleHitPoints); Assert.Null(siege.EffectiveHitPoints);
        Assert.Equal(4, siege.EngineCount); Assert.Equal(40, siege.NominalSoldiers);
        Assert.Equal("ballista", siege.EngineType); Assert.Equal("engine_weapon", siege.MissileWeaponKey);
        Assert.False(siege.MissilePrecursor); Assert.Equal("bolt", siege.DefaultProjectileKey);
        Assert.Equal(9, siege.Ammunition);
    }

    [Fact]
    public void MissingSubReferencesAreReportedWithoutDiscardingKnownUnitRole()
    {
        var db = Fixture(); db.Require("land_units_tables", "foot_land")["primary_melee_weapon"] = "missing";
        var prepared = new PreparedAttilaDatabase(db, [], () => { });
        var result = prepared.QuerySemanticUnit("foot");
        Assert.Equal("inf_melee", result.Category); Assert.Null(result.MeleeDamage);
        Assert.Contains("melee_weapons_tables:missing", result.MissingReferences);
        Assert.Throws<InvalidOperationException>(() => prepared.QuerySemanticUnit("not_installed"));
    }

    [Fact]
    public void ExplicitSelectionUsesNativeOrderingWithoutReadingOrWritingLiveModList()
    {
        string root = Scratch();
        try
        {
            string data = Path.Combine(root, "data"); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "language.txt"), "EN");
            Pack(Path.Combine(data, "data.pack"), 1); Pack(Path.Combine(data, "boot.pack"), 0);
            Pack(Path.Combine(data, "local_ru.pack"), 1); Pack(Path.Combine(data, "local_en.pack"), 1);
            string movie = Path.Combine(data, "movie.pack"); Pack(movie, 4);
            string high = Path.Combine(root, "high.pack"), low = Path.Combine(root, "low.pack"); Pack(high, 3); Pack(low, 3);
            string exe = Path.Combine(root, "Attila.exe");
            var resolved = PackSourceResolver.Resolve(exe, [high, low, movie]);
            Assert.Equal(Path.Combine(data, "boot.pack"), resolved[0]);
            Assert.Equal(new[] { low, high, movie }, resolved.TakeLast(3));
            Assert.DoesNotContain(Path.Combine(data, "local_ru.pack"), resolved);
            Assert.Equal(1, resolved.Count(p => p == movie));
            Assert.False(File.Exists(Path.Combine(root, "used_mods_cc.txt")));
            File.WriteAllText(Path.Combine(root, "used_mods_cc.txt"), "unchanged sentinel");
            Assert.Equal(resolved, PackSourceResolver.Resolve(exe, [high, low, movie]));
            Assert.Equal("unchanged sentinel", File.ReadAllText(Path.Combine(root, "used_mods_cc.txt")));
            Assert.Throws<InvalidDataException>(() => PackSourceResolver.Resolve(exe, ["relative.pack"]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("schema")]
    public void ExplicitPreparationUsesCompletedCacheAndDetectsSnapshotChanges(string mutation)
    {
        string root = Scratch();
        try
        {
            string runtime = Path.Combine(root, "runtime"), cache = Path.Combine(root, "cache"), pack = Path.Combine(root, "source.pack");
            Directory.CreateDirectory(Path.Combine(runtime, "data"));
            string schema = Path.Combine(runtime, "data", "schema_att.ron"); File.WriteAllText(schema, "isolated test schema"); Pack(pack, 3);
            var info = new FileInfo(pack);
            string signature = $"{StatFidelityBackend.CacheFormatVersion}\n{info.FullName}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schema)))}";
            string bucket = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))));
            string attempt = Path.Combine(bucket, "attempt-00000000000000000000000000000001"); Directory.CreateDirectory(attempt);
            foreach (var table in Fixture().Tables.Values)
                table.Write(Path.Combine(attempt, table.Name == "Loc" ? "text/db" : "db/" + table.Name, table.Name == "Loc" ? "fixture.loc.tsv" : "fixture.tsv"));
            File.WriteAllText(Path.Combine(bucket, "complete"), Path.GetFileName(attempt));
            var prepared = StatFidelityBackend.PrepareFromPacks(runtime, [pack], cache);
            Assert.Equal("inf_melee", prepared.QuerySemanticUnit("foot").Category);
            Assert.Equal(new[] { pack }, prepared.SourcePacks); prepared.AssertFresh();
            File.AppendAllText(mutation == "pack" ? pack : schema, "changed");
            Assert.Throws<InvalidOperationException>(() => prepared.AssertFresh());
            Assert.False(Directory.Exists(Path.Combine(runtime, "data", "stat-fidelity-cache")));
            Assert.Throws<InvalidDataException>(() => StatFidelityBackend.PrepareFromPacks(runtime, [pack, pack], cache));
        }
        finally { Directory.Delete(root, true); }
    }

    private static AttilaDatabase Fixture()
    {
        var db = new AttilaDatabase();
        void Add(string name, string[] columns, params string[][] values)
        {
            var table = new TsvTable(name, "1", columns);
            foreach (var row in values) table.Rows.Add(columns.Select((column, i) => (column, row[i])).ToDictionary(p => p.column, p => p.Item2));
            db.Merge(table);
        }
        Add("main_units_tables", ["unit", "land_unit", "num_men", "is_naval", "weight", "caste"],
            ["foot", "foot_land", "160", "false", "very_heavy", "heavy"], ["cavalry", "horse_land", "80", "false", "light", "light"], ["siege", "siege_land", "40", "false", "heavy", "heavy"]);
        Add("land_units_tables", ["key", "category", "class", "man_entity", "bonus_hit_points", "primary_melee_weapon", "primary_missile_weapon", "ammo", "mount", "num_mounts", "engine", "num_guns", "armour", "shield"],
            ["foot_land", "inf_melee", "inf_spr", "man", "7", "spear", "misleading_bow_name", "2", "", "0", "", "0", "mail", "kite"],
            ["horse_land", "cavalry", "cav_shk", "man", "7", "spear", "", "0", "mount", "80", "", "0", "cloth", "round"],
            ["siege_land", "artillery", "art_fld", "man", "0", "spear", "", "9", "", "0", "engine", "4", "cloth", "none"]);
        Add("battle_entities_tables", ["key", "type", "hit_points"], ["man", "man", "100"], ["horse", "horse", "50"]);
        Add("mounts_tables", ["key", "entity", "variant"], ["mount", "horse", "horse_mesh"]);
        Add("melee_weapons_tables", ["key", "melee_weapon_type", "damage", "ap_damage"], ["spear", "spear", "6", "4"]);
        Add("missile_weapons_tables", ["key", "precursor", "default_projectile", "audio_type"], ["misleading_bow_name", "true", "javelin", "javelin"], ["engine_weapon", "false", "bolt", "ballista"]);
        Add("projectiles_tables", ["key", "category", "shot_type", "damage", "ap_damage"], ["javelin", "small", "piercing", "20", "7"], ["bolt", "artillery", "piercing", "50", "200"]);
        Add("battlefield_engines_tables", ["key", "engine_type", "missile_weapon"], ["engine", "ballista", "engine_weapon"]);
        Add("unit_variants_tables", ["unit", "faction", "variant"], ["foot_land", "", "foot_mesh"], ["horse_land", "", "horse_mesh"], ["siege_land", "", "siege_mesh"]);
        Add("Loc", ["key", "text", "tooltip"], ["land_units_onscreen_name_foot_land", "Foot spears", "true"]);
        return db;
    }

    private static void Pack(string path, int type)
    {
        using var writer = new BinaryWriter(File.Create(path)); writer.Write(Encoding.ASCII.GetBytes("PFH4")); writer.Write(type);
    }
    private static string Scratch()
    {
        string path = Path.Combine(Path.GetTempPath(), "cc-semantic-query-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
}
