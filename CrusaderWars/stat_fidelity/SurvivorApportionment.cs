using System;
using System.Collections.Generic;
using System.IO;

namespace CrusaderWars.StatFidelity;

/// <summary>Apportions an aggregate survivor count without increasing any source chunk.</summary>
public static class SurvivorApportionment
{
    public static int[] Allocate(IReadOnlyList<int> originalCounts, long desiredSurvivors)
    {
        if (originalCounts == null) throw new InvalidDataException("Source survivor capacities are missing.");
        var capacities = new int[originalCounts.Count];
        long total = 0;
        for (int i = 0; i < capacities.Length; i++)
        {
            int count = originalCounts[i];
            if (count < 0) throw new InvalidDataException("Source survivor capacity cannot be negative.");
            capacities[i] = count;
            total = checked(total + count);
        }
        if (desiredSurvivors < 0 || desiredSurvivors > total)
            throw new InvalidDataException("Aggregate survivors must be between zero and the original total.");
        if (desiredSurvivors == total) return capacities;
        var result = new int[capacities.Length];
        if (desiredSurvivors == 0) return result;

        // A long aggregate times an int capacity can exceed Int64. Decimal keeps
        // these integer products exact; modulo gives exact rational remainders.
        // Removing the remainder before division avoids rounded quotient edges.
        var fractions = new List<(int Index, decimal Remainder)>();
        long assigned = 0;
        for (int i = 0; i < capacities.Length; i++)
        {
            decimal numerator = (decimal)capacities[i] * desiredSurvivors;
            decimal remainder = numerator % total;
            int whole = checked((int)((numerator - remainder) / total));
            result[i] = whole;
            assigned = checked(assigned + whole);
            if (remainder > 0) fractions.Add((i, remainder));
        }
        fractions.Sort((left, right) => {
            int comparison = right.Remainder.CompareTo(left.Remainder);
            return comparison != 0 ? comparison : left.Index.CompareTo(right.Index);
        });
        long remaining = desiredSurvivors - assigned;
        if (remaining > fractions.Count)
            throw new InvalidDataException("Survivor remainder cannot be allocated within the source capacities.");
        for (int i = 0; i < remaining; i++)
        {
            int index = fractions[i].Index;
            if (result[index] >= capacities[index])
                throw new InvalidDataException("Survivor apportionment would increase a source chunk.");
            result[index]++;
        }
        return result;
    }
}
