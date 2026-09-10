using Tl;

namespace Tl.Samples.GeneratedJobs;

public readonly partial struct Attack : ITimeline
{
    public static void Define(scoped Builder builder)
    {
        var value = builder.Track(new ValueTrack(2)).Use<ValueJob>();
        builder.Clip(value, new ValueClip(3), 0u, 2u);
    }
}

public readonly struct Rows;

public readonly partial struct Combat : ITimelineCatalog
{
    public static void Define(scoped CatalogBuilder builder)
    {
        builder.Schema<Rows>().Asset<Attack>();
    }
}
