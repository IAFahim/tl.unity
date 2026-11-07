#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Tl.Gen.Tlb;

internal sealed class JsonStructuralIndex
{
    private const int BlockBytes = 64;

    internal readonly byte[] Utf8;
    internal readonly ulong[] Structural;
    internal readonly ulong[] Quotes;
    internal readonly int Blocks;

    private JsonStructuralIndex(byte[] utf8, ulong[] structural, ulong[] quotes, int blocks)
    {
        Utf8 = utf8;
        Structural = structural;
        Quotes = quotes;
        Blocks = blocks;
    }

    internal static bool TryScan(byte[] utf8, BakeWorkspace? workspace, out JsonStructuralIndex index)
    {
        index = null!;
#if NET
        if (!Avx2.IsSupported || utf8.Length == 0)
            return false;
        var blocks = (utf8.Length + BlockBytes - 1) / BlockBytes;
        ulong[] structural;
        ulong[] quotes;
        if (workspace?.RentMasks(blocks) is { } rented)
        {
            structural = rented.Structural;
            quotes = rented.Quotes;
        }
        else
        {
            structural = new ulong[blocks];
            quotes = new ulong[blocks];
        }
        unsafe
        {
            fixed (byte* source = utf8)
            fixed (ulong* structuralTarget = structural)
            fixed (ulong* quotesTarget = quotes)
            {
                if (!ScanAvx2(source, utf8.Length, blocks, structuralTarget, quotesTarget))
                {
                    workspace?.ReturnMasks(structural, quotes);
                    return false;
                }
            }
        }
        index = new JsonStructuralIndex(utf8, structural, quotes, blocks);
        return true;
#else
        return false;
#endif
    }

#if NET
    private static readonly Vector256<byte> StructuralHigh = Replicate(0x00, 0x00, 0x20, 0x80, 0x00, 0x50, 0x00, 0x50);
    private static readonly Vector256<byte> StructuralLow = Replicate(0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x40, 0x20, 0x10);

    private static Vector256<byte> Replicate(params byte[] table)
    {
        var half = new byte[16];
        Array.Copy(table, half, Math.Min(table.Length, 16));
        var lane = Vector128.Create(half);
        return Vector256.Create(lane, lane);
    }

    private static unsafe bool ScanAvx2(byte* source, int length, int blocks, ulong* structural, ulong* quotes)
    {
        var structuralHigh = StructuralHigh;
        var structuralLow = StructuralLow;
        var nibbleMask = Vector256.Create((byte)0x0F);
        var quoteByte = Vector256.Create((byte)0x22);
        var backslashByte = Vector256.Create((byte)0x5C);
        var controlMask = Vector256.Create((byte)0xE0);
        var zero = Vector256<byte>.Zero;
        Span<byte> tail = stackalloc byte[32];
        var carry = 0UL;
        var anyBackslash = 0UL;
        var opBits = 0UL;
        var quoteBits = 0UL;
        var backslashBits = 0UL;
        var controlBits = 0UL;
        var pending = false;
        var pendingChunk = 0;
        var fullChunks = length / 32;
        for (var chunk = 0; chunk <= fullChunks; chunk++)
        {
            var offset = chunk * 32;
            Vector256<byte> input;
            if (offset + 32 <= length)
            {
                input = Avx.LoadVector256(source + offset);
            }
            else
            {
                var remainder = length - offset;
                if (remainder <= 0)
                    break;
                for (var i = 0; i < remainder; i++)
                    tail[i] = source[offset + i];
                tail[remainder..].Clear();
                fixed (byte* tailPtr = tail)
                    input = Avx.LoadVector256(tailPtr);
            }
            var wideOp = (ulong)ClassifyMask(input, structuralHigh, structuralLow, nibbleMask, zero);
            var wideQuote = (ulong)Avx2.CompareEqual(input, quoteByte).ExtractMostSignificantBits();
            var wideBackslash = (ulong)Avx2.CompareEqual(input, backslashByte).ExtractMostSignificantBits();
            var wideControl = (ulong)Avx2.CompareEqual(Avx2.And(input, controlMask), zero).ExtractMostSignificantBits();
            if ((chunk & 1) == 0)
            {
                opBits = wideOp;
                quoteBits = wideQuote;
                backslashBits = wideBackslash;
                controlBits = wideControl;
                pending = true;
                pendingChunk = chunk;
                continue;
            }
            opBits |= wideOp << 32;
            quoteBits |= wideQuote << 32;
            backslashBits |= wideBackslash << 32;
            controlBits |= wideControl << 32;
            pending = false;
            if (!FlushBlock(structural, quotes, blocks, ref carry, ref anyBackslash, opBits, quoteBits, backslashBits, controlBits, chunk >> 1))
                return false;
        }
        if (pending && !FlushBlock(structural, quotes, blocks, ref carry, ref anyBackslash, opBits, quoteBits, backslashBits, controlBits, pendingChunk >> 1))
            return false;
        return carry == 0 && anyBackslash == 0;
    }

    private static unsafe bool FlushBlock(ulong* structural, ulong* quotes, int blocks, ref ulong carry, ref ulong anyBackslash, ulong opBits, ulong quoteBits, ulong backslashBits, ulong controlBits, int at)
    {
        var parity = PrefixXor(quoteBits) ^ (carry != 0 ? ulong.MaxValue : 0UL);
        var content = parity ^ quoteBits;
        var opening = quoteBits & parity;
        if ((content & controlBits) != 0)
            return false;
        if (at < blocks)
        {
            structural[at] = (opBits | opening) & ~content;
            quotes[at] = quoteBits;
        }
        carry = parity >> 63;
        anyBackslash |= backslashBits;
        return true;
    }

    private static uint ClassifyMask(Vector256<byte> input, Vector256<byte> high, Vector256<byte> low, Vector256<byte> nibbleMask, Vector256<byte> zero)
    {
        var highNibbles = Avx2.And(Avx2.ShiftRightLogical(input.AsUInt16(), 4).AsByte(), nibbleMask);
        var lowNibbles = Avx2.And(input, nibbleMask);
        var flags = Avx2.And(Avx2.Shuffle(high, highNibbles), Avx2.Shuffle(low, lowNibbles));
        return ~Avx2.CompareEqual(flags, zero).ExtractMostSignificantBits();
    }

#endif

    private static ulong PrefixXor(ulong value)
    {
        value ^= value << 1;
        value ^= value << 2;
        value ^= value << 4;
        value ^= value << 8;
        value ^= value << 16;
        value ^= value << 32;
        return value;
    }
}
