using Tl;
using Tl.Samples.GeneratedJobs;

namespace Tl.Samples.External;

public readonly partial struct Gap : ITimeline
{
    public static void Define(scoped Builder builder)
    {
        var value = builder.Track(new ValueTrack(1)).Use<A>();
        builder.Clip(value, new ValueClip(9), 1u, 2u);
    }
}
