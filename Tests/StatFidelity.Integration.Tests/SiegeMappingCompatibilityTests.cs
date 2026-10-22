using CrusaderWars.unit_mapper;
using Xunit;

public sealed class SiegeMappingCompatibilityTests
{
    [Fact]
    public void OnlyTheReviewedAutomaticEarlyImazigenOnagerMappingIsCorrected()
    {
        string? Resolve(string playthrough = "DefaultCK3", string mapper = DefaultCk3EraPolicy.EarlyMapper,
            string faction = "Imazigen", string type = "onager", string key = "SUEB_skiltfolk_sword",
            string original = "SUEB_skiltfolk_sword", bool replaced = false, bool validActiveArtillery = false) =>
            SiegeMappingCompatibility.Resolve(playthrough, mapper, faction, type, key, original, replaced, _ => validActiveArtillery);
        Assert.Equal("bel_ost_cheiroballista", Resolve());
        Assert.Null(Resolve(playthrough: "Custom"));
        Assert.Null(Resolve(mapper: DefaultCk3EraPolicy.HighMapper));
        Assert.Null(Resolve(faction: "Other"));
        Assert.Null(Resolve(type: "mangonel"));
        Assert.Null(Resolve(key: "manual_key"));
        Assert.Null(Resolve(original: "another_original"));
        Assert.Null(Resolve(replaced: true));
        Assert.Null(Resolve(validActiveArtillery: true));
    }

    [Fact]
    public void ValidActiveRedefinitionIsPreservedAndManualChoicesAreNotQueriedForCorrection()
    {
        int queries = 0;
        bool Valid(string key) { Assert.Equal("SUEB_skiltfolk_sword", key); queries++; return true; }
        Assert.Null(SiegeMappingCompatibility.Resolve("DefaultCK3", DefaultCk3EraPolicy.EarlyMapper, "Imazigen", "onager",
            "SUEB_skiltfolk_sword", "SUEB_skiltfolk_sword", false, Valid));
        Assert.Equal(1, queries);
        Assert.Null(SiegeMappingCompatibility.Resolve("DefaultCK3", DefaultCk3EraPolicy.EarlyMapper, "Imazigen", "onager",
            "SUEB_skiltfolk_sword", "SUEB_skiltfolk_sword", true, _ => throw new Exception("Manual mappings cannot be corrected.")));
    }
}
