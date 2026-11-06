using System;
using System.Runtime.InteropServices;

namespace Tl
{
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct TimelineId
    {
        public readonly ushort Value;
        public TimelineId(ushort value) { Value = value; }
        public bool IsNone => Value == 0;
        public static TimelineId None => default;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct LibraryView
    {
        public const uint AbiVersion = 2u;
        public uint Version;
        public int TimelineCount;
        public LibraryTimeline* Timelines;
        public uint ByteCount;
        public KindSectionView Kinds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct LibraryTimeline
    {
        public ushort Id;
        public ushort Channel;
        public ushort Next;
        public byte Loop;
        public byte Hold;
        public uint Duration;
        public int StageCount;
        public LibraryStage* Stages;
        public int EventCount;
        public LibraryEvent* Events;
        public ulong* EventTickBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct LibraryStage
    {
        public uint Start;
        public uint End;
        public int StepCount;
        public LibraryStep* Steps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct LibraryStep
    {
        public uint Pair;
        public ulong PairKey;
        public int TrackIndex;
        public uint WindowStart;
        public uint WindowEnd;
        public uint FactorStart;
        public uint FactorSpan;
        public ushort TrackValueIndex;
        public ushort FirstValueIndex;
        public ushort SecondValueIndex;
        public void* FoldLane;
        public void* FrameRow;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibraryEvent
    {
        public uint Tick;
        public byte Kind;
        public byte _pad0;
        public ushort _pad1;
        public int StepIndex;
        public uint Marker;
    }

    public static unsafe class LibraryViewReader
    {
        public static LibraryTimeline* GetTimeline(in LibraryView view, TimelineId id)
        {
            if (id.Value == 0 || id.Value > view.TimelineCount) return null;
            return view.Timelines + (id.Value - 1);
        }

        public static LibraryStage* StageAt(LibraryTimeline* tl, ushort tick)
        {
            if (tl == null) return null;
            var n = tl->StageCount;
            var stages = tl->Stages;
            if (n <= 4)
            {
                for (var i = 0; i < n; i++) if (tick < stages[i].End) return stages + i;
                return null;
            }
            int lo = 0, hi = n - 1;
            while (lo <= hi)
            {
                var m = (lo + hi) >> 1;
                if (tick < stages[m].End) { if (m == 0 || tick >= stages[m - 1].End) return stages + m; hi = m - 1; }
                else lo = m + 1;
            }
            return null;
        }

        public static Span<LibraryStep> StepsFor(LibraryTimeline* tl, LibraryStage* st)
        {
            if (tl == null || st == null) return Span<LibraryStep>.Empty;
            return new Span<LibraryStep>(st->Steps, st->StepCount);
        }

        public static void* FoldLane(LibraryStep* step, int lane)
        {
            if (step == null || lane != 0) return null;
            return step->FoldLane;
        }

        public static Span<LibraryEvent> Events(LibraryTimeline* tl)
        {
            if (tl == null || tl->EventCount == 0) return Span<LibraryEvent>.Empty;
            return new Span<LibraryEvent>(tl->Events, tl->EventCount);
        }

        public static bool HasEvent(LibraryTimeline* tl, uint tick)
        {
            if (tl == null || tl->EventTickBits == null || tick >= tl->Duration) return false;
            return (tl->EventTickBits[tick >> 6] & (1UL << (int)(tick & 63))) != 0;
        }

        public static LibraryStep* FrameAt(LibraryTimeline* tl, ushort tick, int stepIndex)
        {
            var st = StageAt(tl, tick);
            if (st == null || stepIndex < 0 || stepIndex >= st->StepCount) return null;
            var step = st->Steps + stepIndex;
            if (tick < step->WindowStart || tick >= step->WindowEnd) return null;
            return step;
        }
    }
}
