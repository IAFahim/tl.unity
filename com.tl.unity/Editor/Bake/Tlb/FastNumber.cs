using System.Diagnostics.CodeAnalysis;


namespace Tl.Gen.Tlb;

internal static class FastNumber
{
    private static readonly double[] Pow10Double = BuildPow10();
    private static readonly ulong[] Pow5U64 = BuildPow5();
    private static readonly ulong[] Pow5MaxMantissa = BuildPow5MaxMantissa();

    private static double[] BuildPow10()
    {
        var table = new double[23];
        var value = 1.0;
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = value;
            value *= 10.0;
        }
        return table;
    }

    private static ulong[] BuildPow5()
    {
        var table = new ulong[28];
        ulong value = 1;
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = value;
            value *= 5;
        }
        return table;
    }

    private static ulong[] BuildPow5MaxMantissa()
    {
        var table = new ulong[28];
        for (var i = 0; i < table.Length; i++)
            table[i] = ulong.MaxValue / Pow5U64[i];
        return table;
    }

    internal static bool ScanUnsigned32(byte[] utf8, int start, int bound, out int tokenEnd, out uint value)
    {
        tokenEnd = 0;
        value = 0;
        var i = start;
        while (i < bound && IsWhitespaceByte(utf8[i]))
            i++;
        if (i >= bound)
            return false;
        var first = utf8[i];
        if (first == (byte)'-')
            return false;
        if (first == (byte)'0')
        {
            i++;
            if (!TailWhitespace(utf8, i, bound))
                return false;
            tokenEnd = i;
            value = 0;
            return true;
        }
        ulong acc = 0;
        var digits = 0;
        while (i < bound && utf8[i] >= (byte)'0' && utf8[i] <= (byte)'9')
        {
            acc = acc * 10 + (ulong)(utf8[i] - (byte)'0');
            if (acc > uint.MaxValue)
                return false;
            i++;
            digits++;
        }
        if (digits == 0 || !TailWhitespace(utf8, i, bound))
            return false;
        tokenEnd = i;
        value = (uint)acc;
        return true;
    }

    internal static bool ScanSignedInteger(byte[] utf8, int start, int bound, bool allowNegative, long min, long max, out int tokenEnd, out long value)
    {
        tokenEnd = 0;
        value = 0;
        var i = start;
        while (i < bound && IsWhitespaceByte(utf8[i]))
            i++;
        if (i >= bound)
            return false;
        var negative = false;
        if (utf8[i] == (byte)'-')
        {
            if (!allowNegative)
                return false;
            negative = true;
            i++;
        }
        if (i >= bound || utf8[i] == (byte)'0')
        {
            if (i < bound && utf8[i] == (byte)'0')
            {
                i++;
                if (!TailWhitespace(utf8, i, bound))
                    return false;
                tokenEnd = i;
                value = 0;
                return !negative || allowNegative;
            }
            return false;
        }
        ulong acc = 0;
        var digits = 0;
        while (i < bound && utf8[i] >= (byte)'0' && utf8[i] <= (byte)'9')
        {
            var digit = (ulong)(utf8[i] - (byte)'0');
            if (acc > (ulong.MaxValue - digit) / 10)
                return false;
            acc = acc * 10 + digit;
            i++;
            digits++;
        }
        if (digits == 0 || !TailWhitespace(utf8, i, bound))
            return false;
        tokenEnd = i;
        if (negative)
        {
            if (acc > 0x8000000000000000UL)
                return false;
            var signed = acc == 0x8000000000000000UL ? long.MinValue : -(long)acc;
            if (signed < min)
                return false;
            value = signed;
            return true;
        }
        if (acc > (ulong)max)
            return false;
        value = (long)acc;
        return true;
    }

    internal static bool ScanUnsigned64(byte[] utf8, int start, int bound, out int tokenEnd, out ulong value)
    {
        tokenEnd = 0;
        value = 0;
        var i = start;
        while (i < bound && IsWhitespaceByte(utf8[i]))
            i++;
        if (i >= bound)
            return false;
        var first = utf8[i];
        if (first == (byte)'-')
            return false;
        if (first == (byte)'0')
        {
            i++;
            if (!TailWhitespace(utf8, i, bound))
                return false;
            tokenEnd = i;
            value = 0;
            return true;
        }
        ulong acc = 0;
        var digits = 0;
        while (i < bound && utf8[i] >= (byte)'0' && utf8[i] <= (byte)'9')
        {
            var digit = (ulong)(utf8[i] - (byte)'0');
            if (acc > (ulong.MaxValue - digit) / 10)
                return false;
            acc = acc * 10 + digit;
            i++;
            digits++;
        }
        if (digits == 0 || !TailWhitespace(utf8, i, bound))
            return false;
        tokenEnd = i;
        value = acc;
        return true;
    }

    internal static bool ScanNumberParts(byte[] utf8, int start, int bound, out int tokenEnd, out ulong mantissa, out int exponent, out bool negative, out bool hard)
    {
        tokenEnd = 0;
        mantissa = 0;
        exponent = 0;
        negative = false;
        hard = false;
        var i = start;
        while (i < bound && IsWhitespaceByte(utf8[i]))
            i++;
        if (i >= bound)
            return false;
        if (utf8[i] == (byte)'-')
        {
            negative = true;
            i++;
        }
        var intDigits = 0;
        var intStart = i;
        ulong m = 0;
        var started = false;
        var digits = 0;
        var hardLocal = false;
        while (i < bound && utf8[i] >= (byte)'0' && utf8[i] <= (byte)'9')
        {
            Accumulate(utf8[i]);
            i++;
            intDigits++;
        }
        if (intDigits == 0)
            return false;
        if (intDigits > 1 && utf8[intStart] == (byte)'0')
            return false;
        if (i < bound && utf8[i] == (byte)'.')
        {
            i++;
            var fracDigits = 0;
            while (i < bound && utf8[i] >= (byte)'0' && utf8[i] <= (byte)'9')
            {
                Accumulate(utf8[i]);
                i++;
                fracDigits++;
            }
            if (fracDigits == 0)
                return false;
            exponent -= fracDigits;
        }
        if (i < bound && (utf8[i] == (byte)'e' || utf8[i] == (byte)'E'))
        {
            i++;
            var expNegative = false;
            if (i < bound && (utf8[i] == (byte)'+' || utf8[i] == (byte)'-'))
            {
                expNegative = utf8[i] == (byte)'-';
                i++;
            }
            var expDigits = 0;
            var exp = 0;
            while (i < bound && utf8[i] >= (byte)'0' && utf8[i] <= (byte)'9')
            {
                if (exp < 1000000)
                    exp = exp * 10 + (utf8[i] - (byte)'0');
                i++;
                expDigits++;
            }
            if (expDigits == 0)
                return false;
            exponent += expNegative ? -exp : exp;
        }
        if (!TailWhitespace(utf8, i, bound))
            return false;
        tokenEnd = i;
        mantissa = started ? m : 0;
        hard = hardLocal;
        return true;

        void Accumulate(byte c)
        {
            if (c == (byte)'0' && !started)
                return;
            started = true;
            if (digits < 19)
                m = m * 10 + (ulong)(c - (byte)'0');
            else
                hardLocal = true;
            digits++;
        }
    }

    [SuppressMessage("ReSharper", "RedundantCast", Justification = "casts select the numeric operator; removal changes resolution")]

    internal static float PartsToFloat(ulong mantissa, int exponent, bool negative, bool hard)
    {
        if (hard || exponent > 27 || exponent < -400)
            return float.NaN;
        if (mantissa == 0)
            return negative ? -0f : 0f;
        if (exponent >= 0)
        {
            if (mantissa <= Pow5MaxMantissa[exponent])
            {
                var product = mantissa * Pow5U64[exponent];
                if (product < (1UL << 53))
                {
                    var scaled = (float)((double)product * (double)(1L << exponent));
                    return float.IsInfinity(scaled) ? float.NaN : (negative ? -scaled : scaled);
                }
            }
            return float.NaN;
        }
        var k = -exponent;
        if (k <= 10 && mantissa <= 0xFFFFFF)
        {
            var divided = (float)(mantissa / Pow10Double[k]);
            return float.IsInfinity(divided) ? float.NaN : (negative ? -divided : divided);
        }
        return float.NaN;
    }

    internal static double PartsToDouble(ulong mantissa, int exponent, bool negative, bool hard)
    {
        if (hard || exponent > 22 || exponent < -22)
            return double.NaN;
        if (mantissa == 0)
            return negative ? -0d : 0d;
        if (mantissa >= (1UL << 53))
            return double.NaN;
        double value;
        if (exponent >= 0)
            value = mantissa * Pow10Double[exponent];
        else
            value = mantissa / Pow10Double[-exponent];
        if (double.IsInfinity(value))
            return double.NaN;
        return negative ? -value : value;
    }

    private static bool IsWhitespaceByte(byte b) => b is 0x20 or 0x09 or 0x0A or 0x0D;

    private static bool TailWhitespace(byte[] utf8, int from, int bound)
    {
        for (var i = from; i < bound; i++)
            if (!IsWhitespaceByte(utf8[i]))
                return false;
        return true;
    }

}
