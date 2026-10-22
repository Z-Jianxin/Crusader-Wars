using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CrusaderWars.StatFidelity;

/// <summary>Multipliers already normalized to the mapped unit's unmodified CK3 type.</summary>
public sealed record UnitStatProfile(string Id, string BaseUnitKey, double DamageMultiplier, double HitPointMultiplier,
    int? SiegeEngineCount = null, int? SiegeCrewCount = null);

public static partial class StatFidelityBackend
{
    public static string GeneratedKey(UnitStatProfile profile)
    {
        Validate(profile);
        string identity = string.Join("\n", profile.Id, profile.BaseUnitKey,
            profile.DamageMultiplier.ToString("R", CultureInfo.InvariantCulture),
            profile.HitPointMultiplier.ToString("R", CultureInfo.InvariantCulture));
        if (profile.SiegeEngineCount.HasValue)
            identity += "\nsiege-battery-v1\n" + profile.SiegeEngineCount.Value.ToString(CultureInfo.InvariantCulture)
                + "\n" + profile.SiegeCrewCount!.Value.ToString(CultureInfo.InvariantCulture);
        return "cc_sf_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
    }

    private static void Validate(UnitStatProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.BaseUnitKey))
            throw new InvalidOperationException("Stat fidelity requires a source identity and an Attila unit key.");
        foreach (double multiplier in new[] { profile.DamageMultiplier, profile.HitPointMultiplier })
            if (!double.IsFinite(multiplier) || multiplier <= 0)
                throw new InvalidOperationException($"Stat fidelity profile '{profile.Id}' has an invalid multiplier ({multiplier}). Expected a finite positive value.");
        if (profile.SiegeEngineCount.HasValue != profile.SiegeCrewCount.HasValue
            || (profile.SiegeEngineCount.HasValue && (profile.SiegeEngineCount <= 0 || profile.SiegeCrewCount <= 0
                || profile.DamageMultiplier != 1 || profile.HitPointMultiplier != 1)))
            throw new InvalidOperationException("Siege battery variants require positive paired engine/crew counts and unchanged combat stats.");
    }
}
