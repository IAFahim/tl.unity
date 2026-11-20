namespace Tl;

public readonly struct MarkerFrame<TMarker>
    where TMarker : unmanaged
{
    public MarkerFrame(in TMarker marker, ushort tick, ushort trackIndex, FrameFlags flags)
    {
        Marker = marker;
        Tick = tick;
        TrackIndex = trackIndex;
        Flags = flags;
    }

    public readonly TMarker Marker;
    public ushort Tick { get; }
    public ushort TrackIndex { get; }
    public FrameFlags Flags { get; }
    public int Direction => Has(FrameFlags.Reverse) ? -1 : 1;
    public bool IsBackward => Has(FrameFlags.Reverse);
    public bool Has(FrameFlags flags) => (Flags & flags) == flags;
}
