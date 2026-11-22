using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Tl.Gen.Tlb;
using Unity.Collections;

namespace KindPure
{
    public static unsafe class PureShapeBench
    {
        const int TimelineCount = 500;
        const int TracksPerTimeline = 16;
        const int ShortestDuration = 30;
        const int DurationSpread = 31;
        const int BlendOverlapTicks = 5;
        public const int LiveTracks = 8;

        public static void RunBatch()
        {
            var args = Environment.GetCommandLineArgs();
            var log = Path.Combine(UnityEngine.Application.dataPath, "..", "PureShapeBench.log");
            var rowsList = "10000,50000";
            var activeList = "40,100";
            var warmup = 40;
            var measure = 16;
            var runs = 5;
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-pureLog") log = args[i + 1];
                if (args[i] == "-pureRows") rowsList = args[i + 1];
                if (args[i] == "-pureActive") activeList = args[i + 1];
                if (args[i] == "-pureWarmup") warmup = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-pureMeasure") measure = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-pureRuns") runs = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            }
            Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobDebuggerEnabled = false;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
            using var writer = new StreamWriter(log, append: true);
            Row(writer, "env unity=" + UnityEngine.Application.unityVersion + " burst=" + Unity.Burst.BurstCompiler.IsEnabled + " workers=" + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + " timelines=" + TimelineCount + " live=" + LiveTracks + " warmup=" + warmup + " measure=" + measure + " runs=" + runs);
            var library = Library(LiveTracks);
            var split = Array.IndexOf(args, "-pureSplit") >= 0;
            var every = 0;
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-pureRequestEvery") every = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            foreach (var rowsText in rowsList.Split(','))
            foreach (var activeText in activeList.Split(','))
            {
                var rows = int.Parse(rowsText, CultureInfo.InvariantCulture);
                var active = int.Parse(activeText, CultureInfo.InvariantCulture);
                if (split) Split(writer, library, rows, active, warmup, measure);
                else Cell(writer, library, rows, active, warmup, measure, runs, every);
            }
        }

        static void Split(StreamWriter writer, byte[] library, int rows, int active, int warmup, int measure)
        {
            using var game = World(library, rows, active);
            for (var i = 0; i <= warmup; i++) game.Step();
            game.Entities.CompleteAllTrackedJobs();
            var parts = new double[3];
            var samples = new double[3][];
            for (var s = 0; s < 3; s++) samples[s] = new double[measure];
            for (var i = 0; i < measure; i++)
            {
                game.StepEachSystemCompleted(parts);
                for (var s = 0; s < 3; s++) samples[s][i] = parts[s] * 1000.0 / Stopwatch.Frequency;
            }
            for (var s = 0; s < 3; s++) Array.Sort(samples[s]);
            Row(writer, string.Format(CultureInfo.InvariantCulture, "split rows={0} active={1} precombined={2} tickMs={3:F3} movementMs={4:F3} vitalsMs={5:F3}",
                rows, active, game.Precombined("KindPure.Locomotion") && game.Precombined("KindPure.Lift"), samples[0][measure / 2], samples[1][measure / 2], samples[2][measure / 2]));
        }

        static void Row(StreamWriter writer, string text)
        {
            var line = "TlPureShape: " + text;
            writer.WriteLine(line);
            writer.Flush();
            UnityEngine.Debug.Log(line);
        }

        static int DurationOf(int timeline) => ShortestDuration + timeline * 7 % DurationSpread;

        static bool Blends(int track) => track < 4 || track == 8 || track == 9;

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        static byte[] Raw<T>(T value) where T : unmanaged
        {
            var bytes = new byte[sizeof(T)];
            fixed (byte* at = bytes) *(T*)at = value;
            return bytes;
        }

        public static byte[] Library(int liveTracks)
        {
            var names = new List<string>();
            for (var i = 0; i < TimelineCount; i++) names.Add("shape" + (i + 1).ToString("D4", CultureInfo.InvariantCulture));
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
                    var start = Next(ref state) % (uint)(duration - covered + 1);
                    var end = start + (uint)covered;
                    var split = Blends(track) && covered >= 2 * BlendOverlapTicks - 1;
                    var firstEnd = split ? start + (uint)((covered + BlendOverlapTicks) / 2) : end;
                    var secondStart = firstEnd - BlendOverlapTicks;
                    var seed = i + track;
                    var typed = new TypedTrack { Name = "t" + track };
                    if (track < 8)
                    {
                        typed.Type = typeof(ArcTrack);
                        typed.Data = Raw(new ArcTrack { Scale = 1f });
                        typed.Clips.Add(new TypedClip { Type = typeof(ArcClip), Start = start, End = firstEnd, Data = Raw(new ArcClip { Velocity = 1 + seed % 5 }) });
                        if (split) typed.Clips.Add(new TypedClip { Type = typeof(ArcClip), Start = secondStart, End = end, Data = Raw(new ArcClip { Velocity = 1 + (seed + 2) % 5 }) });
                    }
                    else if (track < 12)
                    {
                        var priority = (byte)(seed % 3);
                        typed.Type = typeof(WalkTrack);
                        typed.Data = Raw(new WalkTrack { Scale = 1f });
                        var walk = Raw(new WalkClip { Speed = 1 + priority, Priority = priority });
                        typed.Clips.Add(new TypedClip { Type = typeof(WalkClip), Start = start, End = firstEnd, Data = walk });
                        if (split) typed.Clips.Add(new TypedClip { Type = typeof(WalkClip), Start = secondStart, End = end, Data = walk });
                    }
                    else if (track < 14)
                    {
                        typed.Type = typeof(HitTrack);
                        typed.Data = Raw(new HitTrack { Scale = 1f });
                        typed.Clips.Add(new TypedClip { Type = typeof(HitClip), Start = start, End = end, Data = Raw(new HitClip { Amount = 1 + seed % 5 }) });
                    }
                    else
                    {
                        typed.Type = typeof(StatTrack);
                        typed.Data = Raw(new StatTrack { Scale = 1f });
                        typed.Clips.Add(new TypedClip { Type = typeof(StatClip), Start = start, End = end, Data = Raw(new StatClip { Delta = 1 + seed % 3 }) });
                    }
                    timeline.Tracks.Add(typed);
                }
                var tlb = LibraryBake.BakeTyped(timeline, manifest);
                parts.Add(new global::Tl.LibraryPart { Name = names[i], Tlb = tlb, Kinds = LibraryBake.KindPayload(names[i], tlb, kinds) });
            }
            return global::Tl.LibraryAssembler.Assemble(parts, 60, global::Tl.LibrarySettings.Default);
        }

        public static PureWorld World(byte[] library, int rows, int active)
        {
            var game = new PureWorld(rows, library);
            for (var e = 0; e < rows; e++)
                for (var k = 0; k < active; k++)
                    game.Start(e, (ushort)(1 + (e * 7 + k * 13) % TimelineCount), (ushort)((e * 31 + k * 17) % ShortestDuration));
            return game;
        }

        public static ulong Play(byte[] library, int rows, int active, int steps, out bool precombined)
        {
            using var game = World(library, rows, active);
            for (var i = 0; i < steps; i++) game.Step();
            precombined = game.Precombined("KindPure.Locomotion") && game.Precombined("KindPure.Lift");
            return game.Hash();
        }

        static void Cell(StreamWriter writer, byte[] library, int rows, int active, int warmup, int measure, int runs, int every)
        {
            var medians = new List<double>();
            var hash = 0ul;
            long allocated = 0;
            double setupMs = 0;
            var precombined = false;
            var requestHeap = 0;
            var chunkCapacity = 0;
            for (var run = 0; run < runs; run++)
            {
                var setup = Stopwatch.StartNew();
                using var game = World(library, rows, active);
                game.RequestEvery(every);
                game.Step();
                game.Entities.CompleteAllTrackedJobs();
                setupMs = setup.Elapsed.TotalMilliseconds;
                for (var i = 0; i < warmup; i++) game.Step();
                game.Entities.CompleteAllTrackedJobs();
                var samples = new double[measure];
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < measure; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    game.Step();
                    game.Entities.CompleteAllTrackedJobs();
                    samples[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                }
                var spent = GC.GetAllocatedBytesForCurrentThread() - before;
                allocated = Math.Max(allocated, spent);
                Array.Sort(samples);
                medians.Add(samples[measure / 2]);
                requestHeap = game.RequestHeapBytes(4096);
                chunkCapacity = game.Entities.GetChunk(game.Rows[0]).Archetype.ChunkCapacity;
                hash = game.Hash();
                precombined = game.Precombined("KindPure.Locomotion") && game.Precombined("KindPure.Lift");
            }
            medians.Sort();
            var ms = medians[medians.Count / 2];
            Row(writer, string.Format(CultureInfo.InvariantCulture, "cell rows={0} active={1} requestEvery={10} precombined={2} stepMs={3:F3} minMs={4:F3} maxMs={5:F3} nsPerActive={6:F2} setupMs={7:F0} alloc={8} chunkCapacity={11} requestHeapBytesPerRow={12} hash={9:X16}",
                rows, active, precombined, ms, medians[0], medians[medians.Count - 1], ms * 1e6 / ((double)rows * active), setupMs, allocated, hash, every, chunkCapacity, requestHeap));
        }
    }
}
