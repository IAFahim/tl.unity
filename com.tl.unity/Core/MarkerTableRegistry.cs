using System;

namespace Tl;

public static unsafe class MarkerTableRegistry
{
    public static void Register(TimelineWorld world, ushort timeline, void* values, int stride, int count)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        world.RegisterMarkers(timeline, values, stride, count);
    }

    public static bool TryResolve(TimelineWorld world, ushort timeline, uint marker, int size, out byte* value)
    {
        value = null;
        if (world is null || size <= 0) return false;
        return world.TryResolveMarker(timeline, marker, size, out value);
    }
}
