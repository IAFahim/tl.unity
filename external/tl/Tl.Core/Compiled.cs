using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tl;

public interface ITimeline
{
    static abstract void Define(scoped Builder builder);
}

public interface ITimelineJob<TTrack, TClip>
    where TTrack : unmanaged, IBlend<TClip>
    where TClip : unmanaged;

public interface ITimelineCatalog
{
    static abstract void Define(scoped CatalogBuilder builder);
}

public interface IHook;

public readonly ref struct TrackRef<TTrack>
    where TTrack : unmanaged
{
    public TrackRef<TTrack, TJob> Use<TJob>()
        where TJob : unmanaged
        => default;
}

public readonly ref struct TrackRef<TTrack, TJob>
    where TTrack : unmanaged
    where TJob : unmanaged;

public readonly ref struct Builder
{
    public TrackRef<TTrack> Track<TTrack>(in TTrack track)
        where TTrack : unmanaged
        => default;

    public void Clip<TTrack, TJob, TClip>(in TrackRef<TTrack, TJob> track, in TClip clip, uint start, uint end)
        where TTrack : unmanaged, IBlend<TClip>
        where TJob : unmanaged, ITimelineJob<TTrack, TClip>
        where TClip : unmanaged
    {
    }

    public void Looping()
    {
    }

    public void Before<THook>() where THook : unmanaged, IHook
    {
    }

    public void After<THook>() where THook : unmanaged, IHook
    {
    }

    public void Include<TTimeline>() where TTimeline : unmanaged, ITimeline
    {
    }
}

public readonly ref struct CatalogBuilder
{
    public SchemaBuilder<TSchema> Schema<TSchema>()
        where TSchema : unmanaged
        => default;
}

public readonly ref struct SchemaBuilder<TSchema>
    where TSchema : unmanaged
{
    public SchemaBuilder<TSchema> Asset<TTimeline>()
        where TTimeline : unmanaged, ITimeline
        => this;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct TimelineFrame
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TimelineFrame(uint gameTick, uint timelineTick, long cycle, FrameFlags flags)
    {
        GameTick = gameTick;
        TimelineTick = timelineTick;
        Cycle = cycle;
        Flags = flags;
    }

    public uint GameTick { get; }
    public uint TimelineTick { get; }
    public long Cycle { get; }
    public FrameFlags Flags { get; }
    public int Direction => Has(FrameFlags.Reverse) ? -1 : 1;
    public bool Has(FrameFlags flags) => (Flags & flags) == flags;
}

public readonly ref struct Frame<TTrack, TClip>
    where TTrack : unmanaged, IBlend<TClip>
    where TClip : unmanaged
{
    private readonly ref readonly TTrack _track;
    private readonly ref readonly TClip _clip;

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
        _track = ref track;
        _clip = ref clip;
        GameTick = gameTick;
        TimelineTick = timelineTick;
        Cycle = cycle;
        TrackIndex = trackIndex;
        Flags = flags;
    }

    public ref readonly TTrack Track => ref _track;
    public ref readonly TClip Clip => ref _clip;
    public uint GameTick { get; }
    public uint TimelineTick { get; }
    public long Cycle { get; }
    public ushort TrackIndex { get; }
    public FrameFlags Flags { get; }
    public int Direction => Has(FrameFlags.Reverse) ? -1 : 1;
    public bool IsBackward => Has(FrameFlags.Reverse);
    public bool Has(FrameFlags flags) => (Flags & flags) == flags;
}
