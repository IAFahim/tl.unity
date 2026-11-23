using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.LowLevel.Unsafe;
using Unity.Jobs;

namespace Tl.Unity
{
    public static class KindCounter
    {
        public const int DroppedPulses = 0;
        public const int DroppedStarts = 1;
        public const int ManagedRuns = 2;
        public const int Count = 3;
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe struct KindPulseClearJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public KindIndex Index;
        public bool AllowFastPulses;
        public int OrderVersion;
        [ReadOnly] public EntityStorageInfoLookup Storage;
        [NativeDisableContainerSafetyRestriction] public KindHandleSet Handles;

        public void Execute()
        {
            if (Index.WorkerCache != null)
                for (var t = 0; t < Index.ThreadCount; t++)
                    Index.WorkerCache[t].Flags = 0;
            if (Index.TickOrderVersion != null) *Index.TickOrderVersion = OrderVersion;
            if (Index.PrevSampledCount != null && Index.SampledCount != null)
            {
                *Index.PrevSampledCount = *Index.SampledCount;
                *Index.SampledCount = 0;
            }
            ulong fast = 0;
            for (var k = 0; k < Index.KindCount; k++)
            {
                if (Index.SlowCount[k] != 0)
                {
                    Index.HeldBufferKinds[k] = 1;
                    Index.SlowCount[k] = 0;
                }
                Index.SpillCount[k] = 0;
                Index.HeldSeen[k] = 0;
                var baseLane = Index.PulseLanes + k * Index.ThreadCount;
                var anyPending = false;
                for (var t = 0; t < Index.ThreadCount; t++)
                    if (baseLane[t].Count > 0) { anyPending = true; break; }
                if (anyPending)
                {
                    FlushKind(k, baseLane);
                    Index.HeldBufferKinds[k] = 1;
                }
                if (AllowFastPulses && Index.TablePulses != null && (*Index.TablePulses >> k & 1) != 0 && Index.HeldBufferKinds[k] == 0)
                    fast |= 1ul << k;
            }
            if (Index.TickFastPulses != null) *Index.TickFastPulses = fast;
        }

        void FlushKind(int kind, KindPulseLane* baseLane)
        {
            var handleSet = (KindHandleSet*)UnsafeUtility.AddressOf(ref Handles);
            var mapped = KindHandleSet.IsMapped(handleSet, kind);
            var pv = Index.PulseValuesTable + kind;
            var canWrite = mapped && pv->Data != null;
            if (canWrite)
            {
                var entryHandle = KindHandleSet.Entry(handleSet, kind);
                var activeHandle = KindHandleSet.Active(handleSet, kind);
                var starts = pv->Start;
                var vbytes = pv->Stride;
                var stride = Index.EntryStride[kind];
                for (var th = 0; th < Index.ThreadCount; th++)
                {
                    var lane = baseLane + th;
                    var n = lane->Count;
                    if (n == 0) continue;
                    var items = lane->Items;
                    var flushing = Entity.Null;
                    var insertAt = 0;
                    for (var i = 0; i < n; i++)
                    {
                        var e = items[i].Entity;
                        if (!Storage.Exists(e)) continue;
                        var info = Storage[e];
                        if (!info.Chunk.Has(ref *entryHandle)) continue;
                        var c = items[i].Cell;
                        var s = starts[c];
                        var cnt = starts[c + 1] - s;
                        if (cnt <= 0) continue;
                        if (e != flushing)
                        {
                            flushing = e;
                            insertAt = 0;
                        }
                        var acc = info.Chunk.GetUntypedBufferAccessor(ref *entryHandle);
                        var len = acc.GetBufferLength(info.IndexInChunk);
                        acc.ResizeUninitialized(info.IndexInChunk, len + cnt);
                        var rowBase = (byte*)acc.GetUnsafePtr(info.IndexInChunk);
                        UnsafeUtility.MemMove(rowBase + (long)(insertAt + cnt) * stride, rowBase + (long)insertAt * stride, (long)(len - insertAt) * stride);
                        var dstBase = rowBase + (long)insertAt * stride;
                        insertAt += cnt;
                        var src = pv->Data + (long)s * vbytes;
                        for (var p = 0; p < cnt; p++)
                        {
                            var dst = dstBase + (long)p * stride;
                            UnsafeUtility.MemClear(dst, stride);
                            ((global::Tl.KindEntry*)dst)->Flags = global::Tl.KindEntryFlags.Pulse;
                            UnsafeUtility.MemCpy(dst + 8, src + (long)p * vbytes, vbytes);
                        }
                        if (len == 0) info.Chunk.SetComponentEnabled(ref *activeHandle, info.IndexInChunk, true);
                    }
                    lane->Count = 0;
                }
            }
            else
            {
                for (var th = 0; th < Index.ThreadCount; th++) baseLane[th].Count = 0;
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe partial struct KindTickJob : IJobChunk
    {
        [NativeDisableUnsafePtrRestriction] public KindIndex Index;
        [NativeDisableUnsafePtrRestriction] public global::Tl.KindTables Tables;
        [ReadOnly] public EntityTypeHandle EntityHandle;
        [NativeSetThreadIndex] internal int ThreadIndex;
        public BufferTypeHandle<Playback> PlaybackHandle;
        public BufferTypeHandle<TimelineRequest> RequestHandle;
        public ComponentTypeHandle<TimelinePlaying> PlayingHandle;
        public ComponentTypeHandle<TimelineHorizon> HorizonHandle;
        public ComponentTypeHandle<TimelineClock> ClockHandle;
        public BufferTypeHandle<PlaybackDue> DueHandle;
        [NativeDisableContainerSafetyRestriction] public KindHandleSet Handles;
        [NativeDisableUnsafePtrRestriction] public int* Counters;
        public int StepDir;
        public uint LastSystemVersion;
        public int HorizonLimit;
        public bool ForgetHorizons;
        public ushort Now;
        [NativeDisableUnsafePtrRestriction] public KindDense* Dense;
        [NativeDisableUnsafePtrRestriction] public DenseSpace* Space;
        public ushort DenseFrom;
        public bool DenseReady;
        public bool DenseForget;

        [BurstDiscard]
        static void CountManagedRun(int* counters) => System.Threading.Interlocked.Increment(ref counters[KindCounter.ManagedRuns]);

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            CountManagedRun(Counters);
            var count = chunk.Count;
            var playbackChanged = Space != null && chunk.DidChange(ref PlaybackHandle, LastSystemVersion);
            var cache = Index.WorkerCache != null && (uint)ThreadIndex < (uint)Index.ThreadCount ? Index.WorkerCache + ThreadIndex : null;
            var arch = chunk.Archetype;
            bool hasClock, hasDue, hasHorizon, hasRequest;
            if (cache != null && (cache->Flags & 1) != 0 && cache->Archetype.Equals(arch))
            {
                var cf = cache->Flags;
                hasClock = (cf & 2) != 0;
                hasDue = (cf & 4) != 0;
                hasHorizon = (cf & 8) != 0;
                hasRequest = (cf & 16) != 0;
            }
            else
            {
                hasClock = chunk.Has(ref ClockHandle);
                hasDue = hasClock && chunk.Has(ref DueHandle);
                hasHorizon = chunk.Has(ref HorizonHandle);
                hasRequest = chunk.Has(ref RequestHandle);
                if (cache != null)
                {
                    cache->Archetype = arch;
                    cache->Reach = 0;
                    cache->Flags = (byte)(1 | (hasClock ? 2 : 0) | (hasDue ? 4 : 0) | (hasHorizon ? 8 : 0) | (hasRequest ? 16 : 0));
                }
            }
            byte* horizons = null;
            ushort* clocks = null;
            var dues = default(BufferAccessor<PlaybackDue>);
            if (hasClock)
            {
                if (hasDue)
                {
                    clocks = (ushort*)chunk.GetNativeArray(ref ClockHandle).GetUnsafePtr();
                    dues = chunk.GetBufferAccessor(ref DueHandle);
                }
                else
                    Unclock(in chunk, count);
            }
            if (HorizonLimit > 0 && hasHorizon)
            {
                horizons = (byte*)chunk.GetNativeArray(ref HorizonHandle).GetUnsafePtr();
                if (ForgetHorizons || chunk.DidChange(ref PlaybackHandle, LastSystemVersion))
                {
                    UnsafeUtility.MemClear(horizons, count);
                    if (clocks != null)
                    {
                        var all = chunk.GetBufferAccessor(ref PlaybackHandle);
                        for (var row = 0; row < count; row++)
                            DueNow(dues[row], all[row].Length);
                    }
                }
            }
            else if (clocks != null)
            {
                var all = chunk.GetBufferAccessor(ref PlaybackHandle);
                for (var row = 0; row < count; row++)
                {
                    if (dues[row].Length != 0) dues[row].Clear();
                    if (clocks[row] == 0) continue;
                    TimelineTicks.Decode((global::Tl.Playback*)all[row].GetUnsafePtr(), all[row].Length, clocks[row]);
                    clocks[row] = 0;
                }
                clocks = null;
            }
            var requested = hasRequest && chunk.DidChange(ref RequestHandle, LastSystemVersion);
            var playing = chunk.GetEnableableBits(ref PlayingHandle);
            if (!requested && playing.ULong0 == 0 && (count <= 64 || playing.ULong1 == 0)) return;
            var buffers = Index.WorkerBuffers != null && (uint)ThreadIndex < (uint)Index.ThreadCount && Index.KindCount > 0 ? Index.WorkerBuffers + ThreadIndex * Index.KindCount : null;
            var masks = Index.WorkerMasks != null && (uint)ThreadIndex < (uint)Index.ThreadCount && Index.KindCount > 0 ? Index.WorkerMasks + ThreadIndex * Index.KindCount : null;
            var tables = Tables.Timelines != null || Tables.Closure != null ? Tables : Index.Tables;
            tables.FastPulseMask = Index.TickFastPulses != null ? *Index.TickFastPulses : 0ul;
            var playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);
            var ctx = new KindCursor
            {
                Index = (KindIndex*)UnsafeUtility.AddressOf(ref Index),
                Tables = &tables,
                Handles = (KindHandleSet*)UnsafeUtility.AddressOf(ref Handles),
                Buffers = buffers,
                Masks = masks,
                Counters = Counters,
                PlayingHandlePtr = (ComponentTypeHandle<TimelinePlaying>*)UnsafeUtility.AddressOf(ref PlayingHandle),
                Playbacks = playbacks,
            };
            ctx.Bind(in chunk);
            if (cache != null)
            {
                ctx.Cache = cache;
                if ((cache->Flags & 32) != 0)
                {
                    ctx.Reach = cache->Reach;
                    ctx.HasReach = true;
                }
            }
            if (requested)
            {
                var requests = chunk.GetBufferAccessor(ref RequestHandle);
                for (var row = 0; row < count; row++)
                {
                    var queue = requests[row];
                    if (queue.Length == 0) continue;
                    if (horizons != null) horizons[row] = 0;
                    if (clocks != null) TimelineTicks.Decode((global::Tl.Playback*)playbacks[row].GetUnsafePtr(), playbacks[row].Length, clocks[row]);
                    else if (Space != null && DenseSlots.IsDense((global::Tl.Playback*)playbacks[row].GetUnsafePtr(), playbacks[row].Length))
                        DenseSlots.Settle(Space, (global::Tl.Playback*)playbacks[row].GetUnsafePtr(), playbacks[row].Length, DenseFrom);
                    var slots = new KindSlots(playbacks[row], &ctx, row);
                    for (var r = 0; r < queue.Length; r++)
                        global::Tl.PlaybackRules.Apply(ref slots, Index.Timelines, Index.TimelineCount, queue[r].Op, queue[r].Timeline, queue[r].At);
                    queue.Clear();
                    if (clocks != null)
                    {
                        TimelineTicks.Encode((global::Tl.Playback*)playbacks[row].GetUnsafePtr(), playbacks[row].Length, clocks[row]);
                        DueNow(dues[row], playbacks[row].Length);
                    }
                    ctx.SetPlaying(ref PlayingHandle, row, true);
                }
                playing = chunk.GetEnableableBits(ref PlayingHandle);
            }
            if (playing.ULong0 != 0 || (count > 64 && playing.ULong1 != 0))
            {
                var reverse = global::Tl.SlotWord.WorldReverse(StepDir > 0);
                var hadSampled = Index.PrevSampledCount == null || *Index.PrevSampledCount != 0;
                var copiesRo = hadSampled ? (TimelinePlaying*)chunk.GetRequiredComponentDataPtrRO(ref PlayingHandle) : null;
                ctx.CopiesRO = copiesRo;
                ctx.HadSampled = hadSampled;
                var anyClosed = (ctx.Closed0 | ctx.Closed1) != 0;
                var sink = new UnityPulseSink { Cursor = (KindCursor*)UnsafeUtility.AddressOf(ref ctx), EntityHandle = EntityHandle, Entities = null, ThreadIndex = ThreadIndex, IsActive = true };
                if (Space != null && clocks == null)
                {
                    TickDense(in chunk, in tables, ref ctx, ref sink, playbacks, playing, copiesRo, anyClosed, horizons, reverse, playbackChanged);
                    return;
                }
                if (clocks == null && horizons == null)
                {
                    var allPlaying = count <= 64
                        ? playing.ULong0 == (count == 64 ? ~0ul : (1ul << count) - 1)
                        : playing.ULong0 == ~0ul && playing.ULong1 == (count == 128 ? ~0ul : (1ul << (count - 64)) - 1);
                    if (allPlaying)
                    {
                        if (copiesRo == null && !anyClosed)
                        {
                            for (var row = 0; row < count; row++)
                            {
                                var slots = playbacks[row];
                                if (!global::Tl.KindRules.TickEntity(in tables, (global::Tl.Playback*)slots.GetUnsafePtr(), slots.Length, ref ctx, row, StepDir, ref sink))
                                    ctx.SetPlaying(ref PlayingHandle, row, false);
                            }
                        }
                        else
                        {
                            for (var row = 0; row < count; row++)
                            {
                                if (copiesRo != null && copiesRo[row].Copies != 0) ctx.Sampled(row, 0);
                                sink.IsActive = !anyClosed || !ctx.IsClosed(row);
                                var slots = playbacks[row];
                                if (!global::Tl.KindRules.TickEntity(in tables, (global::Tl.Playback*)slots.GetUnsafePtr(), slots.Length, ref ctx, row, StepDir, ref sink))
                                    ctx.SetPlaying(ref PlayingHandle, row, false);
                            }
                        }
                    }
                    else
                    {
                        for (var kw = 0; kw < 2; kw++)
                        {
                            if (kw == 1 && count <= 64) break;
                            var kb = kw == 0 ? playing.ULong0 : playing.ULong1;
                            while (kb != 0)
                            {
                                var row = (kw << 6) + (int)global::Unity.Mathematics.math.tzcnt(kb);
                                kb &= kb - 1;
                                if (row >= count) break;
                                if (copiesRo != null && copiesRo[row].Copies != 0) ctx.Sampled(row, 0);
                                sink.IsActive = !anyClosed || !ctx.IsClosed(row);
                                var slots = playbacks[row];
                                if (!global::Tl.KindRules.TickEntity(in tables, (global::Tl.Playback*)slots.GetUnsafePtr(), slots.Length, ref ctx, row, StepDir, ref sink))
                                    ctx.SetPlaying(ref PlayingHandle, row, false);
                            }
                        }
                    }
                    return;
                }
                for (var kw = 0; kw < 2; kw++)
                {
                    if (kw == 1 && count <= 64) break;
                    var kb = kw == 0 ? playing.ULong0 : playing.ULong1;
                    while (kb != 0)
                    {
                        var row = (kw << 6) + (int)global::Unity.Mathematics.math.tzcnt(kb);
                        kb &= kb - 1;
                        if (row >= count) break;
                        if (copiesRo != null && copiesRo[row].Copies != 0) ctx.Sampled(row, 0);
                        sink.IsActive = !anyClosed || !ctx.IsClosed(row);
                        if (clocks != null)
                        {
                            if (horizons[row] != 0)
                            {
                                horizons[row]--;
                                clocks[row] = (ushort)(clocks[row] + StepDir);
                            }
                            else if (!TickLazy(in tables, ref ctx, playbacks[row], dues[row], ref clocks[row], ref horizons[row], row, reverse, ref sink))
                                ctx.SetPlaying(ref PlayingHandle, row, false);
                            continue;
                        }
                        var slots = playbacks[row];
                        var steps = horizons[row];
                        if (steps != 0)
                        {
                            horizons[row] = (byte)(steps - 1);
                            global::Tl.GlideKernel.RunScalar((ulong*)slots.GetUnsafePtr(), slots.Length, reverse);
                            continue;
                        }
                        if (global::Tl.KindRules.TickEntity(in tables, (global::Tl.Playback*)slots.GetUnsafePtr(), slots.Length, ref ctx, row, StepDir, ref sink))
                            horizons[row] = global::Tl.Horizon.OfKindRun((global::Tl.Playback*)slots.GetUnsafePtr(), slots.Length, in tables, in Index.Calm, reverse, HorizonLimit);
                        else
                            ctx.SetPlaying(ref PlayingHandle, row, false);
                    }
                }
            }
        }
    }

    public unsafe partial struct KindTickJob
    {
        void TickDense(in ArchetypeChunk chunk, in global::Tl.KindTables tables, ref KindCursor ctx, ref UnityPulseSink sink, BufferAccessor<Playback> playbacks, v128 playing, TimelinePlaying* copiesRo, bool anyClosed, byte* horizons, ulong reverse, bool playbackChanged)
        {
            var count = chunk.Count;
            var from = DenseFrom;
            var to = (ushort)(from + StepDir);
            var fast = Index.TickFastPulses != null ? *Index.TickFastPulses : 0ul;
            var ready = DenseReady && Dense != null && Dense->Ready;
            var settleAll = DenseForget || !ready || playbackChanged || (Dense->Lanes & ~fast) != 0;
            var leave = ready ? Dense->Leave + (StepDir > 0 ? 0 : Dense->Rows) : null;
            ulong pending0 = 0, pending1 = 0;
            if (!settleAll)
            {
                ctx.EnsureReach();
                for (var kinds = ctx.Reach & ~Dense->SpanKinds; kinds != 0; kinds &= kinds - 1)
                {
                    var active = chunk.GetEnableableBits(ref *KindHandleSet.Active(ctx.Handles, (int)global::Unity.Mathematics.math.tzcnt(kinds)));
                    pending0 |= active.ULong0;
                    pending1 |= active.ULong1;
                }
            }
            for (var kw = 0; kw < 2; kw++)
            {
                if (kw == 1 && count <= 64) break;
                var kb = kw == 0 ? playing.ULong0 : playing.ULong1;
                var pendingWord = kw == 0 ? pending0 : pending1;
                while (kb != 0)
                {
                    var bit = (int)global::Unity.Mathematics.math.tzcnt(kb);
                    kb &= kb - 1;
                    var row = (kw << 6) + bit;
                    if (row >= count) break;
                    if (copiesRo != null && copiesRo[row].Copies != 0) ctx.Sampled(row, 0);
                    sink.IsActive = !anyClosed || !ctx.IsClosed(row);
                    var buffer = playbacks[row];
                    var raw = (global::Tl.Playback*)buffer.GetUnsafePtr();
                    var n = buffer.Length;
                    var pending = (pendingWord >> bit & 1) != 0;
                    if (DenseSlots.IsDense(raw, n))
                    {
                        if (!settleAll && !pending && (DenseSlots.DueOf(raw) != to || StepDense(raw, n, leave, from, to))) continue;
                        if (DenseSlots.IsDense(raw, n)) DenseSlots.Settle(Space, raw, n, from);
                        if (horizons != null) horizons[row] = 0;
                    }
                    else if (!settleAll && !pending && (copiesRo == null || copiesRo[row].Copies == 0) && Densify(raw, n, leave, from))
                    {
                        if (StepDense(raw, n, leave, from, to)) continue;
                        if (horizons != null) horizons[row] = 0;
                    }
                    if (horizons != null)
                    {
                        var steps = horizons[row];
                        if (steps != 0)
                        {
                            horizons[row] = (byte)(steps - 1);
                            global::Tl.GlideKernel.RunScalar((ulong*)raw, n, reverse);
                            continue;
                        }
                        if (global::Tl.KindRules.TickEntity(in tables, raw, n, ref ctx, row, StepDir, ref sink))
                            horizons[row] = global::Tl.Horizon.OfKindRun(raw, playbacks[row].Length, in tables, in Index.Calm, reverse, HorizonLimit);
                        else
                            ctx.SetPlaying(ref PlayingHandle, row, false);
                        continue;
                    }
                    if (!global::Tl.KindRules.TickEntity(in tables, raw, n, ref ctx, row, StepDir, ref sink))
                        ctx.SetPlaying(ref PlayingHandle, row, false);
                }
            }
        }

        bool Densify(global::Tl.Playback* raw, int n, uint* leave, ushort from)
        {
            if (n == 0 || n > KindDense.MaxSlots) return false;
            var space = Space;
            for (var i = 0; i < n; i++)
            {
                var s = raw + i;
                var t = s->Timeline - 1;
                if ((uint)t >= (uint)space->Timelines || (s->Flags & ~global::Tl.PlaybackFlags.Reverse) != 0 || (s->Rate | s->Phase) != 0) return false;
                if (Dense->Eligible[t] == 0 || s->Tick >= space->Duration[t]) return false;
            }
            if (raw->Channel != space->Channel[raw->Timeline - 1]) return false;
            var least = 0xFFFFu;
            for (var i = 0; i < n; i++)
            {
                var s = raw + i;
                var row = space->RowOf(s->Timeline, s->Flags != 0, s->Tick);
                var gap = leave[row] & 0xFFFFu;
                if (gap < least) least = gap;
                s->Tick = (ushort)(row - from);
            }
            raw->Phase = DenseSlots.Marker;
            DenseSlots.SetDue(raw, (ushort)(from + StepDir * (int)least));
            return true;
        }

        bool StepDense(global::Tl.Playback* raw, int n, uint* leave, ushort from, ushort to)
        {
            if (DenseSlots.DueOf(raw) != to) return true;
            for (var i = 0; i < n; i++)
            {
                var record = leave[(ushort)(raw[i].Tick + from)];
                if (record == 1u)
                {
                    DenseSlots.Settle(Space, raw, n, from);
                    return false;
                }
            }
            var least = 0xFFFFu;
            for (var i = 0; i < n; i++)
            {
                var origin = raw[i].Tick;
                var record = leave[(ushort)(origin + from)];
                var gap = record & 0xFFFFu;
                if (gap == 1)
                {
                    origin = (ushort)(origin + (short)(record >> 16));
                    raw[i].Tick = origin;
                    gap = leave[(ushort)(origin + to)] & 0xFFFFu;
                }
                else gap--;
                if (gap < least) least = gap;
            }
            DenseSlots.SetDue(raw, (ushort)(to + StepDir * (int)least));
            return true;
        }

        void Unclock(in ArchetypeChunk chunk, int count)
        {
            var clocks = (ushort*)chunk.GetComponentDataPtrRO(ref ClockHandle);
            var ticking = false;
            for (var row = 0; row < count && !ticking; row++)
                ticking = clocks[row] != 0;
            if (ticking)
            {
                clocks = (ushort*)chunk.GetNativeArray(ref ClockHandle).GetUnsafePtr();
                var all = chunk.GetBufferAccessor(ref PlaybackHandle);
                for (var row = 0; row < count; row++)
                {
                    if (clocks[row] == 0) continue;
                    TimelineTicks.Decode((global::Tl.Playback*)all[row].GetUnsafePtr(), all[row].Length, clocks[row]);
                    clocks[row] = 0;
                }
            }
        }

        void DueNow(DynamicBuffer<PlaybackDue> dues, int length)
        {
            dues.ResizeUninitialized(length);
            var d = (ushort*)dues.GetUnsafePtr();
            for (var i = 0; i < length; i++) d[i] = (ushort)(Now & TimelineTicks.Mask);
        }

        [global::Unity.Burst.CompilerServices.SkipLocalsInit]
        bool TickLazy(in global::Tl.KindTables tables, ref KindCursor ctx, DynamicBuffer<Playback> buffer, DynamicBuffer<PlaybackDue> dueBuffer, ref ushort clock, ref byte horizon, int row, ulong reverse, ref UnityPulseSink sink)
        {
            var before = clock;
            var after = (ushort)(clock + StepDir);
            clock = after;
            var n = buffer.Length;
            if (dueBuffer.Length != n) DueNow(dueBuffer, n);
            var d = (ushort*)dueBuffer.GetUnsafePtr();
            var pb = (global::Tl.Playback*)buffer.GetUnsafePtr();
            var now = (ushort)(Now & TimelineTicks.Mask);
            var hits = stackalloc byte[KindSlots.MaxSlots];
            var live = false;
            var near = TimelineTicks.MaxCalm + 1;
            var held = DueScan.Collect(d, n, now, hits, ref near, ref live);
            var rated = false;
            for (var k = 0; k < held; k++)
                rated |= pb[hits[k]].Rate != 0;
            if (rated)
            {
                TimelineTicks.Decode(pb, n, before);
                live = global::Tl.KindRules.TickEntity(in tables, pb, n, ref ctx, row, StepDir, ref sink);
                pb = (global::Tl.Playback*)buffer.GetUnsafePtr();
                for (var i = 0; i < n; i++)
                {
                    d[i] = TimelineTicks.Due(pb + i, in tables, in Index.Calm, reverse, now);
                    TimelineTicks.Encode(pb + i, after);
                }
                horizon = 0;
                return live;
            }
            for (var k = 0; k < held; k++)
            {
                var i = hits[k];
                var s = pb + i;
                TimelineTicks.Decode(s, before);
                live |= global::Tl.KindRules.TickSlot(in tables, ref ctx, row, i, s, StepDir);
                var due = TimelineTicks.Due(s, in tables, in Index.Calm, reverse, now);
                TimelineTicks.Encode(s, after);
                d[i] = due;
                if ((due & TimelineTicks.Inert) != 0) continue;
                live = true;
                var distance = (due - now) & TimelineTicks.Mask;
                if (distance < near) near = distance;
            }
            var limit = HorizonLimit < global::Tl.Horizon.Max ? HorizonLimit : global::Tl.Horizon.Max;
            horizon = (byte)(near - 1 < limit ? near - 1 : limit);
            return live;
        }
    }

    public unsafe struct KindSlots : global::Tl.IPlaybackSlots
    {
        DynamicBuffer<Playback> _buffer;
        global::Tl.Playback* _items;
        int _length;
        readonly KindCursor* _cursor;
        readonly int _row;

        public const int MaxSlots = 256;

        public KindSlots(DynamicBuffer<Playback> buffer, KindCursor* cursor, int row)
        {
            _buffer = buffer;
            _items = (global::Tl.Playback*)buffer.GetUnsafePtr();
            _length = buffer.Length;
            _cursor = cursor;
            _row = row;
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _length;
        }

        public ref global::Tl.Playback this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _items[index];
        }

        public bool StartsPending => true;

        public bool TryAdd(in global::Tl.Playback playback)
        {
            if (_length >= MaxSlots) return false;
            var copy = playback;
            _buffer.Add(*(Playback*)&copy);
            _items = (global::Tl.Playback*)_buffer.GetUnsafePtr();
            _length++;
            return true;
        }

        public bool Admit(ushort timeline)
        {
            _cursor->EnsureReach();
            if (_cursor->Tables->Reaches(timeline, _cursor->Reach)) return true;
            _cursor->Counters[KindCounter.DroppedStarts]++;
            return false;
        }

        public void Close(int index)
        {
            _cursor->MarkClosed(_row);
            global::Tl.KindRules.Close(in *_cursor->Tables, ref *_cursor, _row, ref _items[index], index);
        }

        public void Purge(int index) => _cursor->Purge(_row, (byte)index, _items[index].Timeline);
    }

    public unsafe struct KindCursor : global::Tl.IKindRows
    {
        public KindIndex* Index;
        public global::Tl.KindTables* Tables;
        public ArchetypeChunk Chunk;
        public KindHandleSet* Handles;
        public UnsafeUntypedBufferAccessor* Buffers;
        public int* Counters;
        public ulong Reach;
        public bool HasReach;
        public ulong Taken;
        public EnabledMask* Masks;
        public ulong Masked;
        public ulong SlowMarked;
        public ulong Closed0;
        public ulong Closed1;
        public bool SampledMarked;
        public ComponentTypeHandle<TimelinePlaying>* PlayingHandlePtr;
        public EnabledMask Playing;
        public bool HasPlaying;
        public BufferAccessor<Playback> Playbacks;
        public KindWorkerCache* Cache;
        public bool HadSampled;

        public void Bind(in ArchetypeChunk chunk)
        {
            Chunk = chunk;
            Reach = 0;
            HasReach = false;
            Taken = 0;
            Masked = 0;
            SlowMarked = 0;
            Closed0 = 0;
            Closed1 = 0;
            SampledMarked = false;
            HasPlaying = false;
            Copies = null;
            CopiesRO = null;
            Cache = null;
            HadSampled = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkClosed(int row)
        {
            if (row < 64) Closed0 |= 1ul << row;
            else Closed1 |= 1ul << (row - 64);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsClosed(int row) => (((row < 64 ? Closed0 : Closed1) >> (row & 63)) & 1) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureReach()
        {
            if (HasReach) return;
            HasReach = true;
            ulong r = 0;
            for (var k = 0; k < Index->KindCount; k++)
                if (KindHandleSet.IsMapped(Handles, k) && Chunk.Has(ref *KindHandleSet.Entry(Handles, k)))
                    r |= 1ul << k;
            Reach = r;
            if (Cache != null)
            {
                Cache->Reach = r;
                Cache->Flags |= 32;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetActive(int row, int kind, bool value)
        {
            if (Masks == null)
            {
                Chunk.SetComponentEnabled(ref *KindHandleSet.Active(Handles, kind), row, value);
                return;
            }
            if ((Masked & (1ul << kind)) == 0)
            {
                Masks[kind] = Chunk.GetEnabledMask(ref *KindHandleSet.Active(Handles, kind));
                Masked |= 1ul << kind;
            }
            Masks[kind][row] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetPlaying(ref ComponentTypeHandle<TimelinePlaying> handle, int row, bool value)
        {
            if (!HasPlaying)
            {
                Playing = Chunk.GetEnabledMask(ref handle);
                HasPlaying = true;
            }
            Playing[row] = value;
        }

        internal UnsafeUntypedBufferAccessor* Buffer(int kind)
        {
            if ((Taken & (1ul << kind)) == 0)
            {
                Buffers[kind] = Chunk.GetUntypedBufferAccessor(ref *KindHandleSet.Entry(Handles, kind));
                Taken |= 1ul << kind;
            }
            return Buffers + kind;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public global::Tl.KindEntry* Append(int row, global::Tl.LibraryFeeder* feeder)
        {
            var entry = AppendEntry(row, feeder->Kind);
            if (entry == null) return null;
            var route = Index->Routes[feeder - Index->Sec.Feeders];
            entry->Feeder = (byte)route;
            entry->Owner = (byte)(route >> 8);
            return entry;
        }

        public global::Tl.KindEntry* AppendEntry(int row, int kind)
        {
            EnsureReach();
            if ((Reach & (1ul << kind)) == 0) return null;
            var stride = Index->EntryStride[kind];
            var buffer = Buffer(kind);
            var length = buffer->GetBufferLength(row);
            buffer->ResizeUninitialized(row, length + 1);
            var entry = (byte*)buffer->GetUnsafePtr(row) + (long)length * stride;
            UnsafeUtility.MemClear(entry, stride);
            if (length == 0) SetActive(row, kind, true);
            if ((SlowMarked & (1ul << kind)) == 0 && Index->SlowCount != null)
            {
                System.Threading.Interlocked.Increment(ref Index->SlowCount[kind]);
                SlowMarked |= 1ul << kind;
            }
            return (global::Tl.KindEntry*)entry;
        }

        public void Purge(int row, byte slot, ushort timeline)
        {
            EnsureReach();
            for (var kind = 0; kind < Index->KindCount; kind++)
            {
                if ((Reach & (1ul << kind)) == 0) continue;
                var stride = Index->EntryStride[kind];
                var buffer = Buffer(kind);
                var length = buffer->GetBufferLength(row);
                if (length == 0) continue;
                var data = (byte*)buffer->GetUnsafePtr(row);
                var kept = 0;
                for (var i = 0; i < length; i++)
                {
                    var e = (global::Tl.KindEntry*)(data + (long)i * stride);
                    if (e->Slot == slot && e->Timeline == timeline) continue;
                    if (kept != i) UnsafeUtility.MemCpy(data + (long)kept * stride, e, stride);
                    kept++;
                }
                if (kept == length) continue;
                buffer->ResizeUninitialized(row, kept);
                if (kept == 0) SetActive(row, kind, false);
            }
        }

        public global::Tl.Playback* Reserve(int row, int used, int length)
        {
            var buffer = Playbacks[row];
            if (buffer.Capacity < length)
            {
                var live = buffer.Length;
                buffer.ResizeUninitialized(used);
                buffer.EnsureCapacity(length);
                buffer.ResizeUninitialized(live);
            }
            return (global::Tl.Playback*)buffer.GetUnsafePtr();
        }

        public TimelinePlaying* Copies;
        public TimelinePlaying* CopiesRO;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Sampled(int row, int samples)
        {
            if (samples == 0 && !HadSampled) return;
            if (samples != 0 && !SampledMarked && Index->SampledCount != null)
            {
                System.Threading.Interlocked.Increment(ref *Index->SampledCount);
                SampledMarked = true;
            }
            if (Copies == null)
            {
                if (samples == 0)
                {
                    if (CopiesRO == null) CopiesRO = (TimelinePlaying*)Chunk.GetRequiredComponentDataPtrRO(ref *PlayingHandlePtr);
                    if (CopiesRO[row].Copies == 0) return;
                }
                Copies = (TimelinePlaying*)Chunk.GetRequiredComponentDataPtrRW(ref *PlayingHandlePtr);
            }
            if (Copies[row].Copies != (byte)samples) Copies[row].Copies = (byte)samples;
        }
    }

    internal unsafe struct UnityPulseSink : global::Tl.IKindPulseSink
    {
        public KindCursor* Cursor;
        public EntityTypeHandle EntityHandle;
        public Entity* Entities;
        public int ThreadIndex;
        public bool IsActive;

        public bool Active => IsActive;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(in global::Tl.KindTables t, int kind, int row, int cell)
        {
            Cursor->EnsureReach();
            if ((Cursor->Reach & (1ul << kind)) == 0) return;
            if (Entities == null) Entities = (Entity*)Cursor->Chunk.GetNativeArray(EntityHandle).GetUnsafeReadOnlyPtr();
            Cursor->Index->PushPulse(kind, ThreadIndex, in Cursor->Chunk, Entities[row], row, cell);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Rewind(in global::Tl.KindTables t, int kind, int row)
        {
            if (Entities == null) return;
            Cursor->Index->RewindPulse(kind, ThreadIndex, Entities[row]);
        }
    }
}
