using System;
using System.Collections.Generic;
using System.Text;
using Tl.Unity;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace KindGame
{
    public unsafe struct ScoreFeed
    {
        public global::Tl.LibraryStep* Step;
        public ushort Window;
        public byte Case;
        public byte Flags;
        public uint Reserved;
    }

    public static class ScoreFeedFlags
    {
        public const byte Synthetic = 1;
    }

    public unsafe struct ScoreTable
    {
        public int* Start;
        public ScoreFeed* Feeds;
        public int Count;

        public int First(int key) => Start[key];

        public int End(int key) => Start[key + 1];
    }

    public unsafe struct ScoreLane
    {
        public int* Start;
        public uint* Window;
        public uint* Fold;
        public byte* Frames;
        public int FrameStride;
        public int Count;
    }

    public unsafe struct ScoreIndex
    {
        public int TimelineCount;
        public int KindCount;
        public int Rows;
        public int* RowBase;
        public ScoreTable Spans;
        public ScoreTable Arrivals;
        public ScoreTable Begins;
        public ScoreLane* Lanes;
        public ulong FoldedKinds;
        public ulong FramedKinds;

        public int KeyOf(ushort timeline, int tick, int kind) => (RowBase[timeline - 1] + tick) * KindCount + kind;

        public int RowOf(ushort timeline, int tick) => RowBase[timeline - 1] + tick;

        public long Bytes => (long)(TimelineCount + 1) * sizeof(int) + 3L * ((long)Rows * KindCount + 1) * sizeof(int) + (long)(Spans.Count + Arrivals.Count + Begins.Count) * sizeof(ScoreFeed) + LaneBytes;

        public long LaneBytes
        {
            get
            {
                long bytes = 0;
                for (var kind = 0; kind < KindCount; kind++)
                    bytes += (long)(Rows + 1) * sizeof(int) + (long)Lanes[kind].Count * sizeof(uint) + (Lanes[kind].Fold == null ? 0 : 2L * Lanes[kind].Count * sizeof(uint)) + (long)Lanes[kind].Count * Lanes[kind].FrameStride;
                return bytes;
            }
        }

        public static ScoreIndex Build(in KindIndex index)
        {
            var score = default(ScoreIndex);
            score.TimelineCount = index.TimelineCount;
            score.KindCount = index.KindCount;
            score.RowBase = Allocate<int>(index.TimelineCount + 1);
            var rows = 0;
            for (var t = 0; t < index.TimelineCount; t++)
            {
                score.RowBase[t] = rows;
                rows += (int)index.Timelines[t].Duration;
            }
            score.RowBase[index.TimelineCount] = rows;
            score.Rows = rows;
            var spans = new List<(int Key, ScoreFeed Feed)>();
            var arrivals = new List<(int Key, ScoreFeed Feed)>();
            var begins = new List<(int Key, ScoreFeed Feed)>();
            for (var t = 0; t < index.TimelineCount; t++)
            {
                if (index.KindCount == 0 || index.Sec.Timelines[t].KindMask == 0) continue;
                var id = (ushort)(t + 1);
                var tk = index.Sec.Timelines + t;
                var windows = index.Sec.Windows(tk);
                var duration = (int)index.Timelines[t].Duration;
                for (var k = index.PairStart[t]; k < index.PairStart[t + 1]; k++)
                {
                    var w = (int)(index.Pairs[k] >> 16);
                    var f = (int)(index.Pairs[k] & 0xffff);
                    var feeder = index.Sec.Feeders + f;
                    var route = index.Routes[f];
                    if ((route >> 8) != 0) continue;
                    var window = windows[w];
                    for (var tick = 0; tick < duration; tick++)
                    {
                        var inside = tick >= window.Start && tick < window.End;
                        var key = score.KeyOf(id, tick, feeder->Kind);
                        switch (feeder->Trigger)
                        {
                            case 0:
                                if (!inside) break;
                                var step = index.StepInWindow(id, w, (ushort)tick);
                                if (step != null) spans.Add((key, Feed(step, w, route, 0)));
                                break;
                            case 1:
                                if (inside) begins.Add((key, Feed(index.StepInWindow(id, w, (ushort)tick), w, route, tick != window.Start ? ScoreFeedFlags.Synthetic : (byte)0)));
                                break;
                            case 2:
                                if (tick == window.End - 1) begins.Add((key, Feed(index.StepInWindow(id, w, (ushort)tick), w, route, 0)));
                                break;
                            default:
                                if (tick == window.Start) begins.Add((key, Feed(index.StepInWindow(id, w, (ushort)tick), w, route, 0)));
                                break;
                        }
                    }
                }
                var events = index.Sec.Events(tk);
                for (var tick = 0; tick < duration; tick++)
                {
                    var to = index.Sec.EventEnd(tk, tick, 0);
                    for (var n = index.Sec.EventStart(tk, tick, 0); n < to; n++)
                    {
                        var ev = events[n];
                        if (ev.Op == (byte)global::Tl.KindEventOp.Remove || ev.Op == (byte)global::Tl.KindEventOp.Insert) continue;
                        var route = index.Routes[ev.Feeder];
                        if ((route >> 8) != 0) continue;
                        var feeder = index.Sec.Feeders + ev.Feeder;
                        arrivals.Add((score.KeyOf(id, tick, feeder->Kind), Feed(index.StepInWindow(id, ev.Window, (ushort)tick), ev.Window, route, 0)));
                    }
                }
            }
            var keys = rows * index.KindCount;
            score.Spans = Table(spans, keys);
            score.Arrivals = Table(arrivals, keys);
            score.Begins = Table(begins, keys);
            score.Lanes = Allocate<ScoreLane>(Math.Max(1, index.KindCount));
            var unfolded = 0ul;
            for (var kind = 0; kind < index.KindCount; kind++)
            {
                var lane = score.Lanes + kind;
                lane->Start = Allocate<int>(rows + 1);
                for (var row = 0; row < rows; row++)
                    lane->Start[row + 1] = lane->Start[row] + score.Spans.End(row * index.KindCount + kind) - score.Spans.First(row * index.KindCount + kind);
                lane->Count = lane->Start[rows];
                lane->Window = Allocate<uint>(Math.Max(1, lane->Count));
                for (var t = 0; t < index.TimelineCount; t++)
                {
                    if (index.Sec.Timelines[t].KindMask == 0) continue;
                    var windows = index.Sec.Windows(index.Sec.Timelines + t);
                    for (var tick = 0; tick < (int)index.Timelines[t].Duration; tick++)
                    {
                        var row = score.RowBase[t] + tick;
                        var key = row * index.KindCount + kind;
                        for (var f = score.Spans.First(key); f < score.Spans.End(key); f++)
                        {
                            var window = windows[score.Spans.Feeds[f].Window];
                            lane->Window[lane->Start[row] + f - score.Spans.First(key)] = window.Start | (uint)window.End << 16;
                        }
                    }
                }
                var fold = Allocate<uint>(2 * Math.Max(1, lane->Count));
                var folded = true;
                for (var t = 0; t < index.TimelineCount && folded; t++)
                for (var tick = 0; tick < (int)index.Timelines[t].Duration && folded; tick++)
                {
                    var row = score.RowBase[t] + tick;
                    var key = row * index.KindCount + kind;
                    for (var f = score.Spans.First(key); f < score.Spans.End(key); f++)
                    {
                        var step = score.Spans.Feeds[f].Step;
                        if (step->FoldLane == null)
                        {
                            folded = false;
                            break;
                        }
                        var at = lane->Start[row] + f - score.Spans.First(key);
                        var bits = (byte*)step->FoldLane + ((uint)tick - step->WindowStart) * 2u * 4u;
                        fold[2 * at] = *(uint*)bits;
                        fold[2 * at + 1] = *(uint*)(bits + 4);
                    }
                }
                if (folded && lane->Count != 0) lane->Fold = fold;
                else
                {
                    Free(fold);
                    unfolded |= 1ul << kind;
                }
            }
            score.FoldedKinds = ~unfolded;
            return score;
        }

        static ScoreFeed Feed(global::Tl.LibraryStep* step, int window, ushort route, byte flags) => new ScoreFeed { Step = step, Window = (ushort)window, Case = (byte)(route & 0xff), Flags = flags };

        static ScoreTable Table(List<(int Key, ScoreFeed Feed)> feeds, int keys)
        {
            var table = default(ScoreTable);
            table.Count = feeds.Count;
            table.Start = Allocate<int>(keys + 1);
            table.Feeds = Allocate<ScoreFeed>(Math.Max(1, feeds.Count));
            for (var i = 0; i < feeds.Count; i++) table.Start[feeds[i].Key + 1]++;
            for (var k = 0; k < keys; k++) table.Start[k + 1] += table.Start[k];
            var next = Allocate<int>(Math.Max(1, keys));
            UnsafeUtility.MemCpy(next, table.Start, (long)keys * sizeof(int));
            for (var i = 0; i < feeds.Count; i++) table.Feeds[next[feeds[i].Key]++] = feeds[i].Feed;
            UnsafeUtility.Free(next, Allocator.Persistent);
            return table;
        }

        public string Dump(in KindIndex index, ushort timeline)
        {
            var text = new StringBuilder();
            var duration = (int)index.Timelines[timeline - 1].Duration;
            text.Append("score timeline=").Append(timeline).Append(" duration=").Append(duration).Append('\n');
            for (var tick = 0; tick < duration; tick++)
            {
                text.Append("  tick ").Append(tick).Append(':');
                for (var kind = 0; kind < KindCount; kind++)
                {
                    var key = KeyOf(timeline, tick, kind);
                    Section(text, index.Sec.NameOf(kind), "spans", Spans, key);
                    Section(text, index.Sec.NameOf(kind), "arrive", Arrivals, key);
                    Section(text, index.Sec.NameOf(kind), "begin", Begins, key);
                }
                text.Append('\n');
            }
            return text.ToString();
        }

        static void Section(StringBuilder text, string kind, string label, in ScoreTable table, int key)
        {
            var first = table.First(key);
            var end = table.End(key);
            if (first == end) return;
            text.Append(' ').Append(kind.Substring(kind.LastIndexOf('.') + 1)).Append('.').Append(label).Append('[');
            for (var i = first; i < end; i++)
            {
                var feed = table.Feeds[i];
                if (i != first) text.Append(' ');
                text.Append('w').Append(feed.Window);
                if (feed.Step != null) text.Append('@').Append(feed.Step->WindowStart).Append("..").Append(feed.Step->WindowEnd);
                if (feed.Step != null && feed.Step->FactorSpan != 0) text.Append("~blend");
                if ((feed.Flags & ScoreFeedFlags.Synthetic) != 0) text.Append("~synthetic");
            }
            text.Append(']');
        }

        public void Dispose()
        {
            Free(RowBase);
            Free(Spans.Start);
            Free(Spans.Feeds);
            Free(Arrivals.Start);
            Free(Arrivals.Feeds);
            Free(Begins.Start);
            Free(Begins.Feeds);
            for (var kind = 0; kind < KindCount && Lanes != null; kind++)
            {
                Free(Lanes[kind].Start);
                Free(Lanes[kind].Window);
                Free(Lanes[kind].Fold);
                Free(Lanes[kind].Frames);
            }
            Free(Lanes);
            this = default;
        }

        static T* Allocate<T>(int count) where T : unmanaged
        {
            var bytes = (long)Math.Max(1, count) * sizeof(T);
            var p = UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent);
            UnsafeUtility.MemClear(p, bytes);
            return (T*)p;
        }

        static void Free(void* p)
        {
            if (p != null) UnsafeUtility.Free(p, Allocator.Persistent);
        }
    }

    public struct ScoreLibrary : IComponentData
    {
        public ScoreIndex Index;
        public bool Lanes;
    }
}
