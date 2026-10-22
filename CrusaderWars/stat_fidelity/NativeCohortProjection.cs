using System;
using System.IO;

namespace CrusaderWars.StatFidelity;

/// <summary>One rounding operation after summing the raw evidence for a native population.</summary>
public static class NativeCohortProjection
{
    public static int Survivors(int nativeSoldiers, decimal deployedBodies, decimal remainingBodies)
    {
        if (nativeSoldiers < 0 || deployedBodies <= 0 || remainingBodies < 0 || remainingBodies > deployedBodies)
            throw new InvalidDataException("Invalid native cohort casualty projection.");
        return checked((int)Math.Round(nativeSoldiers * remainingBodies / deployedBodies, MidpointRounding.AwayFromZero));
    }
}
