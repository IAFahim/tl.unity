using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;

namespace Tl.Unity
{
    public unsafe struct KindIndex
    {
        public int TimelineCount;
        public int KindCount;
        public global::Tl.LibraryTimeline* Timelines;
        public global::Tl.KindSectionView Sec;
        public ulong* Closure;
        public uint* Pairs;
        public int* PairStart;
        public int* WindowBase;
        public int* FirstStage;
        public int* StageCount;
        public int* StepStart;
        public global::Tl.LibraryStep** StepTable;
        public byte** MarkerValues;
        public int WindowTotal;
        public int StepTableLength;
        public int PairCount;

        public bool IsCreated => Timelines != null || KindCount == 0 && Closure != null;

        public global::Tl.LibraryKind* Kind(int kind) => Sec.Kinds + kind;

        public global::Tl.LibraryStep* StepOf(ushort timeline, int window, ushort tick)
        {
            var t = timeline - 1;
            if ((uint)t >= (uint)TimelineCount) return null;
            var g = WindowBase[t] + window;
            var start = StepStart[g];
            if (start < 0) return null;
            var tl = Timelines + t;
            var stage = global::Tl.LibraryViewReader.StageAt(tl, tick);
            if (stage == null) return null;
            var s = (int)(stage - tl->Stages) - FirstStage[g];
            return (uint)s < (uint)StageCount[g] ? StepTable[start + s] : null;
        }

        public byte* MarkerOf(ushort timeline, int window)
        {
            var t = timeline - 1;
            if ((uint)t >= (uint)TimelineCount) return null;
            return MarkerValues[WindowBase[t] + window];
        }

        public uint MarkerPair(ushort timeline, int window)
        {
            var tk = Sec.TimelineOf(timeline);
            return tk == null ? uint.MaxValue : Sec.Windows(tk)[window].Pair;
        }

        public void Verify(int kind, in KindDescriptor descriptor)
        {
            var name = Sec.NameOf(kind);
            var row = Sec.Kinds[kind];
            if (row.ValueBytes != (uint)descriptor.ValueBytes)
                throw new InvalidOperationException("Kind '" + name + "' is " + descriptor.ValueBytes + " bytes in code but " + row.ValueBytes + " in the loaded library; rebake the library with tlb.");
            if (row.EntryBytes != (uint)descriptor.EntryBytes || UnsafeUtility.SizeOf(descriptor.Entry) != descriptor.EntryBytes)
                throw new InvalidOperationException("Kind '" + name + "' entry is " + UnsafeUtility.SizeOf(descriptor.Entry) + " bytes in code but " + row.EntryBytes + " in the loaded library; rebake the library with tlb.");
            var have = new System.Text.StringBuilder();
            for (var f = 0; f < Sec.FeederCount; f++)
            {
                if (Sec.Feeders[f].Kind != kind) continue;
                if (have.Length > 0) have.Append('\n');
                have.Append(Sec.Text(Sec.Feeders[f].ConsumerOffset, Sec.Feeders[f].ConsumerLength)).Append('.').Append(Sec.Text(Sec.Feeders[f].MethodOffset, Sec.Feeders[f].MethodLength)).Append(':').Append(Sec.Feeders[f].Trigger);
            }
            if (have.ToString() != descriptor.Signature)
                throw new InvalidOperationException("Kind '" + name + "' feeders in the loaded library differ from the compiled game; rebake the library with tlb.\nlibrary: " + have.ToString().Replace('\n', ' ') + "\ncode:    " + descriptor.Signature.Replace('\n', ' '));
        }

        public static KindIndex Build(in global::Tl.LibraryView view, global::Tl.TimelineWorld world)
        {
            var index = default(KindIndex);
            index.TimelineCount = view.TimelineCount;
            index.Timelines = view.Timelines;
            index.Sec = view.Kinds;
            index.KindCount = view.Kinds.IsEmpty ? 0 : view.Kinds.KindCount;
            var count = view.TimelineCount;
            index.Closure = Zeroed<ulong>(Math.Max(1, count));
            index.PairStart = Zeroed<int>(count + 2);
            index.WindowBase = Zeroed<int>(count + 1);
            var pairs = new List<uint>();
            for (var t = 0; t < count; t++)
            {
                var at = t;
                for (var hop = 0; hop <= count; hop++)
                {
                    if (index.KindCount != 0) index.Closure[t] |= index.Sec.Timelines[at].KindMask;
                    if (view.Timelines[at].Next == 0) break;
                    at = view.Timelines[at].Next - 1;
                }
                index.PairStart[t] = pairs.Count;
                if (index.KindCount == 0 || index.Sec.Timelines[t].KindMask == 0) continue;
                var tk = index.Sec.Timelines + t;
                var events = index.Sec.Events(tk);
                var set = new SortedSet<uint>();
                for (var n = 0u; n < tk->EventCount; n++)
                    if (events[n].Op != (byte)global::Tl.KindEventOp.Remove) set.Add(((uint)events[n].Window << 16) | events[n].Feeder);
                pairs.AddRange(set);
            }
            index.PairStart[count] = pairs.Count;
            index.PairCount = pairs.Count;
            index.Pairs = Zeroed<uint>(Math.Max(1, pairs.Count));
            for (var n = 0; n < pairs.Count; n++) index.Pairs[n] = pairs[n];
            BuildSteps(ref index, view, world);
            return index;
        }

        static void BuildSteps(ref KindIndex index, in global::Tl.LibraryView view, global::Tl.TimelineWorld world)
        {
            var count = view.TimelineCount;
            var total = 0;
            for (var t = 0; t < count; t++)
            {
                index.WindowBase[t] = total;
                if (index.KindCount != 0 && index.Sec.Timelines[t].KindMask != 0) total += (int)index.Sec.Timelines[t].WindowCount;
            }
            index.WindowBase[count] = total;
            index.WindowTotal = total;
            index.FirstStage = Zeroed<int>(Math.Max(1, total));
            index.StageCount = Zeroed<int>(Math.Max(1, total));
            index.StepStart = Zeroed<int>(Math.Max(1, total));
            index.MarkerValues = (byte**)Zeroed<IntPtr>(Math.Max(1, total));
            for (var g = 0; g < total; g++) index.StepStart[g] = -1;
            var table = new List<IntPtr>();
            for (var t = 0; t < count; t++)
            {
                if (index.KindCount == 0 || index.Sec.Timelines[t].KindMask == 0) continue;
                var tk = index.Sec.Timelines + t;
                var tl = view.Timelines + t;
                var windows = index.Sec.Windows(tk);
                for (var w = 0; w < tk->WindowCount; w++)
                {
                    var win = windows[w];
                    var g = index.WindowBase[t] + w;
                    if ((win.Flags & 1u) != 0)
                    {
                        if (world != null && global::Tl.MarkerTableRegistry.TryResolve(world, tl->Id, win.Pair, 1, out var value)) index.MarkerValues[g] = value;
                        continue;
                    }
                    int first = -1, last = -1;
                    for (var s = 0; s < tl->StageCount; s++)
                    {
                        if (tl->Stages[s].Start <= win.Start && win.Start < tl->Stages[s].End) first = s;
                        if (tl->Stages[s].Start <= win.End - 1 && win.End - 1 < tl->Stages[s].End) last = s;
                    }
                    if (first < 0 || last < first) continue;
                    index.StepStart[g] = table.Count;
                    index.FirstStage[g] = first;
                    index.StageCount[g] = last - first + 1;
                    for (var s = first; s <= last; s++)
                    {
                        var found = IntPtr.Zero;
                        for (var n = 0; n < tl->Stages[s].StepCount; n++)
                        {
                            var step = tl->Stages[s].Steps + n;
                            if (step->TrackIndex == win.Track && step->Pair == win.Pair && step->WindowStart == win.Start && step->WindowEnd == win.End) { found = (IntPtr)step; break; }
                        }
                        table.Add(found);
                    }
                }
            }
            index.StepTableLength = table.Count;
            index.StepTable = (global::Tl.LibraryStep**)Zeroed<IntPtr>(Math.Max(1, table.Count));
            for (var i = 0; i < table.Count; i++) index.StepTable[i] = (global::Tl.LibraryStep*)table[i];
        }

        public void Dispose()
        {
            Free(Closure);
            Free(Pairs);
            Free(PairStart);
            Free(WindowBase);
            Free(FirstStage);
            Free(StageCount);
            Free(StepStart);
            Free(StepTable);
            Free(MarkerValues);
            this = default;
        }

        static T* Zeroed<T>(int count) where T : unmanaged
        {
            var bytes = (long)count * UnsafeUtility.SizeOf<T>();
            var p = UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent);
            UnsafeUtility.MemClear(p, bytes);
            return (T*)p;
        }

        static void Free(void* p)
        {
            if (p != null) UnsafeUtility.Free(p, Allocator.Persistent);
        }
    }
}
