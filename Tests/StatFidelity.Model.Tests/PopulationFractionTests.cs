using System.Numerics;
using CrusaderWars.StatFidelity;
using Xunit;

public sealed class PopulationFractionTests
{
    private static PopulationFraction F(int n, int d = 1) => PopulationFraction.FromInt(n) / PopulationFraction.FromInt(d);

    [Theory]
    [InlineData("0")] [InlineData("382.85451")] [InlineData("-0.00001")]
    [InlineData("79228162514264337593543950335")] [InlineData("-79228162514264337593543950335")]
    public void DecimalInputAndDiagnosticRoundTripAreExactWhenRepresentable(string text)
    {
        decimal input = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var value = PopulationFraction.FromDecimal(input);
        Assert.Equal(input, value.ToDecimal());
        Assert.True(value.Denominator > 0);
        Assert.Equal(BigInteger.One, BigInteger.GreatestCommonDivisor(BigInteger.Abs(value.Numerator), value.Denominator));
    }

    [Fact]
    public void ThirdsDoNotLoseFiveDecimalPrecisionWhenThreeHalfSurvivorsAreSummed()
    {
        var third = F(1, 3);
        var remaining = third * F(1, 2) + third * F(1, 2) + third * F(1, 2);
        Assert.Equal(F(1, 2), remaining);
        Assert.Equal(0.5m, remaining.TruncateNative5());
        Assert.Equal(1, remaining.CeilingInt32());
        Assert.Equal(F(1), third + third + third);
    }

    [Fact]
    public void ArbitrarilyLargeIntermediateProductsCancelBeforeDiagnosticConversion()
    {
        var large = PopulationFraction.FromDecimal(decimal.MaxValue);
        var product = large * large * large;
        Assert.Equal(large, product / large / large);
        Assert.Equal(decimal.MaxValue, (product / large / large).ToDecimal());
        Assert.Throws<OverflowException>(() => product.ToDecimal());
        Assert.Throws<OverflowException>(() => large.CeilingInt32());
    }

    [Theory]
    [InlineData(2, 3, 0, 1)] [InlineData(-2, 3, -1, 0)]
    [InlineData(6, 3, 2, 2)] [InlineData(-6, 3, -2, -2)]
    public void IntegerBoundsUseMathematicalFloorAndCeiling(int n, int d, int floor, int ceiling)
    {
        Assert.Equal(floor, F(n, d).FloorInt32()); Assert.Equal(ceiling, F(n, d).CeilingInt32());
    }

    [Fact]
    public void NativePrecisionTruncatesOnlyAfterExactAggregation()
    {
        Assert.Equal(0.99999m, F(999999, 1000000).TruncateNative5());
        Assert.Equal(1m, (F(999999, 1000000) + F(1, 1000000)).TruncateNative5());
        Assert.Throws<ArgumentOutOfRangeException>(() => F(-1, 2).TruncateNative5());
        Assert.Throws<DivideByZeroException>(() => F(1, 0));
    }

    [Fact]
    public void DefaultZeroAndNormalizedComparisonsBehaveAsArithmeticIdentities()
    {
        Assert.Equal(F(0), default(PopulationFraction)); Assert.True(default(PopulationFraction).IsZero);
        Assert.Equal(F(1, 2).GetHashCode(), F(2, 4).GetHashCode());
        Assert.True(F(-1, 2) < F(0)); Assert.True(F(1, 3) < F(1, 2));
        Assert.Equal(F(-1, 2), F(1, -2)); Assert.Equal(F(1, 2), -F(-1, 2));
    }

    [Fact]
    public void ExactStockWeightsConservePartialPopulationAcrossManyDifferentChunkSplits()
    {
        var random = new Random(20261022);
        for (int trial = 0; trial < 500; trial++)
        {
            int[] stocks = Enumerable.Range(0, random.Next(2, 30)).Select(_ => random.Next(1, 10000)).ToArray();
            int total = stocks.Sum(), numerator = random.Next(0, 1001), denominator = 1000;
            var active = PopulationFraction.FromDecimal(total - 0.54321m);
            var ratio = F(numerator, denominator);
            var chunks = stocks.Select(stock => active * F(stock, total) * ratio).ToArray();
            var sum = chunks.Aggregate(PopulationFraction.Zero, (a, b) => a + b);
            Assert.Equal(active * ratio, sum);
            Assert.Equal((active * ratio).TruncateNative5(), sum.TruncateNative5());
        }
    }
}
