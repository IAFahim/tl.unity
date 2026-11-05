using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace Tl.Unity
{
    [BurstCompile]
    public partial struct TimelineAdvanceJob : IJobChunk
    {
        [global::Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public global::Tl.LibraryView View;
        public BufferTypeHandle<Playback> PlaybackHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in global::Unity.Burst.Intrinsics.v128 chunkEnabledMask)
        {
            var playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);
            for (var row = 0; row < chunk.Count; row++)
            {
                var slots = playbacks[row];
                for (var i = 0; i < slots.Length; i++)
                {
                    var slot = slots[i];
                    if (!TimelineKernel.AdvancePlayback(ref slot, in View, true, out _, out var removed))
                        continue;
                    if (removed)
                        slots.RemoveAt(i--);
                    else
                        slots[i] = slot;
                }
            }
        }
    }

    public static partial class TimelineJobs
    {
        public static JobHandle ScheduleAdvance(ComponentSystemBase system, EntityQuery query, global::Tl.LibraryView view, JobHandle dependsOn)
        {
            var job = new TimelineAdvanceJob
            {
                View = view,
                PlaybackHandle = system.GetBufferTypeHandle<Playback>(false),
            };
            return job.ScheduleParallel(query, dependsOn);
        }
    }
}
