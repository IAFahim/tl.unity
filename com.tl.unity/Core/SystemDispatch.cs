#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif
using System.Collections.Generic;

namespace Tl;

public readonly struct SystemStep
{
    public SystemStep(int step, int consumer)
    {
        Step = step;
        Consumer = consumer;
    }

    public readonly int Step;
    public readonly int Consumer;
}

readonly struct FilterKey : System.IEquatable<FilterKey>
{
    public FilterKey(ulong system, ushort timeline, int stage)
    {
        System = system;
        Timeline = timeline;
        Stage = stage;
    }

    public readonly ulong System;
    public readonly ushort Timeline;
    public readonly int Stage;

    public bool Equals(FilterKey other) => System == other.System && Timeline == other.Timeline && Stage == other.Stage;
    public override bool Equals(object? obj) => obj is FilterKey other && Equals(other);
    public override int GetHashCode() => System.GetHashCode() ^ (Timeline.GetHashCode() * 397) ^ Stage.GetHashCode();
}

public static unsafe class SystemDispatch
{
    public static Span<SystemStep> FilteredSteps(TimelineWorld world, ulong system, ushort timeline, int stage, ReadOnlySpan<ulong> pairKeys)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        var registry = world._systems ??= new SystemRegistry();
        registry.RefreshModules();
        var view = world._view;
        if (registry.ViewStamp != (nint)view.Timelines || registry.ViewBytes != view.ByteCount || registry.ViewCount != view.TimelineCount || registry.ViewGeneration != world._generation)
            registry.Reset((nint)view.Timelines, view.ByteCount, view.TimelineCount, world._generation);
        var key = new FilterKey(system, timeline, stage);
        if (registry.Filters.TryGetValue(key, out var cached)) return cached;
        var built = Build(world._view, timeline, stage, pairKeys);
        registry.Filters.Add(key, built);
        return built;
    }

    static SystemStep[] Build(LibraryView view, ushort timeline, int stage, ReadOnlySpan<ulong> pairKeys)
    {
        var tl = LibraryViewReader.GetTimeline(in view, new TimelineId(timeline));
        if (tl == null || stage < 0 || stage >= tl->StageCount || pairKeys.Length == 0) return [];
        var steps = LibraryViewReader.StepsFor(tl, tl->Stages + stage);
        var list = new List<SystemStep>(steps.Length);
        for (var s = 0; s < steps.Length; s++)
        {
            var want = steps[s].PairKey;
            for (var c = 0; c < pairKeys.Length; c++)
                if (pairKeys[c] == want) list.Add(new SystemStep(s, c));
        }
        return list.ToArray();
    }

    public static void FastFoldAdd(float* lane, ushort* ticks, float* columns, int count)
    {
#if NET
        if (Avx2.IsSupported)
        {
            var i = 0;
            var bound = count & ~7;
            for (; i < bound; i += 8)
            {
                var narrow = Vector128.Load(ticks + i);
                var index = Vector256.Create(Vector128.WidenLower(narrow).AsInt32(), Vector128.WidenUpper(narrow).AsInt32());
                var gathered = Avx2.GatherVector256(lane, index, 4);
                var current = Vector256.Load(columns + i);
                Avx.Add(current, gathered).Store(columns + i);
            }
            for (; i < count; i++) columns[i] += lane[ticks[i]];
            return;
        }
#endif
        for (var i = 0; i < count; i++) columns[i] += lane[ticks[i]];
    }
}
