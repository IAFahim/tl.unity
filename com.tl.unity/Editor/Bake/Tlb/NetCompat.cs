using System.Diagnostics;
using System.Security.Cryptography;

namespace Tl.Gen.Tlb;

internal static class NetCompat
{
    internal static double ElapsedMs(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    internal static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(bytes);
        var text = new System.Text.StringBuilder(digest.Length * 2);
        foreach (var b in digest)
            text.Append(b.ToString("x2"));
        return text.ToString();
    }

#if NET
    internal static int FirstInvalidUtf8(ReadOnlySpan<byte> span)
    {
        if (System.Text.Unicode.Utf8.IsValid(span))
            return -1;
        var offset = 0;
        while (offset < span.Length)
        {
            var status = System.Text.Rune.DecodeFromUtf8(span[offset..], out _, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
                return offset;
            offset += consumed;
        }
        return span.Length;
    }
#else
    internal static int FirstInvalidUtf8(ReadOnlySpan<byte> span)
    {
        var i = 0;
        while (i < span.Length)
        {
            var b = span[i];
            if (b < 0x80)
            {
                i++;
                continue;
            }
            int need, min;
            if (b >= 0xC2 && b <= 0xDF) { need = 1; min = 0x80; }
            else if (b >= 0xE0 && b <= 0xEF) { need = 2; min = 0x800; }
            else if (b >= 0xF0 && b <= 0xF4) { need = 3; min = 0x10000; }
            else return i;
            if (i + need >= span.Length)
                return i;
            var code = b & (0x3F >> need);
            for (var k = 1; k <= need; k++)
            {
                var c = span[i + k];
                if ((c & 0xC0) != 0x80)
                    return i;
                code = (code << 6) | (c & 0x3F);
            }
            if (code < min || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
                return i;
            i += need + 1;
        }
        return -1;
    }
#endif
}

#if !NET
internal static class BitOperations
{
    internal static int TrailingZeroCount(ulong value)
    {
        if (value == 0)
            return 64;
        var count = 0;
        while ((value & 1) == 0)
        {
            value >>= 1;
            count++;
        }
        return count;
    }

    internal static int PopCount(ulong value)
    {
        var count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }
        return count;
    }
}

internal static class OperatingSystem
{
    internal static bool IsLinux() => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);

    internal static bool IsWindows() => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
}
#endif
