using CrusaderWars.StatFidelity;
using Xunit;

public sealed class BackendTests
{
    [Fact]
    public void SameBaseUnitOnOpposingSidesHasIndependentDamageAndHealth()
    {
        var source = Fixture();
        var builder = new StatVariantBuilder(source);
        string elite = builder.Add(new("army1-regiment7", "spears", 3, 2));
        string weak = builder.Add(new("army2-regiment8", "spears", .5, .5));
        Assert.NotEqual(elite, weak);
        var weapons = builder.Tables.Single(t => t.Name == "melee_weapons_tables");
        Assert.Equal("60", weapons.Rows.Single(r => r["key"] == elite + "_melee")["damage"]);
        Assert.Equal("15", weapons.Rows.Single(r => r["key"] == elite + "_melee")["ap_damage"]);
        Assert.Equal("10", weapons.Rows.Single(r => r["key"] == weak + "_melee")["damage"]);
        var entities = builder.Tables.Single(t => t.Name == "battle_entities_tables");
        Assert.Equal("160", entities.Rows.Single(r => r["key"] == elite + "_man")["hit_points"]);
        Assert.Equal("40", entities.Rows.Single(r => r["key"] == weak + "_man")["hit_points"]);
        // Rebuilding against the same source proves variants never mutate the shared weapon/entity.
        var baseline = new StatVariantBuilder(source); baseline.Add(new("baseline", "spears", 1, 1));
        Assert.Equal("20", baseline.Tables.Single(t => t.Name == "melee_weapons_tables").Rows[0]["damage"]);
        Assert.Equal("60", baseline.Tables.Single(t => t.Name == "battle_entities_tables").Rows[0]["hit_points"]);
        Assert.Equal("20", baseline.Tables.Single(t => t.Name == "land_units_tables").Rows[0]["bonus_hit_points"]);
    }

    [Fact]
    public void AlternateAmmunitionAndAbilitiesAndVisualsArePreserved()
    {
        var builder = new StatVariantBuilder(Fixture());
        string key = builder.Add(new("archer", "spears", 2, 1));
        var projectiles = builder.Tables.Single(t => t.Name == "projectiles_tables");
        Assert.Equal(2, projectiles.Rows.Count);
        Assert.Contains(projectiles.Rows, r => r["damage"] == "24" && r["ap_damage"] == "8");
        Assert.Contains(projectiles.Rows, r => r["damage"] == "16" && r["ap_damage"] == "4");
        Assert.All(builder.Tables.Single(t => t.Name == "missile_weapons_to_projectiles_tables").Rows,
            r => Assert.Equal(key + "_missile", r["missile_weapon"]));
        Assert.Equal("shield_wall", builder.Tables.Single(t => t.Name == "land_units_to_unit_abilites_junctions_tables").Rows[0]["ability"]);
        Assert.Equal("original_mesh", builder.Tables.Single(t => t.Name == "unit_variants_tables").Rows[0]["variant"]);
        Assert.Equal("Spearmen", builder.Tables.Single(t => t.Name == "Loc").Rows[0]["text"]);
    }

    [Fact]
    public void HighPriorityRowsWinAndSchemaMismatchFails()
    {
        var db = Fixture();
        db.Merge(Table("melee_weapons_tables", new[] { "key", "damage", "ap_damage" }, new[] { "spear", "30", "6" }));
        var builder = new StatVariantBuilder(db); builder.Add(new("one", "spears", 2, 1));
        Assert.Equal("60", builder.Tables.Single(t => t.Name == "melee_weapons_tables").Rows[0]["damage"]);
        Assert.Throws<InvalidDataException>(() => db.Merge(new TsvTable("melee_weapons_tables", "99", new[] { "key" })));
    }

    [Fact]
    public void MissingReferencesAndOverflowAbortWithoutPublishingPartialTables()
    {
        string directory = Scratch();
        try
        {
            string sentinel = Path.Combine(directory, "db", "main_units_tables", "cc_stat_fidelity.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!); File.WriteAllText(sentinel, "previous output");
            Assert.Throws<InvalidOperationException>(() => StatFidelityBackend.GenerateFromDatabase(Fixture(),
                new[] { new UnitStatProfile("valid", "spears", 2, 1), new("missing", "nonexistent", 1, 1) }, directory));
            Assert.Equal("previous output", File.ReadAllText(sentinel));
            Assert.Throws<OverflowException>(() => new StatVariantBuilder(Fixture()).Add(new("overflow", "spears", 1e20, 1)));
            Assert.Throws<InvalidOperationException>(() => StatFidelityBackend.GeneratedKey(new("invalid", "spears", double.NaN, 1)));
            Assert.Throws<InvalidOperationException>(() => new StatVariantBuilder(Fixture()).Add(new("below_resolution", "spears", 1, .001)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TsvPreservesQuotedNamesAndMetadata()
    {
        string directory = Scratch();
        try
        {
            var original = Table("Loc", new[] { "key", "text", "tooltip" }, new[] { "name", "Soldiers \"elite\"\nsecond\tline", "true" });
            string file = Path.Combine(directory, "cc_stat_fidelity.loc.tsv"); original.Write(file);
            var result = TsvTable.Read(file);
            Assert.Equal(original.Columns, result.Columns); Assert.Equal(original.Version, result.Version);
            Assert.Equal(original.Rows[0]["text"], result.Rows[0]["text"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ActiveScriptResolvesWorkingDirectoriesAndRejectsMissingMods()
    {
        string directory = Scratch();
        try
        {
            string data = Path.Combine(directory, "data"); Directory.CreateDirectory(data);
            string workshop = Path.Combine(directory, "workshop"); Directory.CreateDirectory(workshop);
            File.WriteAllText(Path.Combine(data, "override.pack"), "fixture");
            File.WriteAllText(Path.Combine(workshop, "roster.pack"), "fixture");
            string script = Path.Combine(directory, "used_mods_cc.txt");
            File.WriteAllText(script, $"mod \"CrusaderConflicts.pack\";\nmod \"override.pack\";\nadd_working_directory \"{workshop}\";\nmod \"roster.pack\";\n");
            Assert.Equal(new[] { "override.pack", "roster.pack" }, PackSourceResolver.ResolveScript(script, data).Select(Path.GetFileName));
            File.AppendAllText(script, "mod \"missing.pack\";\n");
            Assert.Throws<FileNotFoundException>(() => PackSourceResolver.ResolveScript(script, data));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void HigherPackReplacesWholeVirtualFileBeforeRowsMerge()
    {
        string directory = Scratch();
        try
        {
            string low = Path.Combine(directory, "low"); string high = Path.Combine(directory, "high");
            string[] columns = { "key", "damage", "ap_damage" };
            Table("melee_weapons_tables", columns, new[] { "retained", "10", "1" }, new[] { "removed", "20", "2" })
                .Write(Path.Combine(low, "db", "melee_weapons_tables", "same.tsv"));
            Table("melee_weapons_tables", columns, new[] { "separate_fragment", "30", "3" })
                .Write(Path.Combine(low, "db", "melee_weapons_tables", "separate.tsv"));
            Table("melee_weapons_tables", columns, new[] { "retained", "40", "4" })
                .Write(Path.Combine(high, "db", "melee_weapons_tables", "same.tsv"));
            var db = AttilaDatabase.FromPackDirectories(new[] { low, high });
            Assert.Equal("40", db.Require("melee_weapons_tables", "retained")["damage"]);
            Assert.Equal("30", db.Require("melee_weapons_tables", "separate_fragment")["damage"]);
            Assert.Throws<InvalidOperationException>(() => db.Require("melee_weapons_tables", "removed"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NativeBootAndOnlySelectedLocalizationAreLoaded()
    {
        string directory = Scratch();
        try
        {
            string data = Path.Combine(directory, "data"); Directory.CreateDirectory(data);
            foreach (var (name, type) in new[] { ("boot.pack", 0), ("data.pack", 1), ("local_en.pack", 1), ("local_fr.pack", 1), ("local_ru.pack", 1), ("local_en_shared_rome2.pack", 1) })
            {
                using var stream = File.Create(Path.Combine(data, name)); using var writer = new BinaryWriter(stream);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("PFH4")); writer.Write(type);
            }
            File.WriteAllText(Path.Combine(data, "language.txt"), "FR\r\n");
            File.WriteAllText(Path.Combine(directory, "used_mods_cc.txt"), "mod \"CrusaderConflicts.pack\";");
            var packs = PackSourceResolver.Resolve(Path.Combine(directory, "Attila.exe")).Select(Path.GetFileName).ToArray();
            Assert.Contains("boot.pack", packs); Assert.Contains("local_fr.pack", packs);
            Assert.Contains("local_en_shared_rome2.pack", packs);
            Assert.DoesNotContain("local_en.pack", packs); Assert.DoesNotContain("local_ru.pack", packs);
            Assert.True(Array.IndexOf(packs, "local_en_shared_rome2.pack") < Array.IndexOf(packs, "local_fr.pack"));
            File.Delete(Path.Combine(data, "language.txt"));
            Assert.Throws<InvalidDataException>(() => PackSourceResolver.ResolveLocale(data));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnverifiedMountedToughnessFailsRatherThanClaimingRiderHpIsEnough()
    {
        var db = Fixture();
        db.Require("land_units_tables", "spears_land")["mount"] = "horse";
        Assert.Throws<InvalidOperationException>(() => new StatVariantBuilder(db).Add(new("cavalry", "spears", 2, 2)));
        // Damage-only scaling preserves each HP component, not merely their sum: bonus HP may
        // affect mount and rider differently, so folding it into man_entity is not neutral.
        var damageOnly = new StatVariantBuilder(db);
        Assert.NotNull(damageOnly.Add(new("cavalry_damage", "spears", 2, 1)));
        Assert.Equal("60", damageOnly.Tables.Single(t => t.Name == "battle_entities_tables").Rows[0]["hit_points"]);
        Assert.Equal("20", damageOnly.Tables.Single(t => t.Name == "land_units_tables").Rows[0]["bonus_hit_points"]);
    }

    // Opt-in real RPFM/AD919 integration: set CC_RPFM, CC_SCHEMA and CC_TEST_PACK.
    [InstalledPackFact]
    public void InstalledPackCanGenerateAndRoundTripVariants()
    {
        string? rpfm = Environment.GetEnvironmentVariable("CC_RPFM");
        string? schema = Environment.GetEnvironmentVariable("CC_SCHEMA");
        string? pack = Environment.GetEnvironmentVariable("CC_TEST_PACK");
        if (rpfm == null || schema == null || pack == null) return;
        string directory = Scratch();
        try
        {
            var db = StatFidelityBackend.LoadPacks(rpfm, schema, new[] { pack }, Path.Combine(directory, "cache"));
            string marker = Directory.GetFiles(Path.Combine(directory, "cache"), "complete", SearchOption.AllDirectories).Single();
            string previousAttempt = File.ReadAllText(marker);
            string preserved = Path.Combine(Path.GetDirectoryName(marker)!, previousAttempt, "incomplete-sentinel");
            File.WriteAllText(preserved, "preserve me"); File.Delete(marker);
            db = StatFidelityBackend.LoadPacks(rpfm, schema, new[] { pack }, Path.Combine(directory, "cache"));
            Assert.Equal("preserve me", File.ReadAllText(preserved));
            Assert.NotEqual(previousAttempt, File.ReadAllText(marker));
            // Find a self-contained real row; a standalone overhaul pack can depend on absent vanilla rows.
            UnitStatProfile? selected = null;
            foreach (var table in Directory.GetFiles(Path.Combine(directory, "cache"), "*.tsv", SearchOption.AllDirectories))
            {
                var t = TsvTable.Read(table); if (t.Name != "main_units_tables") continue;
                foreach (var row in t.Rows)
                {
                    var candidate = new UnitStatProfile("integration", row["unit"], 1.5, 2);
                    try { new StatVariantBuilder(db).Add(candidate); selected = candidate; break; }
                    catch (InvalidOperationException) { }
                }
                if (selected != null) break;
            }
            Assert.NotNull(selected);
            string generated = Path.Combine(directory, "generated");
            var keys = StatFidelityBackend.GenerateFromDatabase(db, new[] { selected! }, generated);
            string outputPack = Path.Combine(directory, "roundtrip.pack");
            RpfmRunner.Run(rpfm, "--game", "attila", "pack", "create", "-p", outputPack);
            RpfmRunner.Run(rpfm, "--game", "attila", "pack", "add", "-p", outputPack, "-t", schema, "-F", generated);
            string extracted = Path.Combine(directory, "extracted"); Directory.CreateDirectory(extracted);
            RpfmRunner.Run(rpfm, "--game", "attila", "pack", "extract", "-p", outputPack, "-t", schema, "-F", "/;" + extracted);
            var original = TsvTable.Read(Path.Combine(generated, "db", "land_units_tables", "cc_stat_fidelity.tsv"));
            var roundtrip = TsvTable.Read(Path.Combine(extracted, "db", "land_units_tables", "cc_stat_fidelity.tsv"));
            Assert.Equal(original.Rows[0]["key"], roundtrip.Rows[0]["key"]);
            Assert.Equal(keys["integration"], roundtrip.Rows[0]["key"]);
            Assert.Equal(original.Rows[0]["man_entity"], roundtrip.Rows[0]["man_entity"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Scratch()
    {
        string path = Path.Combine(Path.GetTempPath(), "cc-fidelity-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static TsvTable Table(string name, string[] columns, params string[][] rows)
    {
        var table = new TsvTable(name, "1", columns);
        foreach (var row in rows) table.Rows.Add(columns.Select((column, i) => (column, row[i])).ToDictionary(x => x.column, x => x.Item2));
        return table;
    }
    private static AttilaDatabase Fixture()
    {
        var db = new AttilaDatabase();
        db.Merge(Table("main_units_tables", new[] { "unit", "land_unit", "is_naval", "unique_index", "in_encyclopedia" }, new[] { "spears", "spears_land", "false", "1", "true" }));
        db.Merge(Table("land_units_tables", new[] { "key", "man_entity", "bonus_hit_points", "primary_melee_weapon", "primary_missile_weapon", "engine" }, new[] { "spears_land", "man", "20", "spear", "bow", "" }));
        db.Merge(Table("battle_entities_tables", new[] { "key", "hit_points" }, new[] { "man", "60" }));
        db.Merge(Table("melee_weapons_tables", new[] { "key", "damage", "ap_damage" }, new[] { "spear", "20", "5" }));
        db.Merge(Table("missile_weapons_tables", new[] { "key", "default_projectile" }, new[] { "bow", "arrow" }));
        db.Merge(Table("projectiles_tables", new[] { "key", "damage", "ap_damage" }, new[] { "arrow", "12", "4" }, new[] { "fire_arrow", "8", "2" }));
        db.Merge(Table("missile_weapons_to_projectiles_tables", new[] { "missile_weapon", "projectile" }, new[] { "bow", "arrow" }, new[] { "bow", "fire_arrow" }));
        db.Merge(Table("unit_variants_tables", new[] { "unit", "faction", "name", "variant", "unit_card" }, new[] { "spears", "", "original_name", "original_mesh", "original_card" }));
        db.Merge(Table("land_units_to_unit_abilites_junctions_tables", new[] { "land_unit", "ability" }, new[] { "spears_land", "shield_wall" }));
        db.Merge(Table("Loc", new[] { "key", "text", "tooltip" }, new[] { "land_units_onscreen_name_spears_land", "Spearmen", "true" }));
        return db;
    }
}

public sealed class InstalledPackFactAttribute : FactAttribute
{
    public InstalledPackFactAttribute()
    {
        if (new[] { "CC_RPFM", "CC_SCHEMA", "CC_TEST_PACK" }.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Set CC_RPFM, CC_SCHEMA and CC_TEST_PACK to run the real installed-pack integration check.";
    }
}
