using CrusaderWars.StatFidelity;
using Xunit;

public sealed class CavalryAdapterTests
{
    private static AttilaDatabase Fixture()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "EarlCavalry");
        var db = new AttilaDatabase(); db.MergeDirectory(root);
        db.CavalryAssets = new();
        using var evidence = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "asset-proofs.json")));
        foreach (var row in evidence.RootElement.EnumerateArray())
        {
            db.CavalryAssets.Files[row.GetProperty("variantPath").GetString()!] = row.GetProperty("variantHash").GetString()!;
            db.CavalryAssets.Animations[row.GetProperty("riderAnimation").GetString()!] = new() { row.GetProperty("riderHash").GetString()! };
            db.CavalryAssets.Animations[row.GetProperty("mountAnimation").GetString()!] = new() { row.GetProperty("mountHash").GetString()! };
        }
        return db;
    }

    [Fact]
    public void CountOnlyOverrideUsesCurrentNominalMenWithoutTouchingStockOrDamage()
    {
        const string unit = "mk_fre_t1_chevaliers_mounted";
        var db = Fixture(); var main = db.Require("main_units_tables", unit); var land = db.Require("land_units_tables", main["land_unit"]);
        main["num_men"] = "150"; land["num_mounts"] = "90";
        var prepared = new PreparedAttilaDatabase(db, Array.Empty<string>(), () => { });
        var adapter = prepared.Query(unit).CavalryAdapter!;
        Assert.NotNull(adapter); Assert.Equal(150, adapter.SourceMen); Assert.Equal(90, adapter.SourceMounts); Assert.Equal(150, adapter.PlannedMounts);
        string root = Path.Combine(Path.GetTempPath(), "cc-cavalry-" + Guid.NewGuid().ToString("N"));
        try {
            prepared.Generate(new[] { new UnitStatProfile("count-buff", unit, 1, 1) }, root);
            Assert.Equal("150", TsvTable.Read(Path.Combine(root, "db", "land_units_tables", "cc_stat_fidelity.tsv")).Rows.Single()["num_mounts"]);
            Assert.Equal("90", land["num_mounts"]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AssetEvidenceResolvesVirtualReplacementAndRejectsConflictingAnimationIds()
    {
        string root = Path.Combine(Path.GetTempPath(), "cc-assets-" + Guid.NewGuid().ToString("N"));
        string low = Path.Combine(root, "low"), high = Path.Combine(root, "high");
        try {
            foreach (string dir in new[] { low, high }) Directory.CreateDirectory(Path.Combine(dir, "animations", "animation_tables"));
            string relative = Path.Combine("animations", "animation_tables", "same.txt");
            File.WriteAllText(Path.Combine(low, relative), "animation_table rider { skeleton_type old }");
            File.WriteAllText(Path.Combine(high, relative), "animation_table rider { skeleton_type new }");
            var winner = CavalryAssetEvidence.Read(new[] { low, high });
            Assert.Single(winner.Animations["rider"]);
            Assert.Equal(CavalryAssetEvidence.Read(new[] { high }).Animations["rider"], winner.Animations["rider"]);
            File.WriteAllText(Path.Combine(high, "animations", "animation_tables", "other.txt"), "animation_table rider { skeleton_type conflict }");
            Assert.Equal(2, CavalryAssetEvidence.Read(new[] { low, high }).Animations["rider"].Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("mk_fre_t1_chevaliers_mounted", 70, 55, "horse")]
    [InlineData("mk_fre_t3_gentilshommes_a_bec_de_corbin", 70, 50, "giant")]
    [InlineData("mk_abb_t1_caliph_bodyguard", 35, 25, "giant")]
    [InlineData("mk_fre_t1_arbaletriers_mounted", 90, 55, "horse")]
    public void QueryAndPrivateNeutralRowsUseSameReviewedAdapterWithoutChangingSource(string unit, int men, int mounts, string type)
    {
        var db = Fixture(); var prepared = new PreparedAttilaDatabase(db, Array.Empty<string>(), () => { });
        var raw = prepared.QuerySemanticUnit(unit); var combat = prepared.Query(unit);
        Assert.Equal(mounts, raw.MountCount); Assert.Equal(men, raw.PlannedMountCount);
        Assert.Equal(type, raw.MountEntityType); Assert.NotNull(raw.CavalryAdapter);
        Assert.False(raw.CavalryAdapter.EngineValidated); Assert.Equal(raw.CavalryAdapter, combat.CavalryAdapter);
        Assert.Equal(raw.EntityHitPoints + raw.BonusHitPoints + raw.MountHitPoints, combat.EffectiveHitPoints);
        var originalLand = new Dictionary<string,string>(db.Require("land_units_tables", raw.LandUnitKey));
        var builder = new StatVariantBuilder(db); string key = builder.Add(new("neutral", unit, 1, 1));
        var land = builder.Tables.Single(t => t.Name == "land_units_tables").Rows.Single();
        var main = builder.Tables.Single(t => t.Name == "main_units_tables").Rows.Single();
        Assert.Equal(men.ToString(), land["num_mounts"]); Assert.Equal(men.ToString(), main["num_men"]);
        Assert.Equal(originalLand, db.Require("land_units_tables", raw.LandUnitKey));
        Assert.Equal(raw.MountKey, land["mount"]); Assert.Equal(raw.BonusHitPoints.ToString(), land["bonus_hit_points"]);
        Assert.Equal(raw.EntityHitPoints.ToString(), builder.Tables.Single(t => t.Name == "battle_entities_tables").Rows.Single()["hit_points"]);
        foreach (var field in originalLand.Keys.Except(new[] { "key", "num_mounts", "man_entity", "primary_melee_weapon", "primary_missile_weapon" }))
            Assert.Equal(originalLand[field], land[field]);
    }

    [Theory]
    [InlineData("mk_fre_t1_chevaliers_mounted")]
    [InlineData("mk_fre_t3_gentilshommes_a_bec_de_corbin")]
    public void ActiveNumericBuffsRemainEffectiveAndPrivatePhysicsArePreserved(string unit)
    {
        var db = Fixture(); var main = db.Require("main_units_tables", unit); var land = db.Require("land_units_tables", main["land_unit"]);
        var mount = db.Require("mounts_tables", land["mount"]); var horse = db.Require("battle_entities_tables", mount["entity"]);
        horse["hit_points"] = "130"; horse["mass"] = "1999.0000";
        var raw = new PreparedAttilaDatabase(db, Array.Empty<string>(), () => { }).QuerySemanticUnit(unit);
        Assert.NotNull(raw.CavalryAdapter); Assert.Equal(130, raw.MountHitPoints);
        var builder = new StatVariantBuilder(db); string key = builder.Add(new("scaled", unit, 1.5, 1.75));
        var generatedHorse = builder.Tables.Single(t => t.Name == "battle_entities_tables").Rows.Single(r => r["key"] == key + "_mount_entity");
        foreach (var field in horse.Keys.Except(new[] { "key", "hit_points" })) Assert.Equal(horse[field], generatedHorse[field]);
        var generatedLand = builder.Tables.Single(t => t.Name == "land_units_tables").Rows.Single();
        var generatedMan = builder.Tables.Single(t => t.Name == "battle_entities_tables").Rows.Single(r => r["key"] == key + "_man");
        Assert.Equal((int)Math.Round(raw.EffectiveHitPoints!.Value * 1.75, MidpointRounding.AwayFromZero),
            int.Parse(generatedMan["hit_points"]) + int.Parse(generatedLand["bonus_hit_points"]) + int.Parse(generatedHorse["hit_points"]));
        Assert.Equal("130", horse["hit_points"]);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("entity")]
    [InlineData("animation")]
    [InlineData("variant")]
    [InlineData("no-proof")]
    [InlineData("same-key-vmd-replacement")]
    [InlineData("same-key-animation-replacement")]
    [InlineData("duplicate-animation-id")]
    public void ChangedStructuralOrEffectiveAssetGraphIsNotAdapted(string mutation)
    {
        const string unit = "mk_fre_t1_chevaliers_mounted";
        var db = Fixture(); var main = db.Require("main_units_tables", unit); var land = db.Require("land_units_tables", main["land_unit"]);
        var mount = db.Require("mounts_tables", land["mount"]);
        switch (mutation) {
            case "count": land["num_mounts"] = "120"; break;
            case "entity": db.Require("battle_entities_tables", mount["entity"])["type"] = "elephant"; break;
            case "animation": land["man_animation"] = "elephant_rider"; break;
            case "variant": mount["variant"] = "unknown_variant"; break;
            case "no-proof": db.CavalryAssets = null; break;
            case "same-key-vmd-replacement": db.CavalryAssets!.Files["variantmeshes/variantmeshdefinitions/" + mount["variant"] + ".variantmeshdefinition"] = "CHANGED"; break;
            case "same-key-animation-replacement": db.CavalryAssets!.Animations[land["man_animation"]] = new() { "CHANGED" }; break;
            case "duplicate-animation-id": db.CavalryAssets!.Animations[land["man_animation"]].Add("CONFLICT"); break;
        }
        var prepared = new PreparedAttilaDatabase(db, Array.Empty<string>(), () => { });
        Assert.Null(prepared.QuerySemanticUnit(unit).CavalryAdapter); Assert.False(prepared.Query(unit).CanScaleHitPoints);
        Assert.Throws<InvalidOperationException>(() => new StatVariantBuilder(db).Add(new("reject", unit, 1, 2)));
    }
}
