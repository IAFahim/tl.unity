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
        public const int Count = 2;
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe struct KindTickJob : IJobChunk
    {
        [NativeDisableUnsafePtrRestriction] public KindIndex Index;
        public BufferTypeHandle<Playback> PlaybackHandle;
        public BufferTypeHandle<TimelineRequest> RequestHandle;
        public ComponentTypeHandle<TimelinePlaying> PlayingHandle;
        [NativeDisableContainerSafetyRestriction] public KindHandleSet Handles;
        [NativeDisableUnsafePtrRestriction] public int* Counters;
        public int StepDir;
        public uint LastSystemVersion;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var count = chunk.Count;
            var requested = chunk.Has(ref RequestHandle) && chunk.DidChange(ref RequestHandle, LastSystemVersion);
            if (!requested)
            {
                var idle = chunk.GetEnableableBits(ref PlayingHandle);
                if (idle.ULong0 == 0 && (count <= 64 || idle.ULong1 == 0)) return;
            }
            var playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);
            var buffers = stackalloc UnsafeUntypedBufferAccessor[Index.KindCount];
            var ctx = new KindCursor { Index = Index, Handles = (KindHandleSet*)UnsafeUtility.AddressOf(ref Handles), Buffers = buffers, Counters = Counters };
            ctx.Bind(in chunk);
            if (requested)
            {
                var requests = chunk.GetBufferAccessor(ref RequestHandle);
                for (var row = 0; row < count; row++)
                {
                    var queue = requests[row];
                    if (queue.Length == 0) continue;
                    var slots = new KindSlots(playbacks[row], &ctx, row);
                    for (var r = 0; r < queue.Length; r++)
                        global::Tl.PlaybackRules.Apply(ref slots, Index.Timelines, Index.TimelineCount, queue[r].Op, queue[r].Timeline, queue[r].At);
                    queue.Clear();
                    chunk.SetComponentEnabled(ref PlayingHandle, row, true);
                }
            }
            var playing = chunk.GetEnableableBits(ref PlayingHandle);
            if (playing.ULong0 != 0 || (count > 64 && playing.ULong1 != 0))
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
                        var slots = playbacks[row];
                        if (!ctx.TickEntity(row, ref slots, StepDir))
                            chunk.SetComponentEnabled(ref PlayingHandle, row, false);
                    }
                }
            }
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

        public void Bind(in ArchetypeChunk chunk)
        {
            Chunk = chunk;
            Tables = Index.Tables;
            Reach = 0;
            Taken = 0;
            for (var k = 0; k < Index.KindCount; k++)
                if (KindHandleSet.IsMapped(Handles, k) && chunk.Has(ref *KindHandleSet.Entry(Handles, k)))
                    Reach |= 1ul << k;
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

        public bool TickEntity(int row, ref DynamicBuffer<Playback> slots, int stepDir)
        {
            var count = slots.Length;
            var cells = (Playback*)slots.GetUnsafePtr();
            var live = false;
            var tls = Index.Timelines;
            var sec = Index.Sec;
            for (var i = 0; i < count; i++)
            {
                var s = cells[i];
                var id = s.Timeline;
                if (id == 0) continue;
                var flags = s.Flags;
                if ((flags & ~global::Tl.PlaybackFlags.Reverse) == 0)
                {
                    var dir = flags == 0 ? stepDir : -stepDir;
                    var t = s.Tick + dir;
                    if ((uint)t < tls[id - 1].Duration)
                    {
                        cells[i].Tick = (ushort)t;
                        if (Index.KindCount != 0 && sec.Timelines[id - 1].KindMask != 0 && (sec.EventBitsAt(sec.Timelines + (id - 1), t) & (dir > 0 ? 1 : 2)) != 0) ApplyEvents(row, i, id, t, dir);
                        live = true;
                        continue;
                    }
                }
                live |= TickSlot(row, i, ref slots, stepDir);
            }
            return live;
        }

        bool TickSlot(int row, int i, ref DynamicBuffer<Playback> slots, int stepDir)
        {
            var s = slots[i];
            s.Flags &= ~global::Tl.PlaybackFlags.Began;
            if ((s.Flags & global::Tl.PlaybackFlags.Stopping) != 0)
            {
                if ((s.Flags & global::Tl.PlaybackFlags.StopAged) != 0)
                {
                    slots[i] = default;
                    return false;
                }
                s.Flags |= global::Tl.PlaybackFlags.StopAged;
                slots[i] = s;
                return true;
            }
            if ((s.Flags & global::Tl.PlaybackFlags.StartPending) != 0)
            {
                Begin(row, i, ref s, stepDir);
                slots[i] = s;
                return true;
            }
            if ((s.Flags & global::Tl.PlaybackFlags.Paused) != 0)
            {
                slots[i] = s;
                return true;
            }
            if ((s.Flags & global::Tl.PlaybackFlags.Ended) != 0)
            {
                slots[i] = s;
                return false;
            }
            var dir = (s.Flags & global::Tl.PlaybackFlags.Reverse) != 0 ? -stepDir : stepDir;
            var id = s.Timeline;
            var tl = Index.Timelines + (id - 1);
            int duration = (int)tl->Duration;
            var t = s.Tick + dir;
            if ((uint)t < (uint)duration)
            {
                s.Tick = (ushort)t;
                slots[i] = s;
                Events(row, i, id, t, dir);
                return true;
            }
            if (tl->Loop != 0)
            {
                WrapRemove(row, i, id, dir);
                t = dir > 0 ? 0 : duration - 1;
                s.Tick = (ushort)t;
                slots[i] = s;
                Events(row, i, id, t, dir);
                return true;
            }
            RemoveActiveSpans(row, i, in s);
            if (tl->Next != 0)
            {
                var next = Index.Timelines + (tl->Next - 1);
                s.Timeline = tl->Next;
                s.Channel = (byte)next->Channel;
                s.Tick = (ushort)(dir > 0 ? 0 : next->Duration - 1);
                Begin(row, i, ref s, stepDir);
                slots[i] = s;
                return true;
            }
            if (tl->Hold != 0)
            {
                s.Tick = (ushort)(duration - 1);
                s.Flags |= global::Tl.PlaybackFlags.Ended;
                slots[i] = s;
                return false;
            }
            slots[i] = default;
            return false;
        }

        void Events(int row, int i, ushort id, int tick, int dir)
        {
            var sec = Index.Sec;
            if (Index.KindCount != 0 && sec.Timelines[id - 1].KindMask != 0 && (sec.EventBitsAt(sec.Timelines + (id - 1), tick) & (dir > 0 ? 1 : 2)) != 0) ApplyEvents(row, i, id, tick, dir);
        }

        void Begin(int row, int i, ref Playback s, int stepDir)
        {
            s.Flags = (s.Flags & ~global::Tl.PlaybackFlags.StartPending) | global::Tl.PlaybackFlags.Began;
            var id = s.Timeline;
            if (Index.KindCount == 0 || Index.Sec.Timelines[id - 1].KindMask == 0) return;
            var dir = (s.Flags & global::Tl.PlaybackFlags.Reverse) != 0 ? -stepDir : stepDir;
            var windows = Index.Sec.Windows(Index.Sec.Timelines + (id - 1));
            int tick = s.Tick;
            for (var k = Index.PairStart[id - 1]; k < Index.PairStart[id]; k++)
            {
                var w = (int)(Index.Pairs[k] >> 16);
                var f = (int)(Index.Pairs[k] & 0xffff);
                var window = windows[w];
                var feeder = Index.Sec.Feeders + f;
                var inside = tick >= window.Start && tick < window.End;
                switch (feeder->Trigger)
                {
                    case 0:
                        if (inside) Append(row, feeder, i, id, w, global::Tl.KindEntryFlags.Span | ((dir > 0 ? tick != window.Start : tick != window.End - 1) ? global::Tl.KindEntryFlags.Synthetic : 0));
                        break;
                    case 1:
                        if (inside) Append(row, feeder, i, id, w, global::Tl.KindEntryFlags.Pulse | (tick != window.Start ? global::Tl.KindEntryFlags.Synthetic : 0));
                        break;
                    case 2:
                        if (tick == window.End - 1) Append(row, feeder, i, id, w, global::Tl.KindEntryFlags.Pulse);
                        break;
                    default:
                        if (tick == window.Start) Append(row, feeder, i, id, w, global::Tl.KindEntryFlags.Pulse);
                        break;
                }
            }
        }

        void RemoveActiveSpans(int row, int i, in Playback s)
        {
            var id = s.Timeline;
            if (Index.KindCount == 0 || Index.Sec.Timelines[id - 1].KindMask == 0) return;
            var windows = Index.Sec.Windows(Index.Sec.Timelines + (id - 1));
            int tick = s.Tick;
            for (var k = Index.PairStart[id - 1]; k < Index.PairStart[id]; k++)
            {
                var w = (int)(Index.Pairs[k] >> 16);
                var feeder = Index.Sec.Feeders + (Index.Pairs[k] & 0xffff);
                if (feeder->Trigger == 0 && tick >= windows[w].Start && tick < windows[w].End) RemoveSpan(row, feeder->Kind, (byte)i, id, (ushort)w);
            }
        }

        void WrapRemove(int row, int i, ushort id, int dir)
        {
            if (Index.KindCount == 0) return;
            var tk = Index.Sec.Timelines + (id - 1);
            if (tk->KindMask == 0) return;
            var events = Index.Sec.Events(tk);
            var from = dir > 0 ? tk->WrapForward : tk->WrapBackward;
            var to = from + (dir > 0 ? tk->WrapForwardCount : tk->WrapBackwardCount);
            for (var n = from; n < to; n++)
            {
                var feeder = Index.Sec.Feeders + events[n].Feeder;
                RemoveSpan(row, feeder->Kind, (byte)i, id, events[n].Window);
            }
        }

        void ApplyEvents(int row, int i, ushort id, int tick, int dir)
        {
            var tk = Index.Sec.Timelines + (id - 1);
            var events = Index.Sec.Events(tk);
            var d = dir > 0 ? 0 : 1;
            var to = Index.Sec.EventEnd(tk, tick, d);
            for (var n = Index.Sec.EventStart(tk, tick, d); n < to; n++)
            {
                var ev = events[n];
                var feeder = Index.Sec.Feeders + ev.Feeder;
                switch ((global::Tl.KindEventOp)ev.Op)
                {
                    case global::Tl.KindEventOp.Remove:
                        RemoveSpan(row, feeder->Kind, (byte)i, id, ev.Window);
                        break;
                    case global::Tl.KindEventOp.Insert:
                        Append(row, feeder, i, id, ev.Window, global::Tl.KindEntryFlags.Span);
                        break;
                    default:
                        Append(row, feeder, i, id, ev.Window, global::Tl.KindEntryFlags.Pulse);
                        break;
                }
            }
        }

        void Append(int row, global::Tl.LibraryFeeder* feeder, int slot, ushort timeline, int window, global::Tl.KindEntryFlags flags)
        {
            var entry = AppendEntry(row, feeder->Kind);
            if (entry == null) return;
            entry->Timeline = timeline;
            entry->Step = (ushort)window;
            entry->Slot = (byte)slot;
            var route = Index.Routes[feeder - Index.Sec.Feeders];
            entry->Feeder = (byte)route;
            entry->Flags = flags;
            entry->Owner = (byte)(route >> 8);
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
            if (length == 0) Chunk.SetComponentEnabled(ref *KindHandleSet.Active(Handles, kind), row, true);
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
                if (kept == 0) Chunk.SetComponentEnabled(ref *KindHandleSet.Active(Handles, kind), row, false);
            }
        }

        public void RemoveSpan(int row, int kind, byte slot, ushort timeline, ushort step)
        {
            if ((Reach & (1ul << kind)) == 0) return;
            var stride = Index.EntryStride[kind];
            var buffer = Buffer(kind);
            var length = buffer->GetBufferLength(row);
            var data = (byte*)buffer->GetUnsafePtr(row);
            for (var i = 0; i < length; i++)
            {
                var e = (global::Tl.KindEntry*)(data + (long)i * stride);
                if ((e->Flags & global::Tl.KindEntryFlags.Span) == 0 || e->Slot != slot || e->Timeline != timeline || e->Step != step) continue;
                var tail = (length - i - 1) * stride;
                if (tail > 0) UnsafeUtility.MemMove(data + (long)i * stride, data + (long)(i + 1) * stride, tail);
                buffer->ResizeUninitialized(row, length - 1);
                if (length == 1) Chunk.SetComponentEnabled(ref *KindHandleSet.Active(Handles, kind), row, false);
                return;
            }
        }
    }
}
