using System.Runtime.InteropServices;

namespace Tl;

[StructLayout(LayoutKind.Sequential)]
internal struct LaneSegment
{
    public const byte Constant = 0;
    public const byte Ramp = 1;
    public const byte Dense = 2;
    public ushort Start;
    public ushort End;
    public byte Kind;
    public byte Pad;
    public float V0;
    public float V1;
}

[StructLayout(LayoutKind.Sequential, Size = 80)]
internal unsafe struct SlotView
{
    public const ushort AbiVersionV1 = 1;
    public const ushort AbiVersionV2 = 2;
    public const ushort AbiVersionV3 = 3;
    public const ushort AbiVersionV4 = 4;
    public const ushort Skipped = 0xFFFF;
    public uint* Directory;
    public LaneSegment* Segments;
    public float* Dense;
    public float* Forward;
    public float* Backward;
    public ulong* LaneKeys;
    public ushort Duration;
    public ushort Looping;
    public ushort Absent;
    public uint TableTicks;
    public ushort ResultCount;
    public ushort AbiVersion;
    public ulong Generation;
    public ulong ConsumerDigest;
}
