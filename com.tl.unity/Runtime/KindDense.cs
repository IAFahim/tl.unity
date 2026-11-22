using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Tl.Unity
{
    public unsafe struct KindDense
    {
        public const int MaxSlots = 256;

        public int* RowBase;
        public int Half;
        public int Rows;
        public ushort* Gap;
        public ulong* Leave;
        public ulong* LeaveKinds;
        public byte* Eligible;
        public int TableEpoch;
        public int EligibleEpoch;
        public bool Ready;

        public static KindDense* Create(global::Tl.LibraryTimeline* timelines, int timelineCount)
        {
            var dense = (KindDense*)Zeroed(UnsafeUtility.SizeOf<KindDense>());
            var count = Math.Max(1, timelineCount);
            dense->RowBase = (int*)Zeroed((long)count * 4);
            dense->Eligible = (byte*)Zeroed(count);
            dense->Half = timelineCount == 0 ? -1 : global::Tl.KindBuild.RowSpace(timelines, timelineCount, dense->RowBase);
            dense->Rows = dense->Half > 0 ? 1 + 2 * dense->Half : 0;
            dense->EligibleEpoch = -1;
            return dense;
        }

        public bool Fits => Half > 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int RowOf(int timeline, int duration, bool reverse, int tick) => RowBase[timeline] + (reverse ? Half + duration - 1 - tick : tick);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GapOf(int timeline, int direction, int tick) => Gap[(direction > 0 ? 0 : Half) + RowBase[timeline] - 1 + tick];

        public static void Refresh(ref KindIndex index)
        {
            var dense = index.Dense;
            if (dense == null) return;
            dense->EligibleEpoch = dense->TableEpoch;
            dense->Ready = false;
            if (!dense->Fits) return;
            var anyTable = false;
            for (var k = 0; k < index.KindCount; k++) anyTable |= index.Values[k].Data != null;
            if (!anyTable) return;
            if (dense->Leave == null && !BuildLeave(in index, dense))
            {
                dense->Half = -1;
                dense->Rows = 0;
                return;
            }
            for (var t = 0; t < index.TimelineCount; t++)
            {
                var eligible = index.SpanMeta[t] != 0;
                for (var p = index.PairStart[t]; eligible && p < index.PairStart[t + 1]; p++)
                {
                    var feeder = index.Sec.Feeders + (index.Pairs[p] & 0xffff);
                    if (feeder->Trigger == 0 && (index.Values[feeder->Kind].Data == null || index.HasForeign(feeder->Kind))) eligible = false;
                }
                dense->Eligible[t] = eligible ? (byte)1 : (byte)0;
            }
            dense->Ready = true;
        }

        static bool BuildLeave(in KindIndex index, KindDense* dense)
        {
            var half = dense->Half;
            var rows = dense->Rows;
            dense->Gap = (ushort*)Zeroed((long)Math.Max(1, 2 * half) * 2);
            for (var t = 0; t < index.TimelineCount; t++)
            {
                var duration = (int)(index.SpanMeta[t] & 0x3FFF_FFFFu);
                var pulseBase = index.PulseBase[t];
                for (var d = 0; d < 2; d++)
                {
                    var direction = d == 0 ? 1 : -1;
                    var gap = dense->Gap + d * half + dense->RowBase[t] - 1;
                    for (var n = 0; n < duration; n++)
                    {
                        var tick = direction > 0 ? duration - 1 - n : n;
                        var next = tick + direction;
                        var stop = (uint)next >= (uint)duration || (pulseBase >= 0 && (index.PulseBits[pulseBase + next] & (1 << d)) != 0);
                        gap[tick] = stop ? (ushort)1 : (ushort)(1 + gap[next]);
                    }
                }
            }
            dense->Leave = (ulong*)Zeroed((long)2 * rows * 8);
            dense->LeaveKinds = (ulong*)Zeroed(256 * 8);
            var distinct = 1;
            for (var w = 0; w < 2; w++)
            {
                var step = w == 0 ? 1 : -1;
                dense->Leave[w * rows] = 1ul | (ulong)(ushort)(-step) << 16;
                for (var t = 0; t < index.TimelineCount; t++)
                {
                    var duration = (int)(index.SpanMeta[t] & 0x3FFF_FFFFu);
                    var pulseBase = index.PulseBase[t];
                    var loops = (index.SpanMeta[t] & 0x8000_0000u) != 0;
                    for (var j = 0; j < duration; j++)
                        for (var reverse = 0; reverse < 2; reverse++)
                        {
                            var tick = reverse != 0 ? duration - 1 - j : j;
                            var row = dense->RowOf(t, duration, reverse != 0, tick);
                            var direction = reverse != 0 ? -step : step;
                            var next = tick + direction;
                            if ((uint)next >= (uint)duration)
                            {
                                if (!loops) continue;
                                next = next < 0 ? duration - 1 : 0;
                            }
                            var d = direction > 0 ? 0 : 1;
                            var pulse = pulseBase >= 0 && (index.PulseBits[pulseBase + next] & (1 << d)) != 0;
                            var cell = pulse ? ((pulseBase + next) << 1) + d : 0;
                            var kinds = 0;
                            if (pulse)
                            {
                                var mask = index.PulseMasks[cell];
                                while (kinds < distinct && dense->LeaveKinds[kinds] != mask) kinds++;
                                if (kinds == 256 || cell > 0xFFFF) return false;
                                if (kinds == distinct) dense->LeaveKinds[distinct++] = mask;
                            }
                            var shift = (ushort)(dense->RowOf(t, duration, reverse != 0, next) - row - step);
                            dense->Leave[w * rows + row] = dense->GapOf(t, direction, next) | (ulong)shift << 16 | (ulong)(uint)cell << 32 | (ulong)(uint)kinds << 48;
                        }
                }
            }
            return true;
        }

        public void Dispose()
        {
            Free(RowBase);
            Free(Gap);
            Free(Leave);
            Free(LeaveKinds);
            Free(Eligible);
            this = default;
        }

        static void* Zeroed(long bytes)
        {
            var p = UnsafeUtility.Malloc(Math.Max(1L, bytes), 16, Allocator.Persistent);
            UnsafeUtility.MemClear(p, Math.Max(1L, bytes));
            return p;
        }

        static void Free(void* p)
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
        public ulong Kinds;
        public int Stride;
        internal int Align;
        internal int Shift;
        public int Rows;
        internal int Half;
        internal bool Filling;

        public static KindDenseBuild Of<TFill>(in global::Tl.KindScore score, global::Tl.LibraryTimeline* timelines, global::Tl.KindValues* views, int* row, ref TFill fill) where TFill : struct, IKindDenseFill
        {
            var half = global::Tl.KindBuild.RowSpace(timelines, score.TimelineCount, row);
            var b = new KindDenseBuild { Score = score, Timelines = timelines, Views = views, Stride = 4, Align = 4, Half = half };
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

        public void Fill<TFill>(byte* data, ref TFill fill) where TFill : struct, IKindDenseFill
        {
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
}
