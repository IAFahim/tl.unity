using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Tl.Unity
{
    public unsafe struct TimelineDense : IComponentData
    {
        public ushort Clock;
        public sbyte Direction;
        [NativeDisableUnsafePtrRestriction] public DenseSpace* Space;
    }

    public unsafe struct TimelineDenseChunk : IComponentData
    {
        public ulong Dense0;
        public ulong Dense1;
        public ushort WalkClock;
        public ushort SkipClock;
        public byte State;
        public fixed ushort Since[128];
    }

    public unsafe struct DenseSpace
    {
        public int* RowBase;
        public ushort* Duration;
        public byte* Channel;
        public int Half;
        public int Timelines;

        public static DenseSpace* Of(KindDense* dense, global::Tl.LibraryTimeline* timelines, int count)
        {
            var space = (DenseSpace*)KindDense.Zeroed(UnsafeUtility.SizeOf<DenseSpace>());
            var n = Math.Max(1, count);
            space->RowBase = (int*)KindDense.Zeroed((long)n * 4);
            space->Duration = (ushort*)KindDense.Zeroed((long)n * 2);
            space->Channel = (byte*)KindDense.Zeroed(n);
            space->Half = dense->Half;
            space->Timelines = count;
            for (var t = 0; t < count; t++)
            {
                space->RowBase[t] = dense->RowBase[t];
                space->Duration[t] = (ushort)timelines[t].Duration;
                space->Channel[t] = (byte)timelines[t].Channel;
            }
            return space;
        }

        public static void Release(DenseSpace* space)
        {
            if (space == null) return;
            KindDense.Free(space->RowBase);
            KindDense.Free(space->Duration);
            KindDense.Free(space->Channel);
            KindDense.Free(space);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort RowOf(int timeline, bool reverse, int tick)
        {
            var t = timeline - 1;
            return (ushort)(RowBase[t] + (reverse ? Half + Duration[t] - 1 - tick : tick));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort TickAt(int timeline, bool reverse, ushort row)
        {
            var t = timeline - 1;
            var j = row - RowBase[t] - (reverse ? Half : 0);
            return (ushort)(reverse ? Duration[t] - 1 - j : j);
        }
    }

    public static unsafe class DenseSlots
    {
        public const byte Marker = 0x80;
        public const byte ExactAtDue = 0x40;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsDense(global::Tl.Playback* slots, int count) => count != 0 && (slots->Phase & Marker) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort DueOf(global::Tl.Playback* slot) => (ushort)(slot->Channel | slot->Rate << 8);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ExactEnd(uint record) => (record & 0xFFFFu) == 1 ? record >> 16 == 0 : (record & 0x10000u) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetDue(global::Tl.Playback* slot, ushort due, bool exact)
        {
            slot->Channel = (byte)due;
            slot->Rate = (byte)(due >> 8);
            slot->Phase = (byte)((slot->Phase & Marker) | (exact ? ExactAtDue : 0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Wrap(global::Tl.Playback* slot, uint* leave, ushort from, ushort to, int step)
        {
            var origin = (ushort)(slot->Tick + (short)(leave[(ushort)(slot->Tick + from)] >> 16));
            slot->Tick = origin;
            var record = leave[(ushort)(origin + to)];
            SetDue(slot, (ushort)(to + step * (int)(record & 0xFFFFu)), ExactEnd(record));
        }

        public static void Settle(DenseSpace* space, global::Tl.Playback* slots, int count, ushort clock)
        {
            for (var i = 0; i < count; i++)
            {
                var s = slots + i;
                if (s->Timeline == 0)
                {
                    *(ulong*)s = 0;
                    continue;
                }
                s->Tick = space->TickAt(s->Timeline, s->Flags != 0, (ushort)(s->Tick + clock));
                s->Channel = space->Channel[s->Timeline - 1];
                s->Rate = 0;
                s->Phase = 0;
            }
        }

        public static void Read(DenseSpace* space, ushort clock, global::Tl.Playback* slots, int count, global::Tl.Playback* into)
        {
            UnsafeUtility.MemCpy(into, slots, (long)count * sizeof(global::Tl.Playback));
            if (space != null && IsDense(into, count)) Settle(space, into, count, clock);
        }
    }

    public unsafe struct KindDense
    {
        public const int MaxSlots = 256;
        public const uint Exact = 0;

        public int* RowBase;
        public int Half;
        public int Rows;
        public uint* Leave;
        public byte* Eligible;
        public int* KeyLanes;
        public ulong SpanKinds;
        public ulong LaneKinds;
        public ulong Lanes;
        public byte* Table;
        public int Stride;
        public int CellLane;
        public int TableEpoch;
        public int BuiltEpoch;
        public ushort WalkClock;
        public bool Ready;

        public static KindDense* Create(global::Tl.LibraryTimeline* timelines, int timelineCount)
        {
            var dense = (KindDense*)Zeroed(UnsafeUtility.SizeOf<KindDense>());
            var count = Math.Max(1, timelineCount);
            dense->RowBase = (int*)Zeroed((long)count * 4);
            dense->Eligible = (byte*)Zeroed(count);
            dense->KeyLanes = (int*)Zeroed(64 * 4);
            for (var k = 0; k < 64; k++) dense->KeyLanes[k] = -1;
            dense->CellLane = -1;
            dense->Half = timelineCount == 0 ? -1 : global::Tl.KindBuild.RowSpace(timelines, timelineCount, dense->RowBase);
            dense->Rows = dense->Half > 0 ? 1 + 2 * dense->Half : 0;
            dense->BuiltEpoch = -1;
            return dense;
        }

        public bool Fits => Half > 0;

        public static void Register(KindDense* dense, ulong spanKinds, ulong laneKinds)
        {
            if (dense == null || dense->SpanKinds == spanKinds && dense->LaneKinds == laneKinds) return;
            dense->SpanKinds = spanKinds;
            dense->LaneKinds = laneKinds;
            dense->TableEpoch++;
        }

        public static void Refresh(ref KindIndex index)
        {
            var dense = index.Dense;
            if (dense == null) return;
            dense->BuiltEpoch = dense->TableEpoch;
            dense->Ready = false;
            Free(dense->Leave);
            dense->Leave = null;
            if (!dense->Fits || dense->Table == null || dense->SpanKinds == 0) return;
            var lanes = 0ul;
            for (var k = 0; k < index.KindCount && k < 64; k++)
                if ((dense->LaneKinds >> k & 1) != 0 && index.PulseValuesTable[k].Data != null)
                    lanes |= 1ul << k;
            dense->Lanes = lanes;
            for (var t = 0; t < index.TimelineCount; t++)
            {
                var eligible = index.SpanMeta[t] != 0;
                for (var p = index.PairStart[t]; eligible && p < index.PairStart[t + 1]; p++)
                {
                    var feeder = index.Sec.Feeders + (index.Pairs[p] & 0xffff);
                    if (feeder->Trigger != 0) continue;
                    var kind = feeder->Kind;
                    if (kind >= 64 || (dense->SpanKinds >> kind & 1) == 0 || index.Values[kind].Data != dense->Table || index.HasForeign(kind)) eligible = false;
                }
                dense->Eligible[t] = eligible ? (byte)1 : (byte)0;
            }
            BuildLeave(in index, dense, lanes);
            dense->Ready = true;
        }

        static void BuildLeave(in KindIndex index, KindDense* dense, ulong lanes)
        {
            var half = dense->Half;
            var rows = dense->Rows;
            dense->Leave = (uint*)Zeroed((long)2 * rows * 4);
            for (var w = 0; w < 2; w++)
            {
                var step = w == 0 ? 1 : -1;
                var leave = dense->Leave + w * rows;
                for (var t = 0; t < index.TimelineCount; t++)
                {
                    var duration = (int)(index.SpanMeta[t] & 0x3FFF_FFFFu);
                    var loops = (index.SpanMeta[t] & 0x8000_0000u) != 0;
                    var pulseBase = index.PulseBase[t];
                    for (var reverse = 0; reverse < 2; reverse++)
                    {
                        var start = dense->RowBase[t] + (reverse != 0 ? half : 0);
                        var dir = reverse != 0 ? -step : step;
                        for (var n = 0; n < duration; n++)
                        {
                            var j = step > 0 ? duration - 1 - n : n;
                            var row = start + j;
                            var tick = reverse != 0 ? duration - 1 - j : j;
                            var next = tick + dir;
                            var exact = false;
                            var shift = 0;
                            var arrives = true;
                            if ((uint)next >= (uint)duration)
                            {
                                if (loops)
                                {
                                    next = next < 0 ? duration - 1 : 0;
                                    shift = start + (reverse != 0 ? duration - 1 - next : next) - row - step;
                                    exact = Stops(in index, pulseBase, next, dir, lanes);
                                }
                                else exact = true;
                            }
                            else
                            {
                                exact = Stops(in index, pulseBase, next, dir, lanes);
                                arrives = exact;
                            }
                            var gap = arrives ? 1u : 1u + (leave[row + step] & 0xFFFFu);
                            var end = arrives ? exact : DenseSlots.ExactEnd(leave[row + step]);
                            leave[row] = arrives ? gap | (exact ? 0u : (uint)(ushort)shift << 16) : gap | (end ? 0x10000u : 0u);
                        }
                    }
                }
            }
        }

        static bool Stops(in KindIndex index, int pulseBase, int tick, int dir, ulong lanes)
        {
            if (pulseBase < 0) return false;
            var d = dir > 0 ? 0 : 1;
            if ((index.PulseBits[pulseBase + tick] & (1 << d)) == 0) return false;
            return (index.PulseMasks[((pulseBase + tick) << 1) + d] & ~lanes) != 0;
        }

        public void Dispose()
        {
            Free(RowBase);
            Free(Leave);
            Free(Eligible);
            Free(KeyLanes);
            this = default;
        }

        internal static void* Zeroed(long bytes)
        {
            var p = UnsafeUtility.Malloc(Math.Max(1L, bytes), 16, Allocator.Persistent);
            UnsafeUtility.MemClear(p, Math.Max(1L, bytes));
            return p;
        }

        internal static void Free(void* p)
        {
            if (p != null) UnsafeUtility.Free(p, Allocator.Persistent);
        }

        internal static void Release(KindDense* dense)
        {
            if (dense == null) return;
            dense->Dispose();
            UnsafeUtility.Free(dense, Allocator.Persistent);
        }
    }

    public unsafe struct KindDenseBuild
    {
        internal global::Tl.KindScore Score;
        internal global::Tl.LibraryTimeline* Timelines;
        internal global::Tl.KindValues* Views;
        internal int* RowBase;
        internal int* PulseBase;
        internal byte* PulseBits;
        internal byte* Data;
        public ulong Kinds;
        public int Stride;
        internal int Align;
        internal int Shift;
        public int Rows;
        internal int Half;
        internal bool Filling;
        internal int CellLane;
        internal fixed int Keys[64];

        public static KindDenseBuild Of<TFill>(in global::Tl.KindScore score, global::Tl.LibraryTimeline* timelines, global::Tl.KindValues* views, int* row, int* pulseBase, byte* pulseBits, ref TFill fill) where TFill : struct, IKindDenseFill
        {
            var half = global::Tl.KindBuild.RowSpace(timelines, score.TimelineCount, row);
            var b = new KindDenseBuild { Score = score, Timelines = timelines, Views = views, RowBase = row, PulseBase = pulseBase, PulseBits = pulseBits, Stride = 4, Align = 4, Half = half, CellLane = -1 };
            for (var k = 0; k < 64; k++) b.Keys[k] = -1;
            fill.Add(ref b);
            b.Stride = (b.Stride + b.Align - 1) / b.Align * b.Align;
            b.Rows = 1 + 2 * half;
            var owner = 0;
            var rest = (long)Math.Max(1, score.TimelineCount) * 4 + (long)b.Rows * b.Stride * 2;
            for (var k = 0; k < 64; k++)
                if ((b.Kinds >> k & 1) != 0)
                {
                    views[k].Row = row;
                    views[k].Stride = b.Stride;
                    views[k].TimelineCount = score.TimelineCount;
                    views[k].Rows = b.Rows;
                    views[k].Reversed = half;
                    if (views[k].Shift == 0) owner = k;
                    else rest -= views[k].Bytes = (long)b.Rows * 2 * (views[k].Back - views[k].Front);
                    views[k].Back = views[k].Front + b.Rows * b.Stride;
                }
            if (b.Kinds != 0) views[owner].Bytes = rest;
            return b;
        }

        public void Add<T, TSpans>(int kind, int part, ushort* routes, ref TSpans spans, T identity) where T : unmanaged where TSpans : struct, global::Tl.IKindSpanValues<T>
        {
            if ((uint)kind >= 64 || routes == null) return;
            var v = Views + kind;
            if (Filling)
            {
                if ((Kinds >> kind & 1) != 0) v->Fill(in Score, Timelines, kind, routes, true, ref spans, identity);
                return;
            }
            if ((Kinds >> kind & 1) != 0 || Shift > 27) return;
            var align = Math.Min(64, UnsafeUtility.AlignOf<T>());
            var at = (Stride + align - 1) / align * align;
            *v = new global::Tl.KindValues { Front = at, Back = at + sizeof(T), Shift = Shift, Part = part };
            Stride = at + sizeof(T);
            Align = Math.Max(Align, align);
            Shift += 3;
            Kinds |= 1ul << kind;
        }

        public void Key(int kind, int priorityOffset, int priorityBytes, bool signed)
        {
            if ((uint)kind >= 64) return;
            if (!Filling)
            {
                if ((Kinds >> kind & 1) == 0 || Keys[kind] >= 0) return;
                Stride = (Stride + 3) / 4 * 4;
                Keys[kind] = Stride;
                Stride += 4;
                return;
            }
            if (Keys[kind] < 0) return;
            var v = Views + kind;
            var bias = signed ? 1 << (8 * priorityBytes - 1) : 0;
            for (var h = 0; h < 2; h++)
            {
                var halfAt = Data + (long)h * Rows * Stride;
                var present = 1u << (v->Shift + 1 + h);
                for (var r = 0; r < Rows; r++)
                {
                    var head = *(uint*)(Data + (long)r * Stride);
                    var at = halfAt + (long)r * Stride;
                    var p = at + v->Front + priorityOffset;
                    var priority = priorityBytes == 1 ? (signed ? (int)*(sbyte*)p : *p) : (signed ? (int)*(short*)p : *(ushort*)p);
                    *(int*)(at + Keys[kind]) = (head & present) != 0 ? (priority + bias) << 8 : -1;
                }
            }
        }

        public void Cells()
        {
            if (!Filling)
            {
                if (CellLane >= 0) return;
                Stride = (Stride + 3) / 4 * 4;
                CellLane = Stride;
                Stride += 4;
                return;
            }
            if (CellLane < 0 || PulseBase == null) return;
            for (var t = 0; t < Score.TimelineCount; t++)
            {
                var pb = PulseBase[t];
                if (pb < 0) continue;
                var duration = (int)Timelines[t].Duration;
                for (var tick = 0; tick < duration; tick++)
                {
                    var bits = PulseBits[pb + tick];
                    if (bits == 0) continue;
                    for (var h = 0; h < 2; h++)
                    {
                        var halfAt = Data + (long)h * Rows * Stride;
                        var forward = RowBase[t] + tick;
                        var reversed = RowBase[t] + Half + duration - 1 - tick;
                        var dForward = h;
                        var dReversed = 1 - h;
                        if ((bits & (1 << dForward)) != 0) *(uint*)(halfAt + (long)forward * Stride + CellLane) = (uint)(((pb + tick) << 1) + dForward + 1);
                        if ((bits & (1 << dReversed)) != 0) *(uint*)(halfAt + (long)reversed * Stride + CellLane) = (uint)(((pb + tick) << 1) + dReversed + 1);
                    }
                }
            }
        }

        public void Wide(int bytes)
        {
            if (!Filling) Align = Math.Max(Align, Math.Min(64, bytes));
        }

        internal int KeyOf(int kind) => Keys[kind];

        public void Fill<TFill>(byte* data, ref TFill fill) where TFill : struct, IKindDenseFill
        {
            Data = data;
            for (var k = 0; k < 64; k++)
                if ((Kinds >> k & 1) != 0) Views[k].Data = data;
            Filling = true;
            fill.Add(ref this);
            var half = (long)Rows * Stride;
            for (var r = 0; r < Rows; r++) *(uint*)(data + half + (long)r * Stride) = *(uint*)(data + (long)r * Stride);
        }
    }

    public interface IKindDenseFill
    {
        void Add(ref KindDenseBuild build);
    }

    public struct FusedMixed
    {
        public ArchetypeChunk Chunk;
        public global::Unity.Burst.Intrinsics.v128 Dense;

        public static unsafe FusedMixed* Allocate(int chunks, out int* count)
        {
            count = (int*)UnsafeUtility.Malloc(sizeof(int), 16, Allocator.TempJob);
            *count = 0;
            return (FusedMixed*)UnsafeUtility.Malloc((long)Math.Max(1, chunks) * UnsafeUtility.SizeOf<FusedMixed>(), 16, Allocator.TempJob);
        }
    }

    [global::Unity.Burst.BurstCompile]
    public unsafe struct FusedFreeJob : global::Unity.Jobs.IJob
    {
        [NativeDisableUnsafePtrRestriction] public FusedMixed* Items;
        [NativeDisableUnsafePtrRestriction] public int* Count;

        public void Execute()
        {
            UnsafeUtility.Free(Items, Allocator.TempJob);
            UnsafeUtility.Free(Count, Allocator.TempJob);
        }
    }
}
