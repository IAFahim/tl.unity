using System;
using System.Diagnostics;
using System.IO;
using Unity.Burst;
using UnityEditor;
using UnityEngine;

namespace KindGame
{
    // K3 Unity performance receipts (plan 4.12 rows vs the .NET K2 gates).
    // Shapes mirror benchmarks/KindSystems K-A..K-F at N=20000, all entities
    // carrying all four kinds (worst case; K4 exact sets only shrink this):
    //   A  every entity plays Run (one walk entry each)
    //   B  10% of entities play Run (bit skipping)
    //   C  Run started staggered over the 40-tick loop (no sorting on host)
    //   D  Run from tick 0, measuring ticks 1..24: no boundary events
    //   F  8 game damage pulses per entity per step vs 1
    // Run headless: -executeMethod KindGame.KindBench.RunBatch [-kindsBench <path>]
    // Rows are logged with the TlKindBench: prefix and appended to the log file.
    public static class KindBench
    {
        const int N = 100000;
        const int Warmup = 10;
        const int Measure = 200;

        [MenuItem("Tools/Tl/Benchmark Kinds")]
        public static void RunFromMenu()
        {
            Run(Path.Combine(Application.dataPath, "..", "KindsBench.log"));
        }

        public static void RunBatch()
        {
            var path = Path.Combine(Application.dataPath, "..", "KindsBench.log");
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-kindsBench")
                    path = args[i + 1];
            Run(path);
        }

        public static void Run(string logPath)
        {
            using (var log = new StreamWriter(logPath, append: true))
            {
                Row(log, "env unity=" + Application.unityVersion + " burst=" + BurstCompiler.IsEnabled + " workers=" + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + " entities=" + N);
                RowA(log);
                RowB(log);
                RowIdle(log);
                RowEmpty(log);
                RowC(log);
                RowD(log);
                RowF(log);
            }
        }

        static void Row(StreamWriter log, string text)
        {
            var line = "TlKindBench: " + text;
            log.WriteLine(line);
            UnityEngine.Debug.Log(line);
        }

        static double StepAll(KindWorld game, int steps)
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < steps; i++)
                game.Step();
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds;
        }

        static double NsPerEntity(double ms, int entities, int steps)
        {
            return ms * 1000000.0 / (entities * steps);
        }

        static void RowA(StreamWriter log)
        {
            using (var game = new KindWorld(N))
            {
                for (var e = 0; e < N; e++)
                    game.Start(e, KindWorld.Run);
                StepAll(game, Warmup);
                var before = GC.GetAllocatedBytesForCurrentThread();
                var ms = StepAll(game, Measure);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Row(log, "A all-playing step=" + NsPerEntity(ms, N, Measure).ToString("F1")
                    + "ns/entity Tick+kinds allocationB=" + allocated);
            }
        }

        static void RowB(StreamWriter log)
        {
            using (var game = new KindWorld(N))
            {
                for (var e = 0; e < N; e += 10)
                    game.Start(e, KindWorld.Run);
                StepAll(game, Warmup);
                var ms = StepAll(game, Measure);
                Row(log, "B tenth-playing step=" + NsPerEntity(ms, N, Measure).ToString("F1") + "ns/entity");
            }
        }

        static void RowIdle(StreamWriter log)
        {
            using (var game = new KindWorld(N))
            {
                StepAll(game, Warmup);
                var ms = StepAll(game, Measure);
                Row(log, "B0 none-playing step=" + NsPerEntity(ms, N, Measure).ToString("F1") + "ns/entity");
            }
        }

        static void RowEmpty(StreamWriter log)
        {
            using (var game = new KindWorld(0))
            {
                StepAll(game, Warmup);
                var ms = StepAll(game, Measure);
                Row(log, "E empty-world step=" + (ms * 1000.0 / Measure).ToString("F1") + "us/step");
            }
        }

        static void RowC(StreamWriter log)
        {
            using (var game = new KindWorld(N))
            {
                for (var e = 0; e < N; e++)
                    game.Start(e, KindWorld.Run, (ushort)(e % 40));
                StepAll(game, Warmup);
                var ms = StepAll(game, Measure);
                Row(log, "C staggered step=" + NsPerEntity(ms, N, Measure).ToString("F1") + "ns/entity");
            }
        }
        static void RowD(StreamWriter log)
        {
            using (var game = new KindWorld(N))
            {
                for (var e = 0; e < N; e++)
                    game.Start(e, KindWorld.Run);
                game.Step();
                StepAll(game, Warmup);
                var ms = StepAll(game, Measure);
                Row(log, "D no-boundary step=" + NsPerEntity(ms, N, Measure).ToString("F1") + "ns/playback");
            }
        }


        static void RowF(StreamWriter log)
        {
            // Batched into one stream per step (the fair analog of the .NET
            // queued pulses): one stream writer carries all of a step's sends.
            const int NF = 2000;
            const int Steps = 24;
            double one = PulseSteps(NF, Steps, 1);
            double eight = PulseSteps(NF, Steps, 8);
            var perExtra = (eight - one) * 1000000.0 / (7 * NF * Steps);
            Row(log, "F pulse8v1 extra=" + perExtra.ToString("F1") + "ns/pulse");
        }

        static double PulseSteps(int count, int steps, int perEntity)
        {
            using (var game = new KindWorld(count))
            {
                for (var e = 0; e < count; e++)
                    game.Start(e, KindWorld.Run);
                StepAll(game, Warmup);
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < steps; i++)
                {
                    var sender = game.Tick.BeginPulses(1);
                    var writer = sender.Writer;
                    writer.Begin(0);
                    var pulses = DamageKind.Pulses(writer);
                    for (var e = 0; e < count; e++)
                        for (var p = 0; p < perEntity; p++)
                            pulses.To(game.Rows[e], 3);
                    writer.End();
                    game.Step();
                }
                watch.Stop();
                return watch.Elapsed.TotalMilliseconds;
            }
        }
    }
}
