using CrusaderWars.unit_mapper;
using Xunit;

public sealed class DefaultCk3EraPolicyTests
{
    private static readonly (string Folder, int Start, int End)[] CurrentDefaultRanges = [
        (DefaultCk3EraPolicy.EarlyMapper, 768, 1130),
        (DefaultCk3EraPolicy.HighMapper, 1129, 1259),
        ("OfficialCC_DefaultCK3_LateMedieval_MK1212Mod", 1260, 1369),
        ("OfficialCC_DefaultCK3_Renaissance_MK1212Mod", 1370, 2000)
    ];

    [Theory]
    [InlineData(1128, 0)]
    [InlineData(1129, 0)]
    [InlineData(1130, 1)]
    [InlineData(1131, 1)]
    [InlineData(1259, 1)]
    [InlineData(1260, 2)]
    [InlineData(1369, 2)]
    [InlineData(1370, 3)]
    [InlineData(2000, 3)]
    public void CurrentDefaultCatalogHasExactlyTheExpectedEraAtEachBoundary(int year, int expected)
    {
        var selected = CurrentDefaultRanges.Select((range, index) => (range, index))
            .Where(item => DefaultCk3EraPolicy.IncludesYear("DefaultCK3", item.range.Folder, item.range.Start, item.range.End, year))
            .Select(item => item.index).ToArray();
        Assert.Equal(new[] { expected }, selected);
    }

    [Fact]
    public void EveryConfiguredDefaultYearHasExactlyOneEraWithoutChangingLateOrRenaissance()
    {
        for (int year = 768; year <= 2000; year++)
            Assert.Single(CurrentDefaultRanges.Where(range => DefaultCk3EraPolicy.IncludesYear("DefaultCK3", range.Folder, range.Start, range.End, year)));
        Assert.DoesNotContain(CurrentDefaultRanges, range => DefaultCk3EraPolicy.IncludesYear("DefaultCK3", range.Folder, range.Start, range.End, 767));
        Assert.DoesNotContain(CurrentDefaultRanges, range => DefaultCk3EraPolicy.IncludesYear("DefaultCK3", range.Folder, range.Start, range.End, 2001));
        Assert.Equal(new MapperEraRange(1260, 1369), DefaultCk3EraPolicy.GetEffectiveRange("DefaultCK3", CurrentDefaultRanges[2].Folder, 1260, 1369));
        Assert.Equal(new MapperEraRange(1370, 2000), DefaultCk3EraPolicy.GetEffectiveRange("DefaultCK3", CurrentDefaultRanges[3].Folder, 1370, 2000));
    }

    [Theory]
    [InlineData("Custom")]
    [InlineData("TheFallenEagle")]
    [InlineData("BookmarksPlus")]
    [InlineData("AGOT")]
    [InlineData("RealmsInExile")]
    [InlineData(null)]
    public void OtherPlaythroughsRetainConfiguredRangesEvenWithAnOfficialFolderName(string? tag)
    {
        Assert.Equal(new MapperEraRange(1000, 1400), DefaultCk3EraPolicy.GetEffectiveRange(tag, DefaultCk3EraPolicy.EarlyMapper, 1000, 1400));
        Assert.Equal(new MapperEraRange(1000, 1400), DefaultCk3EraPolicy.GetEffectiveRange(tag, DefaultCk3EraPolicy.HighMapper, 1000, 1400));
    }

    [Fact]
    public void CustomFolderNamesAndUnrelatedBoundsAreNotReinterpreted()
    {
        Assert.Equal(new MapperEraRange(700, 1500), DefaultCk3EraPolicy.GetEffectiveRange("DefaultCK3", "Custom_EarlyMedieval", 700, 1500));
        Assert.Equal(new MapperEraRange(800, 1129), DefaultCk3EraPolicy.GetEffectiveRange("DefaultCK3", DefaultCk3EraPolicy.EarlyMapper, 800, 1400));
        Assert.Equal(new MapperEraRange(1130, 1240), DefaultCk3EraPolicy.GetEffectiveRange("DefaultCK3", DefaultCk3EraPolicy.HighMapper, 900, 1240));
        Assert.Equal(new MapperEraRange(800, 1100), DefaultCk3EraPolicy.GetEffectiveRange("DefaultCK3", DefaultCk3EraPolicy.EarlyMapper, 800, 1100));
        Assert.Equal(new MapperEraRange(1150, 1240), DefaultCk3EraPolicy.GetEffectiveRange("DefaultCK3", DefaultCk3EraPolicy.HighMapper, 1150, 1240));
    }

    [Fact]
    public void WindowsOrPortableFolderPathsUseTheSamePolicy()
    {
        Assert.Equal(new MapperEraRange(768, 1129), DefaultCk3EraPolicy.GetEffectiveRange("DefaultCK3", "C:\\runtime\\unit mappers\\" + DefaultCk3EraPolicy.EarlyMapper + "\\", 768, 1130));
        Assert.Equal(new MapperEraRange(1130, 1259), DefaultCk3EraPolicy.GetEffectiveRange("DefaultCK3", "/runtime/unit mappers/" + DefaultCk3EraPolicy.HighMapper.ToLowerInvariant() + "/", 1129, 1259));
        Assert.False(DefaultCk3EraPolicy.IncludesYear("DefaultCK3", DefaultCk3EraPolicy.EarlyMapper, 1130, 1500, 1200));
    }
}
