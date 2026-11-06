using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Tl;

public static unsafe class MarkerTableRegistry
{
    struct Table
    {
        public void* Values;
        public int Stride;
        public int Count;
    }

    static readonly ConditionalWeakTable<TimelineWorld, Dictionary<ushort, Table>> _tables = new();
    static readonly object _gate = new();

    public static void Register(TimelineWorld world, ushort timeline, void* values, int stride, int count)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (values == null) throw new ArgumentNullException(nameof(values));
        if (stride <= 0 || count < 0) throw new ArgumentOutOfRangeException(nameof(stride));
        lock (_gate)
        {
            if (!_tables.TryGetValue(world, out var timelines))
            {
                timelines = new Dictionary<ushort, Table>();
                _tables.Add(world, timelines);
            }
            timelines[timeline] = new Table { Values = values, Stride = stride, Count = count };
        }
    }
    internal static void Clear(TimelineWorld world)
    {
        if (world is null) return;
        lock (_gate) { _tables.Remove(world); }
    }

    public static bool TryResolve(TimelineWorld world, ushort timeline, uint marker, int size, out byte* value)
    {
        value = null;
        if (world is null || size <= 0) return false;
        lock (_gate)
        {
            if (!_tables.TryGetValue(world, out var timelines)) return false;
            if (!timelines.TryGetValue(timeline, out var table)) return false;
            if ((ulong)marker >= (ulong)table.Count) return false;
            if (size > table.Stride) return false;
            value = (byte*)table.Values + (nuint)marker * (nuint)table.Stride;
            return true;
        }
    }
}
