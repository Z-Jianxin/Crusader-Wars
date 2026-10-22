using CrusaderWars.StatFidelity;
using Xunit;

public sealed class SurvivorApportionmentTests
{
    [Fact]
    public void OneBodySourceCannotAbsorbAnUnboundedRoundingRemainder()
    {
        // Native source 9656:index2 had current/max1 but the old final-slot
        // remainder correction produced2. Four one-body slots reproduce that
        // failure mode: independently rounding0.5 to0 leaves residue2.
        int[] original = [1, 1, 1, 1];
        Assert.Equal(new[] { 1, 1, 0, 0 }, SurvivorApportionment.Allocate(original, 2));
        Assert.Equal(new[] { 1, 1, 1, 1 }, original);
    }

    [Fact]
    public void StableRemaindersRespectInputOrderAndZeroCapacities()
    {
        Assert.Equal(new[] { 1, 0, 0 }, SurvivorApportionment.Allocate([1, 1, 1], 1));
        Assert.Equal(new[] { 1, 1 }, SurvivorApportionment.Allocate([1, 3], 2));
        Assert.Equal(new[] { 0, 1 }, SurvivorApportionment.Allocate([1, 3], 1));
        Assert.Equal(new[] { 0, 1, 0, 1 }, SurvivorApportionment.Allocate([0, 1, 0, 2], 2));
    }

    [Fact]
    public void FullAndZeroAggregatesPreserveBoundsAndReturnIndependentArrays()
    {
        int[] original = [0, 7, 1];
        var full = SurvivorApportionment.Allocate(original, 8);
        Assert.Equal(original, full);
        Assert.NotSame(original, full);
        full[1] = 0;
        Assert.Equal(7, original[1]);
        Assert.Equal(new[] { 0, 0, 0 }, SurvivorApportionment.Allocate(original, 0));
        Assert.Empty(SurvivorApportionment.Allocate([], 0));
        Assert.Equal(new[] { 0, 0 }, SurvivorApportionment.Allocate([0, 0], 0));
    }

    [Fact]
    public void AggregateAndProductsMayExceedInt32AndInt64WithoutLosingPrecision()
    {
        int[] capacities = [int.MaxValue, int.MaxValue, 1];
        Assert.Equal(new[] { 1073741824, 1073741824, 0 }, SurvivorApportionment.Allocate(capacities, (long)int.MaxValue + 1));
        Assert.Equal(new[] { int.MaxValue, int.MaxValue - 1, 1 }, SurvivorApportionment.Allocate(capacities, (long)int.MaxValue * 2));
        // The first product exceeds Int64, even though each source count is int.
        int[] many = Enumerable.Repeat(int.MaxValue, 10).ToArray();
        long desired = many.Sum(x => (long)x) - 1;
        var result = SurvivorApportionment.Allocate(many, desired);
        Assert.Equal(desired, result.Sum(x => (long)x));
        Assert.Equal(int.MaxValue - 1, result[^1]);
        Assert.All(result[..^1], count => Assert.Equal(int.MaxValue, count));
    }

    [Fact]
    public void InvalidAggregatesAndCapacitiesAreRejectedRatherThanClamped()
    {
        Assert.Throws<InvalidDataException>(() => SurvivorApportionment.Allocate(null!, 0));
        Assert.Throws<InvalidDataException>(() => SurvivorApportionment.Allocate([1, -1], 0));
        Assert.Throws<InvalidDataException>(() => SurvivorApportionment.Allocate([2, 3], -1));
        Assert.Throws<InvalidDataException>(() => SurvivorApportionment.Allocate([2, 3], 6));
        Assert.Throws<InvalidDataException>(() => SurvivorApportionment.Allocate([], 1));
        Assert.Throws<InvalidDataException>(() => SurvivorApportionment.Allocate([0, 0], 1));
        Assert.Throws<InvalidDataException>(() => SurvivorApportionment.Allocate([int.MaxValue], long.MaxValue));
    }

    [Fact]
    public void SmallCapacityCombinationsAlwaysHaveExactSumBoundsAndQuotaErrorBelowOne()
    {
        for (int a = 0; a <= 5; a++)
        for (int b = 0; b <= 5; b++)
        for (int c = 0; c <= 5; c++)
        {
            int[] counts = [a, b, c];
            int total = a + b + c;
            for (int desired = 0; desired <= total; desired++)
            {
                var allocated = SurvivorApportionment.Allocate(counts, desired);
                Assert.Equal(desired, allocated.Sum());
                for (int i = 0; i < counts.Length; i++)
                {
                    Assert.InRange(allocated[i], 0, counts[i]);
                    if (total != 0) Assert.True(Math.Abs(allocated[i] - (decimal)counts[i] * desired / total) < 1);
                }
            }
        }
    }
}
