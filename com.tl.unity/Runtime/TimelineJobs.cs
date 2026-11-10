using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace Tl.Unity
{
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe partial struct TimelineAdvanceJob : IJobChunk
    {
        [global::Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public global::Tl.LibraryView View;
        public BufferTypeHandle<Playback> PlaybackHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in global::Unity.Burst.Intrinsics.v128 chunkEnabledMask)
        {
            var playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);
            for (var row = 0; row < chunk.Count; row++)
            {
                var slots = playbacks[row];
                var cells = (global::Tl.Playback*)slots.GetUnsafePtr();
                var length = slots.Length;
                var removed = false;
                for (var i = 0; i < length; i++)
                {
                    if (cells[i].Timeline == 0 || global::Tl.PlaybackRules.Step(ref cells[i], View.Timelines, View.TimelineCount, true) != global::Tl.AdvanceResult.Removed)
                        continue;
                    cells[i] = default;
                    removed = true;
                }
                if (!removed)
                    continue;
                while (length > 0 && cells[length - 1].Timeline == 0)
                    length--;
                slots.ResizeUninitialized(length);
            }
        }
    }

    public static partial class TimelineJobs
    {
        public static JobHandle ScheduleAdvance(ref SystemState state, EntityQuery query, global::Tl.LibraryView view, JobHandle dependsOn)
        {
            var job = new TimelineAdvanceJob
            {
                View = view,
                PlaybackHandle = state.GetBufferTypeHandle<Playback>(false),
            };
            return job.ScheduleParallel(query, dependsOn);
        }
    }
}
