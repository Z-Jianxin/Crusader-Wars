using System;
using System.Collections.Generic;
using System.IO;

namespace CrusaderWars.StatFidelity;

public sealed record SiegeCrewResult(int SourceMachines, int DeployedBodies, int RemainingBodies);
public sealed record SiegeSourceBudget(string ArmyId, string ArmyRegimentId, string RegimentId, string ChunkIndex, int OriginalMachines)
{
    public string Identity => Key(ArmyId, ArmyRegimentId, RegimentId, ChunkIndex);
    public static string Key(string army, string armyRegiment, string regiment, string? chunk) =>
        army + ":" + armyRegiment + ":" + regiment + ":" + (string.IsNullOrEmpty(chunk) ? "0" : chunk);
}

/// <summary>Equipment retention modeled from actual crew survival, not inferred engine deaths.</summary>
public static class SiegeCasualtyAccounting
{
    public static int Survivors(int originalMachines, IReadOnlyList<SiegeCrewResult> formations)
    {
        if (originalMachines < 0 || formations == null) throw new InvalidDataException("Invalid native siege source budget.");
        long represented = 0;
        decimal survivors = 0;
        foreach (var formation in formations)
        {
            if (formation == null || formation.SourceMachines < 0 || formation.DeployedBodies <= 0 ||
                formation.RemainingBodies < 0 || formation.RemainingBodies > formation.DeployedBodies)
                throw new InvalidDataException("Invalid exact siege formation or survivor count.");
            represented = checked(represented + formation.SourceMachines);
            if (represented > originalMachines) throw new InvalidDataException("Deployed siege representation exceeds its native source budget.");
            survivors += (decimal)formation.SourceMachines * formation.RemainingBodies / formation.DeployedBodies;
        }
        // Round once per exact source. Equipment omitted from the battlefield is
        // untouched; an absent formation is not evidence that its source was lost.
        return checked(originalMachines - (int)represented + (int)decimal.Round(survivors, 0, MidpointRounding.AwayFromZero));
    }
}
