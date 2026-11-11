#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif
using System.Collections.Generic;
using System.Runtime.InteropServices;

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

public unsafe struct SystemSteps
{
    public int* StageBase;
    public int* StepStart;
    public SystemStep* Steps;
}

public static unsafe class SystemDispatch
{
    public static SystemSteps Bind(TimelineWorld world, ulong system, ReadOnlySpan<ulong> pairKeys)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        var registry = world._systems ??= new SystemRegistry();
        registry.RefreshModules();
        var view = world._view;
        if (registry.ViewStamp != (nint)view.Timelines || registry.ViewBytes != view.ByteCount || registry.ViewCount != view.TimelineCount || registry.ViewGeneration != world._generation)
            registry.Reset((nint)view.Timelines, view.ByteCount, view.TimelineCount, world._generation);
        lock (registry)
        {
            if (registry.Steps.TryGetValue(system, out var cached)) return cached;
            var built = Build(view, pairKeys);
            registry.Steps.Add(system, built);
            return built;
        }
    }

    static SystemSteps Build(LibraryView view, ReadOnlySpan<ulong> pairKeys)
    {
        var timelines = view.Timelines;
        var count = view.TimelineCount;
        var totalStages = 0;
        for (var i = 0; i < count; i++) totalStages += (timelines + i)->StageCount;
        var stageBase = (int*)TimelineWorld.TrackAlloc((nuint)(count > 0 ? count : 1) * (nuint)sizeof(int));
        var stepStart = (int*)TimelineWorld.TrackAlloc((nuint)(totalStages + 1) * (nuint)sizeof(int));
        NativeMemory.Clear(stageBase, (nuint)(count > 0 ? count : 1) * (nuint)sizeof(int));
        NativeMemory.Clear(stepStart, (nuint)(totalStages + 1) * (nuint)sizeof(int));
        var running = 0;
        for (var i = 0; i < count; i++)
        {
            stageBase[i] = running;
            running += (timelines + i)->StageCount;
        }
        var total = 0;
        if (pairKeys.Length != 0)
        {
            for (var i = 0; i < count; i++)
            {
                var tl = timelines + i;
                for (var s = 0; s < tl->StageCount; s++)
                {
                    var steps = LibraryViewReader.StepsFor(tl, tl->Stages + s);
                    for (var k = 0; k < steps.Length; k++)
                        for (var c = 0; c < pairKeys.Length; c++)
                            if (pairKeys[c] == steps[k].PairKey) total++;
                }
            }
        }
        var table = (SystemStep*)TimelineWorld.TrackAlloc((nuint)(total > 0 ? total : 1) * (nuint)sizeof(SystemStep));
        NativeMemory.Clear(table, (nuint)(total > 0 ? total : 1) * (nuint)sizeof(SystemStep));
        var w = 0;
        var g = 0;
        for (var i = 0; i < count; i++)
        {
            var tl = timelines + i;
            for (var s = 0; s < tl->StageCount; s++)
            {
                stepStart[g++] = w;
                var steps = LibraryViewReader.StepsFor(tl, tl->Stages + s);
                for (var k = 0; k < steps.Length; k++)
                {
                    var want = steps[k].PairKey;
                    for (var c = 0; c < pairKeys.Length; c++)
                        if (pairKeys[c] == want) table[w++] = new SystemStep(k, c);
                }
            }
        }
        stepStart[totalStages] = total;
        return new SystemSteps { StageBase = stageBase, StepStart = stepStart, Steps = table };
    }

    internal static void Free(SystemSteps steps)
    {
        TimelineWorld.TrackFree(steps.StageBase);
        TimelineWorld.TrackFree(steps.StepStart);
        TimelineWorld.TrackFree(steps.Steps);
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
