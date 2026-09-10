using Tl;

namespace Tl.Samples.GeneratedJobs;

public readonly partial struct Attack : ITimeline
{
    public static void Define(scoped Builder builder)
    {
        builder.Before<BeforeAfter>();
        var first = builder.Track(new ValueTrack(1)).Use<A>();
        var middle = builder.Track(new ValueTrack(2)).Use<B>();
        var last = builder.Track(new ValueTrack(3)).Use<A>();
        builder.Clip(first, new ValueClip(10), 0u, 2u);
        builder.Clip(first, new ValueClip(20), 0u, 2u);
        builder.Clip(middle, new ValueClip(5), 0u, 2u);
        builder.Clip(last, new ValueClip(7), 0u, 2u);
        builder.After<BeforeAfter>();
    }
}

public readonly partial struct Defense : ITimeline
{
    public static void Define(scoped Builder builder)
    {
        builder.Before<BeforeAfter>();
        var first = builder.Track(new ValueTrack(2)).Use<B>();
        var last = builder.Track(new ValueTrack(1)).Use<A>();
        builder.Clip(first, new ValueClip(6), 0u, 2u);
        builder.Clip(last, new ValueClip(7), 0u, 2u);
        builder.After<BeforeAfter>();
    }
}

public readonly partial struct Gap : ITimeline
{
    public static void Define(scoped Builder builder)
    {
        var value = builder.Track(new ValueTrack(1)).Use<A>();
        builder.Clip(value, new ValueClip(9), 1u, 2u);
    }
}

public readonly partial struct Loop : ITimeline
{
    public static void Define(scoped Builder builder)
    {
        var value = builder.Track(new ValueTrack(1)).Use<A>();
        builder.Clip(value, new ValueClip(1), 0u, 1u);
        builder.Looping();
    }
}

public readonly struct Rows;
public readonly struct GapRows;
public readonly struct LoopRows;

public readonly partial struct Combat : ITimelineCatalog
{
    public static void Define(scoped CatalogBuilder builder)
    {
        builder.Schema<Rows>().Asset<Attack>().Asset<Defense>();
        builder.Schema<GapRows>().Asset<Gap>();
        builder.Schema<LoopRows>().Asset<Loop>();
    }
}
