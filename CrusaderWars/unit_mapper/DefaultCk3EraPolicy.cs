using System;

namespace CrusaderWars.unit_mapper;

public readonly record struct MapperEraRange(int StartYear, int EndYear)
{
    public bool Contains(int year) => year >= StartYear && year <= EndYear;
}

/// <summary>
/// The official DefaultCK3 919/1212 boundary is source policy, so a mapper refresh
/// cannot reintroduce the inclusive 1129/1130 overlap. Other ranges remain configured.
/// </summary>
public static class DefaultCk3EraPolicy
{
    public const int HighMedievalStartYear = 1130;
    public const string EarlyMapper = "OfficialCC_DefaultCK3_EarlyMedieval_919Mod";
    public const string HighMapper = "OfficialCC_DefaultCK3_HighMedieval_MK1212Mod";

    public static MapperEraRange GetEffectiveRange(string? playthroughTag, string? mapperFolderName,
        int configuredStartYear, int configuredEndYear)
    {
        if (!string.Equals(playthroughTag, "DefaultCK3", StringComparison.Ordinal) || string.IsNullOrEmpty(mapperFolderName))
            return new(configuredStartYear, configuredEndYear);

        // Accept a folder name or path without depending on the host path separator.
        string folder = mapperFolderName.Replace('\\', '/').TrimEnd('/');
        folder = folder[(folder.LastIndexOf('/') + 1)..];
        if (string.Equals(folder, EarlyMapper, StringComparison.OrdinalIgnoreCase))
            return new(configuredStartYear, Math.Min(configuredEndYear, HighMedievalStartYear - 1));
        if (string.Equals(folder, HighMapper, StringComparison.OrdinalIgnoreCase))
            return new(Math.Max(configuredStartYear, HighMedievalStartYear), configuredEndYear);
        return new(configuredStartYear, configuredEndYear);
    }

    public static bool IncludesYear(string? playthroughTag, string? mapperFolderName,
        int configuredStartYear, int configuredEndYear, int year) =>
        GetEffectiveRange(playthroughTag, mapperFolderName, configuredStartYear, configuredEndYear).Contains(year);
}
