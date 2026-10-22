using System;
using System.Globalization;
using System.Numerics;

namespace CrusaderWars.StatFidelity;

/// <summary>Exact population arithmetic. Decimal conversion is diagnostic only.</summary>
public readonly struct PopulationFraction : IComparable<PopulationFraction>, IEquatable<PopulationFraction>
{
    private readonly BigInteger numerator;
    private readonly BigInteger denominator;
    public BigInteger Numerator => numerator;
    // The default struct is the additive identity too.
    public BigInteger Denominator => denominator.IsZero ? BigInteger.One : denominator;
    public bool IsZero => numerator.IsZero;
    public static PopulationFraction Zero => default;

    private PopulationFraction(BigInteger value, BigInteger divisor)
    {
        if (divisor.IsZero) throw new DivideByZeroException("Population fraction has a zero denominator.");
        if (divisor.Sign < 0) { value = -value; divisor = -divisor; }
        if (value.IsZero) { numerator = BigInteger.Zero; denominator = BigInteger.One; return; }
        BigInteger common = BigInteger.GreatestCommonDivisor(BigInteger.Abs(value), divisor);
        numerator = value / common;
        denominator = divisor / common;
    }

    public static PopulationFraction FromInt(int value) => new(value, BigInteger.One);
    public static PopulationFraction FromDecimal(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger coefficient = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        if ((bits[3] & int.MinValue) != 0) coefficient = -coefficient;
        int scale = (bits[3] >> 16) & 0xff;
        return new(coefficient, BigInteger.Pow(10, scale));
    }

    public static PopulationFraction operator +(PopulationFraction a, PopulationFraction b) =>
        new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    public static PopulationFraction operator -(PopulationFraction a, PopulationFraction b) =>
        new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    public static PopulationFraction operator -(PopulationFraction a) => new(-a.Numerator, a.Denominator);
    public static PopulationFraction operator *(PopulationFraction a, PopulationFraction b) =>
        new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
    public static PopulationFraction operator /(PopulationFraction a, PopulationFraction b) =>
        new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);

    public int CompareTo(PopulationFraction other) =>
        (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
    public bool Equals(PopulationFraction other) => Numerator == other.Numerator && Denominator == other.Denominator;
    public override bool Equals(object? other) => other is PopulationFraction fraction && Equals(fraction);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    public static bool operator ==(PopulationFraction a, PopulationFraction b) => a.Equals(b);
    public static bool operator !=(PopulationFraction a, PopulationFraction b) => !a.Equals(b);
    public static bool operator <(PopulationFraction a, PopulationFraction b) => a.CompareTo(b) < 0;
    public static bool operator >(PopulationFraction a, PopulationFraction b) => a.CompareTo(b) > 0;
    public static bool operator <=(PopulationFraction a, PopulationFraction b) => a.CompareTo(b) <= 0;
    public static bool operator >=(PopulationFraction a, PopulationFraction b) => a.CompareTo(b) >= 0;

    public int FloorInt32()
    {
        BigInteger whole = BigInteger.DivRem(Numerator, Denominator, out BigInteger remainder);
        if (Numerator.Sign < 0 && !remainder.IsZero) whole--;
        return checked((int)whole);
    }
    public int CeilingInt32()
    {
        BigInteger whole = BigInteger.DivRem(Numerator, Denominator, out BigInteger remainder);
        if (Numerator.Sign > 0 && !remainder.IsZero) whole++;
        return checked((int)whole);
    }
    public decimal TruncateNative5()
    {
        if (Numerator.Sign < 0) throw new ArgumentOutOfRangeException(nameof(Numerator), "Native population cannot be negative.");
        BigInteger scaled = Numerator * 100000 / Denominator;
        return checked((decimal)scaled) / 100000m;
    }

    /// <summary>Truncated diagnostic approximation; never use this for population rounding.</summary>
    public decimal ToDecimal()
    {
        BigInteger magnitude = BigInteger.Abs(Numerator);
        BigInteger coefficientLimit = (BigInteger.One << 96) - 1;
        int scale = 28;
        BigInteger power = BigInteger.Pow(10, scale);
        BigInteger coefficient = magnitude * power / Denominator;
        while (coefficient > coefficientLimit && scale > 0)
        {
            scale--; power /= 10;
            coefficient = magnitude * power / Denominator;
        }
        if (coefficient > coefficientLimit) throw new OverflowException("Population diagnostic is outside Decimal bounds.");
        uint low = (uint)(coefficient & uint.MaxValue), middle = (uint)((coefficient >> 32) & uint.MaxValue), high = (uint)((coefficient >> 64) & uint.MaxValue);
        return new decimal(unchecked((int)low), unchecked((int)middle), unchecked((int)high), Numerator.Sign < 0, (byte)scale);
    }
    public override string ToString() => Numerator.ToString(CultureInfo.InvariantCulture) + "/" + Denominator.ToString(CultureInfo.InvariantCulture);
}
