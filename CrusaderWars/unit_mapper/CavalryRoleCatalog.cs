using System;
using System.Collections.Generic;
using System.IO;

namespace CrusaderWars.unit_mapper;

/// <summary>Explicit regional/era choices from installed Earl assets, never a culture-name heuristic.</summary>
public static class CavalryRoleCatalog
{
    public static IReadOnlyList<string> Candidates(string? mapperFolder, string faction, string nativeCategory, string nativeType)
    {
        string era = Path.GetFileName(mapperFolder?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "");
        int tier = era switch {
            "OfficialCC_DefaultCK3_HighMedieval_MK1212Mod" => 1,
            "OfficialCC_DefaultCK3_LateMedieval_MK1212Mod" => 2,
            "OfficialCC_DefaultCK3_Renaissance_MK1212Mod" => 3,
            _ => 0
        };
        if (tier == 0 || nativeType == "cataphract_archers") return Array.Empty<string>();
        // These are exact effective mapper factions, including its historical aliases.
        string? region = faction switch {
            "French Rebels" or "Frankish Separatists" or "Occitan Rebels" or "Franks" or "Kingdom of France" or "County of Toulouse" => "fre",
            "English Rebels" or "Kingdom of England" => "eng",
            "Sicilian Pretenders" or "Kingdom of Sicily" => "sic",
            _ => null
        };
        if (region == null) return Array.Empty<string>();
        string prefix = "mk_" + region + "_t" + tier + "_";
        string? suffix = (region, nativeCategory) switch {
            ("fre", "heavy_cavalry") => tier == 3 ? "gendarmes" : "chevaliers_mounted",
            ("eng", "heavy_cavalry") => "knights",
            ("sic", "heavy_cavalry") => tier == 3 ? "condo_maa" : "knights",
            ("fre", "light_cavalry") when tier == 1 => "mounted_sergeants",
            ("eng", "light_cavalry") => "hobilars",
            ("sic", "light_cavalry") => "mounted_serjeants",
            // Do not turn regional mounted crossbowmen or dragoons into CK3 horse archers.
            _ => null
        };
        return suffix == null ? Array.Empty<string>() : new[] { prefix + suffix };
    }
}
