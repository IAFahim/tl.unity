using System.ComponentModel;

namespace Tl;

public interface ITrack<TTrack, TClip>
    where TTrack : unmanaged
    where TClip : unmanaged;


public interface IHostedConsumer;

public readonly struct Frame<TTrack, TClip>
    where TTrack : unmanaged
    where TClip : unmanaged
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Frame(
        in TTrack track,
        in TClip clip,
        ushort timelineTick,
        ushort clipLength,
        ushort withinClip,
        ushort trackIndex,
        FrameFlags flags)
    {
        Track = track;
        Clip = clip;
        TimelineTick = timelineTick;
        ClipLength = clipLength;
        WithinClip = withinClip;
        TrackIndex = trackIndex;
        Flags = flags;
    }

    public readonly TTrack Track;
    public readonly TClip Clip;
    public ushort TimelineTick { get; }
    public ushort Tick => TimelineTick;
    public ushort ClipLength { get; }
    public ushort WithinClip { get; }
    public ushort TrackIndex { get; }
    public FrameFlags Flags { get; }
    public int Direction => Has(FrameFlags.Reverse) ? -1 : 1;
    public bool IsBackward => Has(FrameFlags.Reverse);
    public bool Has(FrameFlags flags) => (Flags & flags) == flags;
}
