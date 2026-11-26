namespace Tl;

public readonly struct MarkerFrame<TMarker>(in TMarker marker, ushort tick, ushort trackIndex, FrameFlags flags)
    where TMarker : unmanaged
{
    public readonly TMarker Marker = marker;
    public ushort Tick { get; } = tick;
    public ushort TrackIndex { get; } = trackIndex;
    public FrameFlags Flags { get; } = flags;
    public int Direction => Has(FrameFlags.Reverse) ? -1 : 1;
    public bool IsBackward => Has(FrameFlags.Reverse);
    public bool Has(FrameFlags flags) => (Flags & flags) == flags;
}
