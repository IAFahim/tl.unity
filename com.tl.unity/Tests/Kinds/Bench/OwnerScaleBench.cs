using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Tl.Gen.Tlb;
using Tl.Unity;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace KindGame
{
    public static unsafe class OwnerScaleBench
    {
        const int TimelineCount = 1000;

        public static void RunBatch()
        {
            var args = Environment.GetCommandLineArgs();
            var log = Path.Combine(UnityEngine.Application.dataPath, "..", "OwnerScaleBench.log");
            var shapes = "10000x40,20000x100,20000x200";
            var durations = "120,600,3000";
            var modes = "eager,glide,lazy";
            var warmup = 64;
            var measure = 128;
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-ownerLog") log = args[i + 1];
                if (args[i] == "-ownerShapes") shapes = args[i + 1];
                if (args[i] == "-ownerDurations") durations = args[i + 1];
                if (args[i] == "-ownerModes") modes = args[i + 1];
                if (args[i] == "-ownerWarmup") warmup = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-ownerMeasure") measure = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            }
            Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobDebuggerEnabled = false;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
            using var writer = new StreamWriter(log, append: true);
            Row(writer, "env unity=" + UnityEngine.Application.unityVersion + " burst=" + Unity.Burst.BurstCompiler.IsEnabled + " safety=" + Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks + " jobDebugger=" + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobDebuggerEnabled + " leaks=" + NativeLeakDetection.Mode + " workers=" + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + " timelines=" + TimelineCount + " warmup=" + warmup + " measure=" + measure);
            foreach (var durationText in durations.Split(','))
            {
                var duration = int.Parse(durationText, CultureInfo.InvariantCulture);
                var library = Library(duration);
                foreach (var shape in shapes.Split(','))
                {
                    var parts = shape.Split('x');
                    var rows = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    var active = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    foreach (var mode in modes.Split(','))
                        Cell(writer, library, duration, rows, active, mode, warmup, measure);
                }
            }
        }

        static void Row(StreamWriter writer, string text)
        {
            var line = "TlOwnerBench: " + text;
            writer.WriteLine(line);
            writer.Flush();
            UnityEngine.Debug.Log(line);
        }

        public static byte[] Library(int duration)
        {
            var names = new List<string>();
            for (var i = 0; i < TimelineCount; i++)
                names.Add("owner" + (i + 1).ToString("D4", CultureInfo.InvariantCulture));
            var manifest = LibraryBake.Manifest(names, new List<string>(), 60);
            var kinds = LibraryBake.Kinds(new[] { typeof(WalkTrack).Assembly });
            var parts = new List<global::Tl.LibraryPart>();
            var state = 0x9E3779B9u;
            for (var i = 0; i < TimelineCount; i++)
            {
                var timeline = new TypedTimeline { Name = names[i], Duration = (uint)duration, Loops = true };
                var start = (uint)(Next(ref state) % (uint)duration);
                var length = 1 + Next(ref state) % (uint)Math.Max(1, duration / 2);
                var end = Math.Min((uint)duration, start + length);
                switch (i % 4)
                {
                    case 0:
                    case 1:
                        timeline.Tracks.Add(Track(typeof(WalkTrack), Raw(new WalkTrack { Scale = 1f }), typeof(WalkClip), Raw(new WalkClip { Speed = 1f + i % 7, Priority = (byte)(i % 3) }), start, end));
                        break;
                    case 2:
                        timeline.Tracks.Add(Track(typeof(HitTrack), Raw(new HitTrack { Scale = 1f }), typeof(HitClip), Raw(new HitClip { Amount = 1 + i % 5 }), start, end));
                        break;
                    default:
                        timeline.Tracks.Add(Track(typeof(StatTrack), Raw(new StatTrack { Scale = 1f }), typeof(StatClip), Raw(new StatClip { Delta = 1 + i % 3 }), start, end));
                        break;
                }
                var tlb = LibraryBake.BakeTyped(timeline, manifest);
                parts.Add(new global::Tl.LibraryPart { Name = names[i], Tlb = tlb, Kinds = LibraryBake.KindPayload(names[i], tlb, kinds), Next = 0 });
            }
            return global::Tl.LibraryAssembler.Assemble(parts, 60, global::Tl.LibrarySettings.Default);
        }

        static TypedTrack Track(Type trackType, byte[] trackData, Type clipType, byte[] clipData, uint start, uint end)
        {
            var track = new TypedTrack { Name = trackType.Name, Type = trackType, Data = trackData };
            track.Clips.Add(new TypedClip { Name = clipType.Name, Type = clipType, Start = start, End = end, Data = clipData });
            return track;
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

        static void Cell(StreamWriter writer, byte[] library, int duration, int rows, int active, string mode, int warmup, int measure)
        {
            using var game = new KindWorld(rows, library);
            if (mode != "eager")
            {
                using var all = game.Entities.CreateEntityQuery(typeof(Playback));
                game.Entities.AddComponent<TimelineHorizon>(all);
                if (mode == "lazy")
                {
                    game.Entities.AddComponent<TimelineClock>(all);
                    game.Entities.AddComponent<PlaybackDue>(all);
                }
            }
            game.Entities.SetComponentData(game.TickHandle, new TimelineGlide { Limit = mode == "eager" ? (byte)0 : (byte)global::Tl.Horizon.Max });
            var setup = Stopwatch.StartNew();
            for (var e = 0; e < rows; e++)
            {
                var requests = game.Entities.GetBuffer<TimelineRequest>(game.Rows[e]);
                for (var k = 0; k < active; k++)
                {
                    var timeline = (ushort)(1 + (e * 7 + k * 13) % TimelineCount);
                    requests.Add(TimelineRequest.Start(0, timeline, (ushort)((e * 31 + k * 17) % duration)));
                }
            }
            game.Step();
            game.Entities.CompleteAllTrackedJobs();
            setup.Stop();
            var first = Stopwatch.StartNew();
            game.Step();
            game.Entities.CompleteAllTrackedJobs();
            first.Stop();
            for (var i = 0; i < warmup; i++)
                game.Step();
            game.Entities.CompleteAllTrackedJobs();
            var tick = new double[measure];
            var kinds = new double[measure];
            var before = GC.GetAllocatedBytesForCurrentThread();
            var toMs = 1000.0 / Stopwatch.Frequency;
            for (var i = 0; i < measure; i++)
            {
                var start = Stopwatch.GetTimestamp();
                game.TickHandle.Update(game.World.Unmanaged);
                game.Entities.CompleteAllTrackedJobs();
                var ticked = Stopwatch.GetTimestamp();
                game.MovementHandle.Update(game.World.Unmanaged);
                game.VitalsHandle.Update(game.World.Unmanaged);
                game.Entities.CompleteAllTrackedJobs();
                tick[i] = (ticked - start) * toMs;
                kinds[i] = (Stopwatch.GetTimestamp() - ticked) * toMs;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Row(writer, string.Format(CultureInfo.InvariantCulture,
                "cell rows={0} active={1} duration={2} mode={3} tickMs={4:F3} kindMs={5:F3} totalMs={6:F3} setupMs={7:F1} firstMs={8:F2} alloc={9} hash={10:X16} playing={11} managedRuns={12}",
                rows, active, duration, mode, Median(tick), Median(kinds), Median(tick) + Median(kinds), setup.Elapsed.TotalMilliseconds, first.Elapsed.TotalMilliseconds, allocated, Hash(game), Playing(game), game.Tick.ManagedRuns));
        }

        static double Median(double[] values)
        {
            var copy = (double[])values.Clone();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }

        static int Playing(KindWorld game)
        {
            var count = 0;
            foreach (var row in game.Rows)
                if (game.Entities.IsComponentEnabled<TimelinePlaying>(row))
                    count++;
            return count;
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
                hash = (hash ^ (ulong)game.Health(row)) * 1099511628211ul;
                hash = (hash ^ (ulong)game.Stamina(row)) * 1099511628211ul;
            }
            return hash;
        }
    }
}
