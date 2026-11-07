using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.LowLevel.Unsafe;

namespace Tl.Unity
{
    [BurstCompile]
    public unsafe struct TlTimelineSwapJob : IJobChunk
    {
        [NativeDisableUnsafePtrRestriction] public KindIndex Index;
        [NativeDisableUnsafePtrRestriction] public global::Tl.LibraryView View;
        public BufferTypeHandle<Playback> PlaybackHandle;
        public ComponentTypeHandle<TimelinePlaying> PlayingHandle;
        [NativeDisableContainerSafetyRestriction] public KindHandleSet Handles;
        [ReadOnly] public NativeArray<ushort> Swapped;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);
            var kinds = Index.KindCount;
            var buffers = stackalloc UnsafeUntypedBufferAccessor[kinds > 0 ? kinds : 1];
            var cursor = new KindCursor { Index = Index, Handles = (KindHandleSet*)UnsafeUtility.AddressOf(ref Handles), Buffers = buffers };
            if (kinds > 0)
                cursor.Bind(in chunk);
            for (var row = 0; row < chunk.Count; row++)
            {
                var slots = playbacks[row];
                var touched = false;
                for (var i = 0; i < slots.Length; i++)
                {
                    var s = slots[i];
                    if (s.Timeline == 0 || !IsSwapped(s.Timeline) || (s.Flags & global::Tl.PlaybackFlags.Stopping) != 0)
                        continue;
                    var tl = TimelineKernel.GetTimeline(in View, s.Timeline);
                    if (tl == null || tl->Duration == 0)
                        continue;
                    var duration = (int)tl->Duration;
                    if (s.Tick >= duration)
                        s.Tick = (ushort)(tl->Loop != 0 ? s.Tick % duration : duration - 1);
                    if (kinds > 0)
                    {
                        cursor.Purge(row, (byte)i, s.Timeline);
                        s.Flags = (s.Flags & (global::Tl.PlaybackFlags.Reverse | global::Tl.PlaybackFlags.Paused)) | global::Tl.PlaybackFlags.StartPending;
                    }
                    slots[i] = s;
                    touched = true;
                }
                if (touched && chunk.Has(ref PlayingHandle))
                    chunk.SetComponentEnabled(ref PlayingHandle, row, true);
            }
        }

        bool IsSwapped(ushort id)
        {
            for (var i = 0; i < Swapped.Length; i++)
                if (Swapped[i] == id)
                    return true;
            return false;
        }
    }
}
