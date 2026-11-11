using System.Buffers.Binary;

namespace Tl.Gen.Tlb;

internal static class TlbLayout
{
    internal const uint Magic = 0x31424C54u;
    internal const uint Version = 6u;
    internal const uint HeaderBytes = 80u;
    internal const uint PairEntryBytes = 48u;
    internal const uint StageEntryBytes = 16u;
    internal const uint StepBytes = 8u;
    internal const uint SlotRowBytes = 24u;
    internal const ushort NoClipIndex = 0xFFFF;
    internal static uint Align16(uint value) => (value + 15u) & ~15u;

    internal static void WriteRow(byte[] bytes, int offset, ushort trackValueIndex, ushort firstIndex, ushort secondIndex, uint windowStart, uint windowEnd, uint factorStart, uint factorSpan, byte trackIndex)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), trackValueIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2), firstIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 4), secondIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), windowStart);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 12), windowEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 16), factorStart);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 20), factorSpan);
        bytes[offset + 6] = trackIndex;
    }
}
