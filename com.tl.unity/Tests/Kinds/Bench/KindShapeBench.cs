using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Tl.Gen.Tlb;
using Tl.Unity;
using Unity.Collections;
using Unity.Entities;

namespace KindGame
{
    public static unsafe class KindShapeBench
    {
        const int TimelineCount = 500;
        const int TracksPerTimeline = 16;
        const int ShortestDuration = 30;
        const int DurationSpread = 31;
        const int BlendOverlapTicks = 5;

        public static void RunBatch()
        {
            var args = Environment.GetCommandLineArgs();
            var log = Path.Combine(UnityEngine.Application.dataPath, "..", "KindShapeBench.log");
            var rowsList = "10000,50000";
            var activeList = "40,100";
            var modes = "eager,lazy";
            var live = 8;
            var warmup = 16;
            var measure = 32;
            var runs = 5;
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-shapeLog") log = args[i + 1];
                if (args[i] == "-shapeRows") rowsList = args[i + 1];
                if (args[i] == "-shapeActive") activeList = args[i + 1];
                if (args[i] == "-shapeModes") modes = args[i + 1];
                if (args[i] == "-shapeLive") live = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-shapeWarmup") warmup = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-shapeMeasure") measure = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-shapeRuns") runs = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            }
            Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobDebuggerEnabled = false;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
            using var writer = new StreamWriter(log, append: true);
            Row(writer, "env unity=" + UnityEngine.Application.unityVersion + " burst=" + Unity.Burst.BurstCompiler.IsEnabled + " safety=" + Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks + " workers=" + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + " timelines=" + TimelineCount + " tracks=" + TracksPerTimeline + " durations=" + ShortestDuration + "-" + (ShortestDuration + DurationSpread - 1) + " live=" + live + " warmup=" + warmup + " measure=" + measure + " runs=" + runs);
            var library = Library(live);
            foreach (var rowsText in rowsList.Split(','))
            foreach (var activeText in activeList.Split(','))
            foreach (var mode in modes.Split(','))
                Cell(writer, library, int.Parse(rowsText, CultureInfo.InvariantCulture), int.Parse(activeText, CultureInfo.InvariantCulture), mode, warmup, measure, runs);
        }

        static void Row(StreamWriter writer, string text)
        {
            var line = "TlKindShape: " + text;
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

        static byte[] Library(int liveTracks)
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
                        typed.Clips.Add(new TypedClip { Name = "arc", Type = typeof(ArcClip), Start = start, End = firstEnd, Data = Raw(new ArcClip { Velocity = 1 + seed % 5 }) });
                        if (split) typed.Clips.Add(new TypedClip { Name = "arc", Type = typeof(ArcClip), Start = secondStart, End = end, Data = Raw(new ArcClip { Velocity = 1 + (seed + 2) % 5 }) });
                    }
                    else if (track < 12)
                    {
                        var priority = (byte)(seed % 3);
                        typed.Type = typeof(WalkTrack);
                        typed.Data = Raw(new WalkTrack { Scale = 1f });
                        typed.Clips.Add(new TypedClip { Name = "walk", Type = typeof(WalkClip), Start = start, End = firstEnd, Data = Raw(new WalkClip { Speed = 1 + priority, Priority = priority }) });
                        if (split) typed.Clips.Add(new TypedClip { Name = "walk", Type = typeof(WalkClip), Start = secondStart, End = end, Data = Raw(new WalkClip { Speed = 1 + priority, Priority = priority }) });
                    }
                    else if (track < 14)
                    {
                        typed.Type = typeof(HitTrack);
                        typed.Data = Raw(new HitTrack { Scale = 1f });
                        typed.Clips.Add(new TypedClip { Name = "hit", Type = typeof(HitClip), Start = start, End = end, Data = Raw(new HitClip { Amount = 1 + seed % 5 }) });
                    }
                    else
                    {
                        typed.Type = typeof(StatTrack);
                        typed.Data = Raw(new StatTrack { Scale = 1f });
                        typed.Clips.Add(new TypedClip { Name = "stat", Type = typeof(StatClip), Start = start, End = end, Data = Raw(new StatClip { Delta = 1 + seed % 3 }) });
                    }
                    timeline.Tracks.Add(typed);
                }
                var tlb = LibraryBake.BakeTyped(timeline, manifest);
                parts.Add(new global::Tl.LibraryPart { Name = names[i], Tlb = tlb, Kinds = LibraryBake.KindPayload(names[i], tlb, kinds) });
            }
            return global::Tl.LibraryAssembler.Assemble(parts, 60, global::Tl.LibrarySettings.Default);
        }

        static KindWorld World(byte[] library, int rows, int active, string mode)
        {
            var game = new KindWorld(rows, library);
            if (mode == "lazy")
            {
                using var all = game.Entities.CreateEntityQuery(typeof(Playback));
                game.Entities.AddComponent<TimelineHorizon>(all);
                game.Entities.AddComponent<TimelineClock>(all);
                game.Entities.AddComponent<PlaybackDue>(all);
                game.Entities.SetComponentData(game.TickHandle, new TimelineGlide { Limit = (byte)global::Tl.Horizon.Max });
            }
            for (var e = 0; e < rows; e++)
            {
                var requests = game.Entities.GetBuffer<TimelineRequest>(game.Rows[e]);
                for (var k = 0; k < active; k++)
                    requests.Add(TimelineRequest.Start(0, (ushort)(1 + (e * 7 + k * 13) % TimelineCount), (ushort)((e * 31 + k * 17) % ShortestDuration)));
            }
            return game;
        }

        static void Cell(StreamWriter writer, byte[] library, int rows, int active, string mode, int warmup, int measure, int runs)
        {
            var medians = new List<double>();
            var hash = 0ul;
            long allocated = 0;
            var chunkBytes = 0;
            long heapBytes = 0;
            double setupMs = 0;
            for (var run = 0; run < runs; run++)
            {
                var setup = Stopwatch.StartNew();
                using var game = World(library, rows, active, mode);
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
                hash = Hash(game);
                chunkBytes = 16384 / game.Entities.GetChunk(game.Rows[0]).Capacity;
                heapBytes = HeapBytes(game) / rows;
            }
            medians.Sort();
            var ms = medians[medians.Count / 2];
            Row(writer, string.Format(CultureInfo.InvariantCulture, "cell rows={0} active={1} mode={2} stepMs={3:F3} minMs={4:F3} maxMs={5:F3} nsPerActive={6:F2} chunkBytesPerRow={7} heapBytesPerRow={8} setupMs={9:F0} alloc={10} hash={11:X16}",
                rows, active, mode, ms, medians[0], medians[medians.Count - 1], ms * 1e6 / ((double)rows * active), chunkBytes, heapBytes, setupMs, allocated, hash));
        }

        static long HeapBytes(KindWorld game)
        {
            long total = 0;
            foreach (var row in game.Rows)
            {
                total += Spilled(game.Entities.GetBuffer<Playback>(row, true).Capacity, 4, sizeof(Playback));
                total += Spilled(game.Entities.GetBuffer<LiftEntry>(row, true).Capacity, LiftKind.InlineCapacity, sizeof(LiftEntry));
                total += Spilled(game.Entities.GetBuffer<LocomotionEntry>(row, true).Capacity, LocomotionKind.InlineCapacity, sizeof(LocomotionEntry));
                total += Spilled(game.Entities.GetBuffer<DamageEntry>(row, true).Capacity, DamageKind.InlineCapacity, sizeof(DamageEntry));
                total += Spilled(game.Entities.GetBuffer<StatChangeEntry>(row, true).Capacity, StatChangeKind.InlineCapacity, sizeof(StatChangeEntry));
                if (game.Entities.HasComponent<PlaybackDue>(row)) total += Spilled(game.Entities.GetBuffer<PlaybackDue>(row, true).Capacity, 4, sizeof(PlaybackDue));
            }
            return total;
        }

        static long Spilled(int capacity, int inline, int size) => capacity > inline ? (long)capacity * size : 0;

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
                    for (var i = 0; i < sizeof(Playback); i++) hash = (hash ^ data[i]) * 1099511628211ul;
                }
                var motion = game.Motion(row);
                hash = (hash ^ (ulong)BitConverter.SingleToInt32Bits(motion.X)) * 1099511628211ul;
                hash = (hash ^ (ulong)BitConverter.SingleToInt32Bits(motion.Y)) * 1099511628211ul;
                hash = (hash ^ (ulong)game.Health(row)) * 1099511628211ul;
                hash = (hash ^ (ulong)game.Stamina(row)) * 1099511628211ul;
            }
            return hash;
        }
    }
}
