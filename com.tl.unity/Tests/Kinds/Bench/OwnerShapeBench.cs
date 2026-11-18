using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Tl.Gen.Tlb;
using Tl.Unity;
using Unity.Collections;
using Unity.Entities;

namespace KindGame
{
    public static unsafe class OwnerShapeBench
    {
        const int TimelineCount = 500;
        const int TracksPerTimeline = 16;
        const int ShortestDuration = 30;
        const int DurationSpread = 31;
        const int BlendOverlapTicks = 5;

        public static void RunBatch()
        {
            var args = Environment.GetCommandLineArgs();
            var log = Path.Combine(UnityEngine.Application.dataPath, "..", "OwnerShapeBench.log");
            var rowsList = "10000,50000";
            var activeList = "40,100";
            var liveList = "4,8,16";
            var modes = "eager,lazy";
            var warmup = 32;
            var measure = 64;
            var verifyTicks = 0;
            var rough = false;
            string perfControl = null;
            string perfAck = null;
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-shapeLog") log = args[i + 1];
                if (args[i] == "-shapeRows") rowsList = args[i + 1];
                if (args[i] == "-shapeActive") activeList = args[i + 1];
                if (args[i] == "-shapeLive") liveList = args[i + 1];
                if (args[i] == "-shapeModes") modes = args[i + 1];
                if (args[i] == "-shapeWarmup") warmup = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-shapeMeasure") measure = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-shapeValues") rough = args[i + 1] == "rough";
                if (args[i] == "-shapeVerify") verifyTicks = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-perfControl") perfControl = args[i + 1];
                if (args[i] == "-perfAck") perfAck = args[i + 1];
            }
            Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobDebuggerEnabled = false;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
            using var writer = new StreamWriter(log, append: true);
            Row(writer, "env unity=" + UnityEngine.Application.unityVersion + " burst=" + Unity.Burst.BurstCompiler.IsEnabled + " safety=" + Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks + " workers=" + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + " timelines=" + TimelineCount + " tracks=" + TracksPerTimeline + " durations=" + ShortestDuration + "-" + (ShortestDuration + DurationSpread - 1) + " warmup=" + warmup + " measure=" + measure);
            var perf = new PerfSwitch(perfControl, perfAck);
            foreach (var liveText in liveList.Split(','))
            {
                var liveTracks = int.Parse(liveText, CultureInfo.InvariantCulture);
                var library = Library(liveTracks, rough);
                if (Array.IndexOf(args, "-shapeDump") >= 0)
                {
                    using var dumped = new KindWorld(1, library, true);
                    Row(writer, Dump(dumped, 1));
                    using var query = dumped.Entities.CreateEntityQuery(typeof(TimelineLibrary));
                    Row(writer, dumped.Score.Dump(query.GetSingleton<TimelineLibrary>().Kinds, 1));
                }
                if (verifyTicks > 0)
                    foreach (var rowsText in rowsList.Split(','))
                    foreach (var activeText in activeList.Split(','))
                    foreach (var mode in modes.Split(','))
                        if (mode.IndexOf('-') < 0)
                            Row(writer, "verify rows=" + rowsText + " active=" + activeText + " live=" + liveTracks + " mode=" + mode + " values=" + (rough ? "rough" : "orderly") + " ticks=" + verifyTicks + ": " + Verify(library, int.Parse(rowsText, CultureInfo.InvariantCulture), int.Parse(activeText, CultureInfo.InvariantCulture), mode, verifyTicks));
                foreach (var rowsText in rowsList.Split(','))
                foreach (var activeText in activeList.Split(','))
                foreach (var mode in modes.Split(','))
                    Cell(writer, library, liveTracks, int.Parse(rowsText, CultureInfo.InvariantCulture), int.Parse(activeText, CultureInfo.InvariantCulture), mode, warmup, measure, perf);
            }
        }

        static void Row(StreamWriter writer, string text)
        {
            var line = "TlShapeBench: " + text;
            writer.WriteLine(line);
            writer.Flush();
            UnityEngine.Debug.Log(line);
        }

        public static int DurationOf(int timelineIndex) => ShortestDuration + timelineIndex * 7 % DurationSpread;

        public static bool Blends(int track) => track < 4 || track == 8 || track == 9;

        public static byte[] Library(int liveTracks, bool rough)
        {
            var names = new List<string>();
            for (var i = 0; i < TimelineCount; i++)
                names.Add("shape" + (i + 1).ToString("D4", CultureInfo.InvariantCulture));
            var manifest = LibraryBake.Manifest(names, new List<string>(), 60);
            var kinds = LibraryBake.Kinds(new[] { typeof(WalkTrack).Assembly });
            var parts = new List<global::Tl.LibraryPart>();
            var state = 0x9E3779B9u;
            for (var i = 0; i < TimelineCount; i++)
            {
                var duration = DurationOf(i);
                var timeline = new TypedTimeline { Name = names[i], Duration = (uint)duration, Loops = true };
                for (var track = 0; track < TracksPerTimeline; track++)
                {
                    var covered = Math.Max(1, duration * liveTracks / TracksPerTimeline);
                    var start = (uint)(Next(ref state) % (uint)(duration - covered + 1));
                    var end = start + (uint)covered;
                    var split = Blends(track) && covered >= 2 * BlendOverlapTicks - 1;
                    var firstEnd = split ? start + (uint)((covered + BlendOverlapTicks) / 2) : end;
                    var secondStart = firstEnd - BlendOverlapTicks;
                    var seed = i + track;
                    TypedTrack typed;
                    if (track < 8)
                    {
                        typed = Track("Jump" + track, typeof(ArcTrack), Raw(new ArcTrack { Scale = 1f }));
                        Clip(typed, typeof(ArcClip), Raw(new ArcClip { Velocity = 1 + seed % 5 + (rough ? 0.1f * (seed % 7) : 0f) }), start, firstEnd);
                        if (split) Clip(typed, typeof(ArcClip), Raw(new ArcClip { Velocity = 1 + (seed + 2) % 5 + (rough ? 0.3f * (seed % 3) : 0f) }), secondStart, end);
                    }
                    else if (track < 12)
                    {
                        var priority = (byte)(seed % 3);
                        typed = Track("Walk" + track, typeof(WalkTrack), Raw(new WalkTrack { Scale = 1f }));
                        Clip(typed, typeof(WalkClip), Raw(new WalkClip { Speed = 1 + priority + (rough ? 0.01f * i : 0f), Priority = priority }), start, firstEnd);
                        if (split) Clip(typed, typeof(WalkClip), Raw(new WalkClip { Speed = 1 + priority + (rough ? 0.01f * i + 0.5f : 0f), Priority = priority }), secondStart, end);
                    }
                    else if (track < 14)
                    {
                        typed = Track("Hit" + track, typeof(HitTrack), Raw(new HitTrack { Scale = 1f }));
                        Clip(typed, typeof(HitClip), Raw(new HitClip { Amount = 1 + seed % 5 }), start, end);
                    }
                    else
                    {
                        typed = Track("Stat" + track, typeof(StatTrack), Raw(new StatTrack { Scale = 1f }));
                        Clip(typed, typeof(StatClip), Raw(new StatClip { Delta = 1 + seed % 3 }), start, end);
                    }
                    timeline.Tracks.Add(typed);
                }
                var tlb = LibraryBake.BakeTyped(timeline, manifest);
                parts.Add(new global::Tl.LibraryPart { Name = names[i], Tlb = tlb, Kinds = LibraryBake.KindPayload(names[i], tlb, kinds), Next = 0 });
            }
            return global::Tl.LibraryAssembler.Assemble(parts, 60, global::Tl.LibrarySettings.Default);
        }

        static TypedTrack Track(string name, Type trackType, byte[] trackData) => new TypedTrack { Name = name, Type = trackType, Data = trackData };

        static void Clip(TypedTrack track, Type clipType, byte[] clipData, uint start, uint end)
        {
            track.Clips.Add(new TypedClip { Name = clipType.Name, Type = clipType, Start = start, End = end, Data = clipData });
        }

        static byte[] Raw<T>(T value) where T : unmanaged
        {
            var bytes = new byte[sizeof(T)];
            fixed (byte* at = bytes)
                *(T*)at = value;
            return bytes;
        }

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        static void Cell(StreamWriter writer, byte[] library, int liveTracks, int rows, int active, string mode, int warmup, int measure, PerfSwitch perf)
        {
            var setup = Stopwatch.StartNew();
            using var game = World(library, rows, active, mode);
            game.Step();
            game.Entities.CompleteAllTrackedJobs();
            setup.Stop();
            for (var i = 0; i < warmup; i++)
                game.Step();
            game.Entities.CompleteAllTrackedJobs();
            var tick = new double[measure];
            var kinds = new double[measure];
            var toMs = 1000.0 / Stopwatch.Frequency;
            var before = GC.GetAllocatedBytesForCurrentThread();
            perf.Enable();
            for (var i = 0; i < measure; i++)
            {
                var start = Stopwatch.GetTimestamp();
                game.TickHandle.Update(game.World.Unmanaged);
                game.Entities.CompleteAllTrackedJobs();
                var ticked = Stopwatch.GetTimestamp();
                game.UpdateKinds();
                game.Entities.CompleteAllTrackedJobs();
                tick[i] = (ticked - start) * toMs;
                kinds[i] = (Stopwatch.GetTimestamp() - ticked) * toMs;
            }
            perf.Disable();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var entries = LiveEntries(game);
            var clips = LiveClips(game);
            var kindMs = Median(kinds);
            var tickMs = Median(tick);
            Row(writer, string.Format(CultureInfo.InvariantCulture,
                "cell rows={0} active={1} live={2} mode={3} tickMs={4:F3} kindMs={5:F3} totalMs={6:F3} entries={7} clips={8} wallNsPerClip={9:F2} heapBytesPerRow={10} chunkBytesPerRow={11} tableBytes={12} laneBytes={17} folded={16:X} setupMs={13:F1} alloc={14} hash={15:X16}",
                rows, active, liveTracks, mode, tickMs, kindMs, tickMs + kindMs, entries, clips, clips == 0 ? 0 : (tickMs + kindMs) * 1e6 / clips, HeapBytes(game) / rows, ChunkBytes(game), game.Scored ? game.Score.Bytes : 0, setup.Elapsed.TotalMilliseconds, allocated, Hash(game), game.Scored ? game.Score.FoldedKinds : 0ul, game.Scored ? game.Score.LaneBytes : 0));
        }

        static KindWorld World(byte[] library, int rows, int active, string mode)
        {
            var lanes = mode.StartsWith("lanes-", StringComparison.Ordinal);
            var scored = lanes || mode.StartsWith("score-", StringComparison.Ordinal);
            var stepping = scored ? mode.Substring(6) : mode;
            var game = new KindWorld(rows, library, scored);
            if (lanes)
            {
                using var query = game.Entities.CreateEntityQuery(typeof(ScoreLibrary));
                var score = query.GetSingleton<ScoreLibrary>();
                score.Lanes = true;
                query.SetSingleton(score);
            }
            if (stepping != "eager")
            {
                using var all = game.Entities.CreateEntityQuery(typeof(Playback));
                game.Entities.AddComponent<TimelineHorizon>(all);
                if (stepping == "lazy")
                {
                    game.Entities.AddComponent<TimelineClock>(all);
                    game.Entities.AddComponent<PlaybackDue>(all);
                }
            }
            game.Entities.SetComponentData(game.TickHandle, new TimelineGlide { Limit = stepping == "eager" ? (byte)0 : (byte)global::Tl.Horizon.Max });
            for (var e = 0; e < rows; e++)
            {
                var requests = game.Entities.GetBuffer<TimelineRequest>(game.Rows[e]);
                for (var k = 0; k < active; k++)
                {
                    var timeline = (ushort)(1 + (e * 7 + k * 13) % TimelineCount);
                    requests.Add(TimelineRequest.Start(0, timeline, (ushort)((e * 31 + k * 17) % ShortestDuration)));
                }
            }
            return game;
        }

        public static string Verify(byte[] library, int rows, int active, string mode, int ticks)
        {
            var scalar = Verify(library, rows, active, mode, "score-" + mode, ticks);
            return "score " + scalar + ", lanes " + Verify(library, rows, active, mode, "lanes-" + mode, ticks);
        }

        static string Verify(byte[] library, int rows, int active, string mode, string candidate, int ticks)
        {
            using var reference = World(library, rows, active, mode);
            using var scored = World(library, rows, active, candidate);
            var census = new StructureCensus();
            var valueDiffers = new bool[rows];
            var valuesDiffer = 0;
            string firstValue = null;
            for (var tick = 0; tick < ticks; tick++)
            {
                reference.StepPlaybacks();
                scored.StepPlaybacks();
                reference.Entities.CompleteAllTrackedJobs();
                scored.Entities.CompleteAllTrackedJobs();
                census.Compare(reference, scored, tick);
                reference.UpdateKinds();
                scored.UpdateKinds();
                reference.Entities.CompleteAllTrackedJobs();
                scored.Entities.CompleteAllTrackedJobs();
                for (var row = 0; row < rows; row++)
                {
                    var a = reference.Motion(row);
                    var b = scored.Motion(row);
                    var differs = BitConverter.SingleToInt32Bits(a.X) != BitConverter.SingleToInt32Bits(b.X) || BitConverter.SingleToInt32Bits(a.Y) != BitConverter.SingleToInt32Bits(b.Y) || reference.Health(row) != scored.Health(row) || reference.Stamina(row) != scored.Stamina(row);
                    if (differs)
                    {
                        if (!valueDiffers[row]) valuesDiffer++;
                        valueDiffers[row] = true;
                        if (firstValue == null) firstValue = string.Format(CultureInfo.InvariantCulture, "step {0} row {1}: motion {2},{3} vs {4},{5}, health {6} vs {7}, stamina {8} vs {9}", tick, row, a.X, a.Y, b.X, b.Y, reference.Health(row), scored.Health(row), reference.Stamina(row), scored.Stamina(row));
                    }
                    var left = reference.Slots(row);
                    var right = scored.Slots(row);
                    if (left.Length != right.Length)
                        return string.Format(CultureInfo.InvariantCulture, "slot count differs at step {0} row {1}: {2} vs {3}", tick, row, left.Length, right.Length);
                    var clockA = Clock(reference, row);
                    var clockB = Clock(scored, row);
                    for (var s = 0; s < left.Length; s++)
                    {
                        var x = left[s];
                        var y = right[s];
                        x.Tick = TimelineTicks.Real(in x, clockA);
                        y.Tick = TimelineTicks.Real(in y, clockB);
                        if (*(ulong*)&x != *(ulong*)&y)
                            return string.Format(CultureInfo.InvariantCulture, "slot {0} differs at step {1} row {2}: {3:X16} vs {4:X16}", s, tick, row, *(ulong*)&x, *(ulong*)&y);
                    }
                }
            }
            if (valuesDiffer != 0)
                return string.Format(CultureInfo.InvariantCulture, "values differ on {0} of {1} rows (first {2}) ({3})", valuesDiffer, rows, firstValue, census);
            return "equal (" + census + ")";
        }

        sealed class StructureCensus
        {
            long _rows;
            long _differ;
            long _differIgnoringSynthetic;
            string _first;

            public override string ToString() => string.Format(CultureInfo.InvariantCulture, "structure: {0} of {1} row-ticks differ, {2} ignoring Synthetic{3}", _differ, _rows, _differIgnoringSynthetic, _first == null ? "" : "; first " + _first);

            public void Compare(KindWorld reference, KindWorld scored, int tick)
            {
                using var query = reference.Entities.CreateEntityQuery(typeof(TimelineLibrary));
                var index = query.GetSingleton<TimelineLibrary>().Kinds;
                var score = scored.Score;
                var kinds = new[] { index.KindOf(LiftKind.Id), index.KindOf(LocomotionKind.Id), index.KindOf(StatChangeKind.Id), index.KindOf(DamageKind.Id) };
                var left = new List<ulong>();
                var right = new List<ulong>();
                for (var row = 0; row < reference.Rows.Length; row++)
                {
                    left.Clear();
                    right.Clear();
                    var slots = reference.Slots(row);
                    var clock = Clock(reference, row);
                    Entries(reference.Entities.GetBuffer<LiftEntry>(reference.Rows[row], true).GetUnsafeReadOnlyPtr(), reference.Entities.GetBuffer<LiftEntry>(reference.Rows[row], true).Length, sizeof(LiftEntry), kinds[0], in index, slots, clock, left);
                    Entries(reference.Entities.GetBuffer<LocomotionEntry>(reference.Rows[row], true).GetUnsafeReadOnlyPtr(), reference.Entities.GetBuffer<LocomotionEntry>(reference.Rows[row], true).Length, sizeof(LocomotionEntry), kinds[1], in index, slots, clock, left);
                    Entries(reference.Entities.GetBuffer<StatChangeEntry>(reference.Rows[row], true).GetUnsafeReadOnlyPtr(), reference.Entities.GetBuffer<StatChangeEntry>(reference.Rows[row], true).Length, sizeof(StatChangeEntry), kinds[2], in index, slots, clock, left);
                    Entries(reference.Entities.GetBuffer<DamageEntry>(reference.Rows[row], true).GetUnsafeReadOnlyPtr(), reference.Entities.GetBuffer<DamageEntry>(reference.Rows[row], true).Length, sizeof(DamageEntry), kinds[3], in index, slots, clock, left);
                    var scoredSlots = scored.Slots(row);
                    var scoredClock = Clock(scored, row);
                    for (var s = 0; s < scoredSlots.Length; s++)
                    {
                        var copy = scoredSlots[s];
                        var slot = (global::Tl.Playback*)&copy;
                        if (!ScoreSlots.Live(slot)) continue;
                        var at = TimelineTicks.Real(slot, scoredClock);
                        foreach (var kind in kinds)
                        {
                            var key = score.KeyOf(slot->Timeline, at, kind);
                            if (!ScoreSlots.Paused(slot))
                                for (var f = score.Spans.First(key); f < score.Spans.End(key); f++)
                                    right.Add(Item(kind, s, score.Spans.Feeds[f].Window, false, (score.Spans.Feeds[f].Flags & ScoreFeedFlags.Synthetic) != 0, false));
                            var pulses = ScoreSlots.Began(slot) ? score.Begins : score.Arrivals;
                            for (var f = pulses.First(key); f < pulses.End(key); f++)
                                right.Add(Item(kind, s, pulses.Feeds[f].Window, true, (pulses.Feeds[f].Flags & ScoreFeedFlags.Synthetic) != 0, false));
                        }
                    }
                    _rows++;
                    if (Same(left, right, ulong.MaxValue)) continue;
                    _differ++;
                    if (!Same(left, right, ~SyntheticBit)) _differIgnoringSynthetic++;
                    if (_first == null) _first = string.Format(CultureInfo.InvariantCulture, "step {0} row {1}: entries [{2}] table [{3}]", tick, row, Describe(left), Describe(right));
                }
            }

            const ulong PulseBit = 1ul << 40;
            const ulong SyntheticBit = 1ul << 41;
            const ulong InterruptedBit = 1ul << 42;

            static ulong Item(int kind, int slot, int window, bool pulse, bool synthetic, bool interrupted)
                => (ulong)(uint)kind << 32 | (ulong)(uint)slot << 16 | (uint)window | (pulse ? PulseBit : 0) | (synthetic ? SyntheticBit : 0) | (interrupted ? InterruptedBit : 0);

            static void Entries(void* data, int length, int stride, int kind, in KindIndex index, Playback[] slots, ushort clock, List<ulong> into)
            {
                for (var i = 0; i < length; i++)
                {
                    var entry = (global::Tl.KindEntry*)((byte*)data + (long)i * stride);
                    var pulse = (entry->Flags & global::Tl.KindEntryFlags.Pulse) != 0;
                    var synthetic = (entry->Flags & global::Tl.KindEntryFlags.Synthetic) != 0;
                    var interrupted = (entry->Flags & global::Tl.KindEntryFlags.Interrupted) != 0;
                    if (entry->Timeline == 0)
                    {
                        into.Add(Item(kind, 255, 0xFFFF, true, synthetic, interrupted));
                        continue;
                    }
                    if (!pulse)
                    {
                        var copy = slots[entry->Slot];
                        if (ScoreSlots.Paused((global::Tl.Playback*)&copy)) continue;
                        if (index.StepInWindow(entry->Timeline, entry->Step, TimelineTicks.Real(in copy, clock)) == null) continue;
                    }
                    into.Add(Item(kind, entry->Slot, entry->Step, pulse, synthetic, interrupted));
                }
            }

            static bool Same(List<ulong> left, List<ulong> right, ulong mask)
            {
                if (left.Count != right.Count) return false;
                var a = new ulong[left.Count];
                var b = new ulong[right.Count];
                for (var i = 0; i < a.Length; i++)
                {
                    a[i] = left[i] & mask;
                    b[i] = right[i] & mask;
                }
                Array.Sort(a);
                Array.Sort(b);
                for (var i = 0; i < a.Length; i++)
                    if (a[i] != b[i]) return false;
                return true;
            }

            static string Describe(List<ulong> items)
            {
                var copy = items.ToArray();
                Array.Sort(copy);
                var text = new StringBuilder();
                foreach (var item in copy)
                {
                    if (text.Length > 0) text.Append(' ');
                    text.Append('k').Append(item >> 32 & 0xFF).Append('s').Append(item >> 16 & 0xFFFF).Append('w').Append(item & 0xFFFF);
                    if ((item & PulseBit) != 0) text.Append('P');
                    if ((item & SyntheticBit) != 0) text.Append('~');
                    if ((item & InterruptedBit) != 0) text.Append('!');
                    if (text.Length > 600) break;
                }
                return text.ToString();
            }
        }

        static ushort Clock(KindWorld game, int row) => game.Entities.HasComponent<TimelineClock>(game.Rows[row]) ? game.Entities.GetComponentData<TimelineClock>(game.Rows[row]).Value : (ushort)0;

        static double Median(double[] values)
        {
            var copy = (double[])values.Clone();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }

        static long LiveEntries(KindWorld game)
        {
            game.Entities.CompleteAllTrackedJobs();
            if (game.Scored) return 0;
            long total = 0;
            foreach (var row in game.Rows)
                total += game.Entities.GetBuffer<LiftEntry>(row, true).Length + game.Entities.GetBuffer<LocomotionEntry>(row, true).Length;
            return total;
        }

        static long LiveClips(KindWorld game)
        {
            game.Entities.CompleteAllTrackedJobs();
            using var query = game.Entities.CreateEntityQuery(typeof(TimelineLibrary));
            var index = query.GetSingleton<TimelineLibrary>().Kinds;
            var score = game.Scored ? game.Score : ScoreIndex.Build(in index);
            var lift = index.KindOf(LiftKind.Id);
            var locomotion = index.KindOf(LocomotionKind.Id);
            long total = 0;
            for (var row = 0; row < game.Rows.Length; row++)
            {
                var clock = Clock(game, row);
                foreach (var slot in game.Slots(row))
                {
                    var copy = slot;
                    if (!ScoreSlots.Live((global::Tl.Playback*)&copy)) continue;
                    var tick = TimelineTicks.Real(in copy, clock);
                    var a = score.KeyOf(copy.Timeline, tick, lift);
                    var b = score.KeyOf(copy.Timeline, tick, locomotion);
                    total += score.Spans.End(a) - score.Spans.First(a) + score.Spans.End(b) - score.Spans.First(b);
                }
            }
            if (!game.Scored) score.Dispose();
            return total;
        }

        static long HeapBytes(KindWorld game)
        {
            long total = 0;
            foreach (var row in game.Rows)
            {
                total += Spilled(game.Entities.GetBuffer<Playback>(row, true).Capacity, 4, sizeof(Playback));
                if (game.Scored) continue;
                total += Spilled(game.Entities.GetBuffer<LiftEntry>(row, true).Capacity, LiftKind.InlineCapacity, sizeof(LiftEntry));
                total += Spilled(game.Entities.GetBuffer<LocomotionEntry>(row, true).Capacity, LocomotionKind.InlineCapacity, sizeof(LocomotionEntry));
                total += Spilled(game.Entities.GetBuffer<DamageEntry>(row, true).Capacity, DamageKind.InlineCapacity, sizeof(DamageEntry));
                total += Spilled(game.Entities.GetBuffer<StatChangeEntry>(row, true).Capacity, StatChangeKind.InlineCapacity, sizeof(StatChangeEntry));
            }
            foreach (var row in game.Rows)
                if (game.Entities.HasComponent<PlaybackDue>(row))
                    total += Spilled(game.Entities.GetBuffer<PlaybackDue>(row, true).Capacity, 4, sizeof(PlaybackDue));
            return total;
        }

        static long Spilled(int capacity, int inline, int size) => capacity > inline ? (long)capacity * size : 0;

        static int ChunkBytes(KindWorld game) => 16384 / game.Entities.GetChunk(game.Rows[0]).Capacity;

        static string Dump(KindWorld game, ushort timeline)
        {
            using var query = game.Entities.CreateEntityQuery(typeof(TimelineLibrary));
            var index = query.GetSingleton<TimelineLibrary>().Kinds;
            var tk = index.Sec.Timelines + (timeline - 1);
            var windows = index.Sec.Windows(tk);
            var text = new StringBuilder();
            text.Append("dump timeline=").Append(timeline).Append(" duration=").Append(index.Timelines[timeline - 1].Duration).Append(" windows=").Append(tk->WindowCount).Append('\n');
            for (var w = 0; w < tk->WindowCount; w++)
            {
                text.Append("  window ").Append(w).Append(" track=").Append(windows[w].Track).Append(" pair=").Append(windows[w].Pair).Append(" ticks=").Append(windows[w].Start).Append("..").Append(windows[w].End).Append(" flags=").Append(windows[w].Flags);
                for (var k = index.PairStart[timeline - 1]; k < index.PairStart[timeline]; k++)
                    if ((int)(index.Pairs[k] >> 16) == w)
                    {
                        var feeder = index.Sec.Feeders + (index.Pairs[k] & 0xffff);
                        text.Append(" feeder=").Append(index.Pairs[k] & 0xffff).Append(" kind=").Append(index.Sec.NameOf(feeder->Kind)).Append(" trigger=").Append(feeder->Trigger);
                    }
                var step = index.WindowStep[index.WindowBase[timeline - 1] + w];
                if (step != null) text.Append(" step=").Append(step->WindowStart).Append("..").Append(step->WindowEnd).Append(" factor=").Append(step->FactorStart).Append('+').Append(step->FactorSpan);
                text.Append('\n');
            }
            return text.ToString();
        }

        static ulong Hash(KindWorld game)
        {
            game.Entities.CompleteAllTrackedJobs();
            var hash = 14695981039346656037ul;
            for (var row = 0; row < game.Rows.Length; row++)
            {
                var buffer = game.Entities.GetBuffer<Playback>(game.Rows[row], true);
                var clock = game.Entities.HasComponent<TimelineClock>(game.Rows[row]) ? game.Entities.GetComponentData<TimelineClock>(game.Rows[row]).Value : (ushort)0;
                for (var slot = 0; slot < buffer.Length; slot++)
                {
                    var copy = buffer[slot];
                    copy.Tick = TimelineTicks.Real(in copy, clock);
                    var data = (byte*)&copy;
                    for (var i = 0; i < sizeof(Playback); i++)
                        hash = (hash ^ data[i]) * 1099511628211ul;
                }
                var motion = game.Motion(row);
                hash = (hash ^ (ulong)BitConverter.SingleToInt32Bits(motion.X)) * 1099511628211ul;
                hash = (hash ^ (ulong)BitConverter.SingleToInt32Bits(motion.Y)) * 1099511628211ul;
                hash = (hash ^ (ulong)game.Health(row)) * 1099511628211ul;
                hash = (hash ^ (ulong)game.Stamina(row)) * 1099511628211ul;
            }
            return hash;
        }

        sealed class PerfSwitch
        {
            readonly string _control;
            readonly string _ack;

            public PerfSwitch(string control, string ack)
            {
                _control = control;
                _ack = ack;
            }

            public void Enable() => Send("enable");

            public void Disable() => Send("disable");

            void Send(string command)
            {
                if (_control == null) return;
                using (var control = new FileStream(_control, FileMode.Open, FileAccess.Write))
                {
                    var bytes = Encoding.ASCII.GetBytes(command + "\n");
                    control.Write(bytes, 0, bytes.Length);
                }
                if (_ack == null) return;
                using var ack = new FileStream(_ack, FileMode.Open, FileAccess.Read);
                var buffer = new byte[16];
                ack.Read(buffer, 0, buffer.Length);
            }
        }
    }
}
