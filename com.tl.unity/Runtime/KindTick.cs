using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.LowLevel.Unsafe;

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
    public unsafe partial struct KindTickJob : IJobChunk
    {
        [NativeDisableUnsafePtrRestriction] public KindIndex Index;
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

        [BurstDiscard]
        static void CountManagedRun(int* counters) => System.Threading.Interlocked.Increment(ref counters[KindCounter.ManagedRuns]);

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            CountManagedRun(Counters);
            var count = chunk.Count;
            byte* horizons = null;
            ushort* clocks = null;
            var dues = default(BufferAccessor<PlaybackDue>);
            var buffers = stackalloc UnsafeUntypedBufferAccessor[Index.KindCount];
            var masks = stackalloc EnabledMask[Index.KindCount];
            var ctx = new KindCursor { Index = Index, Handles = (KindHandleSet*)UnsafeUtility.AddressOf(ref Handles), Buffers = buffers, Masks = masks, Counters = Counters };
            ctx.Bind(in chunk);
            if (chunk.Has(ref ClockHandle))
            {
                if (chunk.Has(ref DueHandle))
                {
                    clocks = (ushort*)chunk.GetNativeArray(ref ClockHandle).GetUnsafePtr();
                    dues = chunk.GetBufferAccessor(ref DueHandle);
                }
                else
                    Unclock(in chunk, ref ctx, count);
            }
            if (HorizonLimit > 0 && chunk.Has(ref HorizonHandle))
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
            var requested = chunk.Has(ref RequestHandle) && chunk.DidChange(ref RequestHandle, LastSystemVersion);
            if (!requested)
            {
                var idle = chunk.GetEnableableBits(ref PlayingHandle);
                if (idle.ULong0 == 0 && (count <= 64 || idle.ULong1 == 0)) return;
            }
            var playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);
            ctx.Playbacks = playbacks;
            ctx.Copies = (TimelinePlaying*)chunk.GetRequiredComponentDataPtrRW(ref PlayingHandle);
            if (requested)
            {
                var requests = chunk.GetBufferAccessor(ref RequestHandle);
                for (var row = 0; row < count; row++)
                {
                    var queue = requests[row];
                    if (queue.Length == 0) continue;
                    if (horizons != null) horizons[row] = 0;
                    if (clocks != null) TimelineTicks.Decode((global::Tl.Playback*)playbacks[row].GetUnsafePtr(), playbacks[row].Length, clocks[row]);
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
            }
            var playing = chunk.GetEnableableBits(ref PlayingHandle);
            if (playing.ULong0 != 0 || (count > 64 && playing.ULong1 != 0))
            {
                var tables = ctx.Tables;
                var reverse = global::Tl.SlotWord.WorldReverse(StepDir > 0);
                for (var kw = 0; kw < 2; kw++)
                {
                    if (kw == 1 && count <= 64) break;
                    var kb = kw == 0 ? playing.ULong0 : playing.ULong1;
                    while (kb != 0)
                    {
                        var row = (kw << 6) + (int)global::Unity.Mathematics.math.tzcnt(kb);
                        kb &= kb - 1;
                        if (row >= count) break;
                        ctx.Sampled(row, 0);
                        if (clocks != null)
                        {
                            if (horizons[row] != 0)
                            {
                                horizons[row]--;
                                clocks[row] = (ushort)(clocks[row] + StepDir);
                            }
                            else if (!TickLazy(in tables, ref ctx, playbacks[row], dues[row], ref clocks[row], ref horizons[row], row, reverse))
                                ctx.SetPlaying(ref PlayingHandle, row, false);
                            continue;
                        }
                        var slots = playbacks[row];
                        if (horizons == null)
                        {
                            if (!global::Tl.KindRules.TickEntity(in tables, (global::Tl.Playback*)slots.GetUnsafePtr(), slots.Length, ref ctx, row, StepDir))
                                ctx.SetPlaying(ref PlayingHandle, row, false);
                            continue;
                        }
                        var steps = horizons[row];
                        if (steps != 0)
                        {
                            horizons[row] = (byte)(steps - 1);
                            global::Tl.GlideKernel.RunScalar((ulong*)slots.GetUnsafePtr(), slots.Length, reverse);
                            continue;
                        }
                        if (global::Tl.KindRules.TickEntity(in tables, (global::Tl.Playback*)slots.GetUnsafePtr(), slots.Length, ref ctx, row, StepDir))
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
        void Unclock(in ArchetypeChunk chunk, ref KindCursor ctx, int count)
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
        bool TickLazy(in global::Tl.KindTables tables, ref KindCursor ctx, DynamicBuffer<Playback> buffer, DynamicBuffer<PlaybackDue> dueBuffer, ref ushort clock, ref byte horizon, int row, ulong reverse)
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
                live = global::Tl.KindRules.TickEntity(in tables, pb, n, ref ctx, row, StepDir);
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
            if (_cursor->Tables.Reaches(timeline, _cursor->Reach)) return true;
            _cursor->Counters[KindCounter.DroppedStarts]++;
            return false;
        }

        public void Close(int index) => global::Tl.KindRules.Close(in _cursor->Tables, ref *_cursor, _row, ref _items[index], index);

        public void Purge(int index) => _cursor->Purge(_row, (byte)index, _items[index].Timeline);
    }

    public unsafe struct KindCursor : global::Tl.IKindRows
    {
        public KindIndex Index;
        public global::Tl.KindTables Tables;
        public ArchetypeChunk Chunk;
        public KindHandleSet* Handles;
        public UnsafeUntypedBufferAccessor* Buffers;
        public int* Counters;
        public ulong Reach;
        public ulong Taken;
        public EnabledMask* Masks;
        public ulong Masked;
        public EnabledMask Playing;
        public bool HasPlaying;
        public BufferAccessor<Playback> Playbacks;

        public void Bind(in ArchetypeChunk chunk)
        {
            Chunk = chunk;
            Tables = Index.Tables;
            Reach = 0;
            Taken = 0;
            Masked = 0;
            HasPlaying = false;
            for (var k = 0; k < Index.KindCount; k++)
                if (KindHandleSet.IsMapped(Handles, k) && chunk.Has(ref *KindHandleSet.Entry(Handles, k)))
                    Reach |= 1ul << k;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SetActive(int row, int kind, bool value)
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

        UnsafeUntypedBufferAccessor* Buffer(int kind)
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
            var route = Index.Routes[feeder - Index.Sec.Feeders];
            entry->Feeder = (byte)route;
            entry->Owner = (byte)(route >> 8);
            return entry;
        }

        public global::Tl.KindEntry* AppendEntry(int row, int kind)
        {
            if ((Reach & (1ul << kind)) == 0) return null;
            var stride = Index.EntryStride[kind];
            var buffer = Buffer(kind);
            var length = buffer->GetBufferLength(row);
            buffer->ResizeUninitialized(row, length + 1);
            var entry = (byte*)buffer->GetUnsafePtr(row) + (long)length * stride;
            UnsafeUtility.MemClear(entry, stride);
            if (length == 0) SetActive(row, kind, true);
            return (global::Tl.KindEntry*)entry;
        }

        public void Purge(int row, byte slot, ushort timeline)
        {
            for (var kind = 0; kind < Index.KindCount; kind++)
            {
                if ((Reach & (1ul << kind)) == 0) continue;
                var stride = Index.EntryStride[kind];
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

        public void Sampled(int row, int samples)
        {
            if (Copies != null) Copies[row].Copies = (byte)samples;
        }
    }
}
