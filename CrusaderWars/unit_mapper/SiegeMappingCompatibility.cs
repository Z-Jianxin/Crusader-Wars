using System;

namespace CrusaderWars.unit_mapper;

/// <summary>A single reviewed Early919 mapper correction, not a generic cross-faction fallback.</summary>
public static class SiegeMappingCompatibility
{
    public const string ReplacementKey = "bel_ost_cheiroballista";
    public const string Provenance = "OfficialCC_DefaultCK3_EarlyMedieval_Factions.xml: Imazigen trebuchet and Default onager";

    public static string? Resolve(string? playthrough, string? mapper, string faction, string regimentType,
        string mappedKey, string originalKey, bool hasExplicitReplacement, Func<string, bool> isSupportedArtillery)
    {
        return !hasExplicitReplacement && playthrough == "DefaultCK3" && mapper == DefaultCk3EraPolicy.EarlyMapper
            && faction == "Imazigen" && regimentType == "onager" && mappedKey == "SUEB_skiltfolk_sword"
            && originalKey == mappedKey && !isSupportedArtillery(mappedKey) ? ReplacementKey : null;
    }
}
