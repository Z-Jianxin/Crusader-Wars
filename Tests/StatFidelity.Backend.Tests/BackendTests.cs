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
    public void VisualVariantsFollowLandKeyWhenRecruitmentKeyDiffers()
    {
        var source = Fixture();
        // A row for an unrelated land unit whose key happens to equal the recruitment key
        // must not be substituted for the selected unit's actual visual definition.
        source.Merge(Table("unit_variants_tables", new[] { "unit", "faction", "name", "variant", "unit_card" },
            new[] { "spears", "", "unrelated_name", "unrelated_mesh", "unrelated_card" }));
        var builder = new StatVariantBuilder(source);
        string key = builder.Add(new("visual-identity", "spears", 1, 1));
        var variant = Assert.Single(builder.Tables.Single(t => t.Name == "unit_variants_tables").Rows);
        Assert.Equal(key, variant["unit"]);
        Assert.Equal("original_mesh", variant["variant"]);
    }

    [Fact]
    public void PreparedQueriesDoNotAssertMountedHealthAndNamesDoNotLeakBetweenBattles()
    {
        var source = Fixture();
        var prepared = new PreparedAttilaDatabase(source, Array.Empty<string>(), () => { });
        var stats = prepared.Query("spears");
        Assert.Equal(25, stats.TotalMeleeDamage);
        Assert.Equal(80, stats.EffectiveInfantryHitPoints);
        Assert.True(stats.HasMissileWeapon);
        source.Require("land_units_tables", "spears_land")["mount"] = "horse";
        Assert.Null(prepared.Query("spears").EffectiveInfantryHitPoints);
        Assert.False(prepared.Query("spears").CanScaleHitPoints);
        source.Require("land_units_tables", "spears_land")["mount"] = "";
        string directory = Scratch();
        try
        {
            string first = Path.Combine(directory, "first"), second = Path.Combine(directory, "second");
            Table("Loc", new[] { "key", "text", "tooltip" }, new[] { "land_units_onscreen_name_spears_land", "Battle-specific name", "true" })
                .Write(Path.Combine(first, "text", "db", "battle_names.loc.tsv"));
            var profiles = new[] { new UnitStatProfile("one", "spears", 1, 1) };
            prepared.Generate(profiles, first);
            prepared.Generate(profiles, second);
            Assert.Equal("Battle-specific name", TsvTable.Read(Path.Combine(first, "text", "db", "cc_stat_fidelity.loc.tsv")).Rows[0]["text"]);
            Assert.Equal("Spearmen", TsvTable.Read(Path.Combine(second, "text", "db", "cc_stat_fidelity.loc.tsv")).Rows[0]["text"]);
            var stale = new PreparedAttilaDatabase(source, Array.Empty<string>(), () => throw new InvalidOperationException("changed"));
            Assert.Throws<InvalidOperationException>(() => stale.Generate(profiles, second));
        }
        finally { Directory.Delete(directory, true); }
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
    public void NativeLocalizationPreservesMultilineUnicodeAndRejectsCorruption()
    {
        string directory = Scratch();
        try
        {
            string file = Path.Combine(directory, "source.loc");
            string value = "Soldiers \"elite\"\r\nsecond\tline \u519b\u961f";
            using (var writer = new BinaryWriter(File.Create(file)))
            {
                writer.Write((ushort)0xFEFF); writer.Write(System.Text.Encoding.ASCII.GetBytes("LOC"));
                writer.Write((byte)0); writer.Write(1); writer.Write((uint)1);
                foreach (string text in new[] { "land_units_onscreen_name_test", value })
                { writer.Write((ushort)text.Length); writer.Write(System.Text.Encoding.Unicode.GetBytes(text)); }
                writer.Write((byte)1);
            }
            var table = LocBinaryReader.Read(file);
            Assert.Equal(value, Assert.Single(table.Rows)["text"]);
            string tsv = Path.Combine(directory, "recovered.loc.tsv"); table.Write(tsv);
            Assert.Equal(value, TsvTable.Read(tsv).Rows[0]["text"]);
            File.AppendAllText(file, "unexpected");
            Assert.Throws<InvalidDataException>(() => LocBinaryReader.Read(file));
        }
        finally { Directory.Delete(directory, true); }
    }

    [InstalledLocalizationPackFact]
    public void InstalledMultilineLocalizationRecoversLosslessly()
    {
        string directory = Scratch();
        try
        {
            var db = StatFidelityBackend.LoadPacks(Environment.GetEnvironmentVariable("CC_RPFM")!,
                Environment.GetEnvironmentVariable("CC_SCHEMA")!,
                new[] { Environment.GetEnvironmentVariable("CC_TEST_LOC_PACK")! }, Path.Combine(directory, "cache"));
            var row = db.Require("Loc", "effect_bundles_localised_description_bundle_supplies_0");
            Assert.True(row["text"].Contains('\r') || row["text"].Contains('\n'));
            string raw = Assert.Single(Directory.GetFiles(directory, "effect_bundles_supply_system.loc", SearchOption.AllDirectories));
            var original = LocBinaryReader.Read(raw).Rows.Single(r => r["key"] == row["key"]);
            Assert.Equal(original["text"], row["text"]);
            Assert.Equal("true", row["tooltip"]);
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
    public void UnsupportedMountShapesFailAndNeutralHpComponentsStayExact()
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

    [Fact]
    public void OrdinaryCavalryScalesPrivateComponentsWithExactMountedAndDismountedTotals()
    {
        var source = CavalryFixture();
        var prepared = new PreparedAttilaDatabase(source, Array.Empty<string>(), () => { });
        var stats = prepared.Query("spears");
        Assert.True(stats.CanScaleHitPoints);
        Assert.Null(stats.EffectiveInfantryHitPoints);
        Assert.Equal(125, stats.MountHitPoints);
        Assert.Equal(232, stats.EffectiveHitPoints);
        foreach (double factor in new[] { 2.0, .5, 1.3 })
        {
            var builder = new StatVariantBuilder(source);
            string key = builder.Add(new("mounted", "spears", 1, factor));
            var land = Assert.Single(builder.Tables.Single(t => t.Name == "land_units_tables").Rows);
            var mount = Assert.Single(builder.Tables.Single(t => t.Name == "mounts_tables").Rows);
            var entities = builder.Tables.Single(t => t.Name == "battle_entities_tables");
            int manHp = int.Parse(entities.Rows.Single(r => r["key"] == land["man_entity"])["hit_points"]);
            int bonus = int.Parse(land["bonus_hit_points"]);
            int mountHp = int.Parse(entities.Rows.Single(r => r["key"] == mount["entity"])["hit_points"]);
            Assert.Equal((int)Math.Round(232 * factor, MidpointRounding.AwayFromZero), manHp + bonus + mountHp);
            Assert.Equal((int)Math.Round(107 * factor, MidpointRounding.AwayFromZero), manHp + bonus);
            Assert.Equal((int)Math.Round(90 * factor, MidpointRounding.AwayFromZero), manHp);
            Assert.Equal(key + "_mount", land["mount"]);
            Assert.Equal("source_animation", mount["animation"]);
        }
        Assert.Equal("90", source.Require("battle_entities_tables", "man")["hit_points"]);
        Assert.Equal("125", source.Require("battle_entities_tables", "horse_entity")["hit_points"]);
        Assert.Equal("17", source.Require("land_units_tables", "spears_land")["bonus_hit_points"]);
        var neutral = new StatVariantBuilder(source);
        neutral.Add(new("neutral", "spears", 1, 1));
        Assert.DoesNotContain(neutral.Tables, t => t.Name == "mounts_tables");
        Assert.Equal("horse", neutral.Tables.Single(t => t.Name == "land_units_tables").Rows[0]["mount"]);
        Assert.Equal("90", neutral.Tables.Single(t => t.Name == "battle_entities_tables").Rows[0]["hit_points"]);
        Assert.Equal("17", neutral.Tables.Single(t => t.Name == "land_units_tables").Rows[0]["bonus_hit_points"]);
    }

    [Fact]
    public void UnsupportedCrewShapesAndMissingMountReferencesCannotPublishHpVariants()
    {
        var multiRider = CavalryFixture();
        multiRider.Require("land_units_tables", "spears_land")["num_mounts"] = "40";
        Assert.Throws<InvalidOperationException>(() => new StatVariantBuilder(multiRider).Add(new("multi", "spears", 1, 2)));
        Assert.Null(new PreparedAttilaDatabase(multiRider, Array.Empty<string>(), () => { }).Query("spears").EffectiveHitPoints);
        foreach (string field in new[] { "engine", "animal", "chariot", "articulated_record" })
        {
            var special = CavalryFixture(); special.Require("land_units_tables", "spears_land")[field] = "special";
            Assert.Throws<InvalidOperationException>(() => new StatVariantBuilder(special).Add(new(field, "spears", 1, 2)));
        }
        var missing = CavalryFixture(); missing.Require("land_units_tables", "spears_land")["mount"] = "absent";
        Assert.Throws<InvalidOperationException>(() => new StatVariantBuilder(missing).Add(new("missing", "spears", 1, 2)));
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
            string marker = Directory.GetDirectories(Path.Combine(directory, "cache"))
                .Where(p => Path.GetFileName(p) != "cavalry-assets-v1").Select(p => Path.Combine(p, "complete")).Single(File.Exists);
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
        db.Merge(Table("main_units_tables", new[] { "unit", "land_unit", "is_naval", "unique_index", "in_encyclopedia", "num_men" }, new[] { "spears", "spears_land", "false", "1", "true", "160" }));
        db.Merge(Table("land_units_tables", new[] { "key", "man_entity", "bonus_hit_points", "primary_melee_weapon", "primary_missile_weapon", "engine", "mount", "num_mounts", "category", "animal", "chariot", "articulated_record" }, new[] { "spears_land", "man", "20", "spear", "bow", "", "", "0", "inf_melee", "", "", "" }));
        db.Merge(Table("battle_entities_tables", new[] { "key", "hit_points" }, new[] { "man", "60" }));
        db.Merge(Table("melee_weapons_tables", new[] { "key", "damage", "ap_damage" }, new[] { "spear", "20", "5" }));
        db.Merge(Table("missile_weapons_tables", new[] { "key", "default_projectile" }, new[] { "bow", "arrow" }));
        db.Merge(Table("projectiles_tables", new[] { "key", "damage", "ap_damage" }, new[] { "arrow", "12", "4" }, new[] { "fire_arrow", "8", "2" }));
        db.Merge(Table("missile_weapons_to_projectiles_tables", new[] { "missile_weapon", "projectile" }, new[] { "bow", "arrow" }, new[] { "bow", "fire_arrow" }));
        db.Merge(Table("unit_variants_tables", new[] { "unit", "faction", "name", "variant", "unit_card" }, new[] { "spears_land", "", "original_name", "original_mesh", "original_card" }));
        db.Merge(Table("land_units_to_unit_abilites_junctions_tables", new[] { "land_unit", "ability" }, new[] { "spears_land", "shield_wall" }));
        db.Merge(Table("Loc", new[] { "key", "text", "tooltip" }, new[] { "land_units_onscreen_name_spears_land", "Spearmen", "true" }));
        return db;
    }

    private static AttilaDatabase CavalryFixture()
    {
        var db = Fixture();
        var land = db.Require("land_units_tables", "spears_land");
        land["mount"] = "horse"; land["num_mounts"] = "160"; land["category"] = "cavalry"; land["bonus_hit_points"] = "17";
        db.Require("battle_entities_tables", "man")["hit_points"] = "90";
        db.Merge(Table("battle_entities_tables", new[] { "key", "hit_points" }, new[] { "horse_entity", "125" }));
        db.Merge(Table("mounts_tables", new[] { "key", "entity", "animation" }, new[] { "horse", "horse_entity", "source_animation" }));
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

public sealed class InstalledLocalizationPackFactAttribute : FactAttribute
{
    public InstalledLocalizationPackFactAttribute()
    {
        if (new[] { "CC_RPFM", "CC_SCHEMA", "CC_TEST_LOC_PACK" }.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Set CC_RPFM, CC_SCHEMA and CC_TEST_LOC_PACK to the installed Age of Justinian pack for its multiline localization regression.";
    }
}
