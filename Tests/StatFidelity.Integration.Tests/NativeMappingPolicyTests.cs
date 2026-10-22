using System.Xml;
using CrusaderWars.StatFidelity;
using CrusaderWars.unit_mapper;
using Xunit;

public sealed class NativeMappingPolicyTests
{
    private static AttilaUnitSemanticStats Foot => new() { UnitKey = "foot", Category = "inf_melee", UnitClass = "inf_mel",
        NominalSoldiers = 100, ManEntityType = "man", EntityHitPoints = 100, HasVisualVariant = true };
    private static AttilaUnitSemanticStats Horse => Foot with { UnitKey = "horse", Category = "cavalry", UnitClass = "cav_mel",
        MountKey = "horse", MountEntityType = "horse", MountCount = 100, MountHitPoints = 50, Weight = "heavy" };

    [Fact]
    public void DefiniteFootMountedAndRangedMismatchesAreDistinguishedFromUnsupportedRepresentation()
    {
        Assert.NotNull(NativeMappingPolicy.RoleMismatch("heavy_infantry", Horse));
        Assert.NotNull(NativeMappingPolicy.RoleMismatch("heavy_cavalry", Foot));
        Assert.NotNull(NativeMappingPolicy.RoleMismatch("archers", Foot));
        Assert.Null(NativeMappingPolicy.RoleMismatch("heavy_cavalry", Horse));
        var earlMismatch = Horse with { NominalSoldiers = 120, MountCount = 70 };
        Assert.Null(NativeMappingPolicy.RoleMismatch("heavy_cavalry", earlMismatch));
        Assert.Equal("unsupported-body-or-hitpoint-representation", NativeMappingPolicy.RepresentationIssue(earlMismatch));
        Assert.Equal("missing-active-visual-variant", NativeMappingPolicy.RepresentationIssue(Foot with { HasVisualVariant = false }));
    }

    [Fact]
    public void ActualMissileFieldsAllowEarlFootBowCategoryAndProtectHeavyArcherHybrids()
    {
        var bow = Foot with { UnitClass = "inf_mis", MissileWeaponKey = "longbow", MissilePrecursor = false, Ammunition = 25 };
        Assert.Null(NativeMappingPolicy.RoleMismatch("archers", bow));
        Assert.NotNull(NativeMappingPolicy.RoleMismatch("archers", bow with { MissilePrecursor = true }));
        Assert.NotNull(NativeMappingPolicy.RoleMismatch("heavy_cavalry", Horse, "cataphract_archers"));
        Assert.Null(NativeMappingPolicy.RoleMismatch("heavy_cavalry", Horse with { MissileWeaponKey = "bow", MissilePrecursor = false, Ammunition = 10 }, "cataphract_archers"));
        Assert.NotNull(NativeMappingPolicy.RoleMismatch("heavy_cavalry", Horse with { MissileWeaponKey = "bow", MissilePrecursor = false, Ammunition = 10 }, "conrois"));
        Assert.Null(NativeMappingPolicy.RoleMismatch("heavy_cavalry", Horse with { MissileWeaponKey = "javelin", MissilePrecursor = true, Ammunition = 1 }, "conrois"));
        Assert.Null(NativeMappingPolicy.RoleMismatch("heavy_cavalry", Horse with { Weight = "super_heavy" }));
        Assert.NotNull(NativeMappingPolicy.RoleMismatch("heavy_cavalry", Horse with { Weight = "very_light" }));
    }

    [Theory]
    [InlineData("gunpowder")]
    [InlineData("camel_cavalry")]
    [InlineData("elephant_cavalry")]
    [InlineData("nomadic_horde")]
    public void UncontractedNativeFamiliesNeverFallBackToFootOrBow(string category)
    {
        Assert.Null(NativeMappingPolicy.CohortCategory(category));
        Assert.StartsWith("unsupported-native-family:", NativeMappingPolicy.RoleMismatch(category, Foot)!);
    }

    [Fact]
    public void PartialDefaultsReplaceOnlyTheirDeclaredTypeOrRolePoolWithoutMutatingAuthors()
    {
        var first = new XmlDocument(); first.LoadXml("<Faction name='Default'><General key='general'/><Knights key='knight'/><MenAtArm type='bowmen' key='bows'/><MenAtArm type='armored_horsemen' key='horse'/></Faction>");
        var partial = new XmlDocument(); partial.LoadXml("<Faction name='Default'><MenAtArm type='bowmen' key='new_bows'/></Faction>");
        var empty = new XmlDocument(); empty.LoadXml("<Faction name='Default'/>");
        string original = first.OuterXml;
        var merged = NativeMappingPolicy.MergeDefaultBlocks([first.DocumentElement!, partial.DocumentElement!, empty.DocumentElement!]);
        Assert.Equal("general", merged.SelectSingleNode("General")!.Attributes!["key"]!.Value);
        Assert.Equal("knight", merged.SelectSingleNode("Knights")!.Attributes!["key"]!.Value);
        Assert.Equal("horse", merged.SelectSingleNode("MenAtArm[@type='armored_horsemen']")!.Attributes!["key"]!.Value);
        Assert.Equal("new_bows", merged.SelectSingleNode("MenAtArm[@type='bowmen']")!.Attributes!["key"]!.Value);
        Assert.Equal(original, first.OuterXml);
    }

    [Theory]
    [InlineData("heavy_cavalry", "CAVALRY")]
    [InlineData("light_cavalry", "CAVALRY")]
    [InlineData("archer_cavalry", "CAVALRY")]
    [InlineData("archers", "RANGED")]
    [InlineData("heavy_infantry", "INFANTRY")]
    public void CohortMaximumFamilyComesFromNativeDefinition(string native, string expected) =>
        Assert.Equal(expected, NativeMappingPolicy.CohortCategory(native));
}
