using CrusaderWars.StatFidelity;
using CrusaderWars.unit_mapper;
using Xunit;

public sealed class CavalryRoleCatalogTests
{
    [Theory]
    [InlineData("HighMedieval", "Frankish Separatists", "heavy_cavalry", "mk_fre_t1_chevaliers_mounted")]
    [InlineData("LateMedieval", "Franks", "heavy_cavalry", "mk_fre_t2_chevaliers_mounted")]
    [InlineData("Renaissance", "Kingdom of Sicily", "heavy_cavalry", "mk_sic_t3_condo_maa")]
    [InlineData("HighMedieval", "English Rebels", "light_cavalry", "mk_eng_t1_hobilars")]
    public void RegionalEraChoicesAreExplicit(string era, string faction, string category, string expected) =>
        Assert.Equal(expected, Assert.Single(CavalryRoleCatalog.Candidates("OfficialCC_DefaultCK3_" + era + "_MK1212Mod", faction, category, "conrois")));

    [Fact]
    public void CatalogDoesNotGuessUnknownCulturesOrReplaceBowHybridsWithKnights()
    {
        const string high = "OfficialCC_DefaultCK3_HighMedieval_MK1212Mod";
        Assert.Empty(CavalryRoleCatalog.Candidates(high, "Unknown French Name", "heavy_cavalry", "conrois"));
        Assert.Empty(CavalryRoleCatalog.Candidates(high, "French Rebels", "heavy_cavalry", "cataphract_archers"));
        Assert.Empty(CavalryRoleCatalog.Candidates(high, "French Rebels", "archer_cavalry", "horse_archers"));
        Assert.Empty(CavalryRoleCatalog.Candidates("custom_" + high, "French Rebels", "heavy_cavalry", "conrois"));
        Assert.Empty(CavalryRoleCatalog.Candidates("OfficialCC_DefaultCK3_Renaissance_MK1212Mod", "Holy Roman Empire", "light_cavalry", "light_horsemen"));
        Assert.Empty(CavalryRoleCatalog.Candidates(high, "German Rebels", "heavy_cavalry", "conrois"));
        Assert.Empty(CavalryRoleCatalog.Candidates("OfficialCC_DefaultCK3_LateMedieval_MK1212Mod", "French Rebels", "light_cavalry", "light_horsemen"));
    }

    [Fact]
    public void ReviewedRoleIsIndependentOfTrainingWeightAndPreservesHeavyBowContract()
    {
        var horse = new AttilaUnitSemanticStats { Category = "cavalry", MountKey = "reviewed", MountEntityType = "giant", Weight = "trained",
            NominalSoldiers = 70, MountCount = 55, EntityHitPoints = 140, MountHitPoints = 95, HasVisualVariant = true,
            CavalryAdapter = new("test", "heavy", 70, 55, 70, 70, "fixture") };
        Assert.Null(NativeMappingPolicy.RoleMismatch("heavy_cavalry", horse, "conrois"));
        Assert.Null(NativeMappingPolicy.RepresentationIssue(horse));
        Assert.NotNull(NativeMappingPolicy.RoleMismatch("light_cavalry", horse));
        Assert.NotNull(NativeMappingPolicy.RoleMismatch("heavy_cavalry", horse, "cataphract_archers"));
        var gun = horse with { MissileWeaponKey = "gun", MissilePrecursor = false, Ammunition = 10 };
        Assert.NotNull(NativeMappingPolicy.RoleMismatch("heavy_cavalry", gun, "cataphract_archers"));
        var bow = gun with { MissileWeaponKey = "bow", CavalryAdapter = horse.CavalryAdapter! with { Role = "heavy,archer" } };
        Assert.Null(NativeMappingPolicy.RoleMismatch("heavy_cavalry", bow, "cataphract_archers"));
    }
}
