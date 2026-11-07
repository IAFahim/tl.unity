using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace DirectGame
{
    public static class DirectBench
    {
        const int N = 100000;
        const int Warmup = 10;
        const int Measure = 200;

        public static void RunBatch()
        {
            var path = Path.Combine(Application.dataPath, "..", "KindsBench.log");
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-kindsBench")
                    path = args[i + 1];
            KindGame.KindBench.Run(path);
            using (var log = new StreamWriter(path, append: true))
            {
                Row(log, "S6-A all-playing", N, 1, true);
                Row(log, "S6-B tenth-playing", N, 10, false);
                Row(log, "S6-B0 none-playing", N, 0, false);
            }
        }

        static void Row(StreamWriter log, string label, int count, int every, bool allocation)
        {
            using (var game = new DirectWorld(count))
            {
                if (every > 0)
                    for (var e = 0; e < count; e += every)
                        game.Start(e, DirectWorld.Stroll);
                for (var i = 0; i < Warmup; i++)
                    game.Step();
                var before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < Measure; i++)
                    game.Step();
                watch.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                var ns = watch.Elapsed.TotalMilliseconds * 1000000.0 / ((double)count * Measure);
                var line = "TlKindBench: " + label + " step=" + ns.ToString("F1") + "ns/entity" + (allocation ? " allocationB=" + allocated : "");
                log.WriteLine(line);
                UnityEngine.Debug.Log(line);
            }
        }
    }
}
