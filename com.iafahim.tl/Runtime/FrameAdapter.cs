using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Tl;

public readonly unsafe ref struct Frame<TTrack, TClip>
    where TTrack : unmanaged, IBlend<TClip>
    where TClip : unmanaged
{
    private readonly TTrack* _track;
    private readonly TClip* _clip;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public Frame(
        in TTrack track,
        in TClip clip,
        uint gameTick,
        uint timelineTick,
        long cycle,
        ushort trackIndex,
        FrameFlags flags)
    {
        _track = (TTrack*)Unsafe.AsPointer(ref Unsafe.AsRef(in track));
        _clip = (TClip*)Unsafe.AsPointer(ref Unsafe.AsRef(in clip));
        GameTick = gameTick;
        TimelineTick = timelineTick;
        Cycle = cycle;
        TrackIndex = trackIndex;
        Flags = flags;
    }

    public ref readonly TTrack Track => ref *_track;
    public ref readonly TClip Clip => ref *_clip;
    public uint GameTick { get; }
    public uint TimelineTick { get; }
    public long Cycle { get; }
    public ushort TrackIndex { get; }
    public FrameFlags Flags { get; }
    public int Direction => Has(FrameFlags.Reverse) ? -1 : 1;
    public bool IsBackward => Has(FrameFlags.Reverse);
    public bool Has(FrameFlags flags) => (Flags & flags) == flags;
}
