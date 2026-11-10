using System;
using Stopwatch = System.Diagnostics.Stopwatch;
using NUnit.Framework;
using UnityEngine;

namespace Tl.Unity.Tests
{
    // MEASURE-ONLY port of the tl PhaseClock LargeBench 1M hostile shape
    // (benchmarks/PhaseClock/LargeBench.cs in tl): duration 1024, mixed phase
    // (e*37+11)%1024, sparse permuted rows (e*97)%N, N=1_000_000, one timeline,
    // varying lane, full step window [0,1024). Asserts hash parity (Burst ==
    // scalar oracle == sequential World, World-frame 1..97) and logs tick
    // timings; no timing thresholds (cross-machine flaky, not a contract).
    // The kernel-frame (f=0..96) scalar hash must equal tl bench-large's
    // hostile-1M LaneGatherScalar oracle A8B9A0A1DC37D025, proving the shape
    // is byte-identical to the .NET benchmark shape. Times are logged as
    // TL-LARGE ... lines and compared against tl bench-large 1M kernels
    // (ScalarPhase 1.307, LaneGatherScalar 1.294, BroadcastSparse 0.688,
    // RunBroadcast 1.571 ns/entity, all CoreCLR single-thread).
    [TestFixture]
    public class BurstWorldLargeBenchTests
    {
        const int N = 1_000_000;
        const int Duration = 1024;
        const int Ticks = 97;
        const ulong KernelFrameExpected = 0xA8B9A0A1DC37D025ul;

        static float[] VaryingLane(int duration)
        {
            var lane = new float[duration];
            for (var i = 0; i < duration; i++) lane[i] = 1f + (i * 7 % 13) * 0.5f + (i % 5) * 0.25f;
            return lane;
        }

        static global::Tl.Library MakeLibrary(float[] lane)
        {
            var blob = global::Tl.LibraryWriter.Write(new[]
            {
                new global::Tl.TimelineSpec((uint)Duration, true, new[]
                {
                    new global::Tl.StepSpec(0u, (uint)Duration, 0u, 1ul, lane, lane),
                }),
            });
            var result = global::Tl.Library.Verify(blob);
            Assert.IsTrue(result.Ok);
            Assert.IsNotNull(result.Library);
            return result.Library!;
        }

        static void Generate(int[] rows, ushort[] phases)
        {
            for (var e = 0; e < N; e++)
            {
                phases[e] = (ushort)((e * 37 + 11) % Duration);
                rows[e] = (int)((e * 97L) % N);
            }
        }

        static ulong Hash(float[] acc)
        {
            var h = 14695981039346656037ul;
            for (var i = 0; i < acc.Length; i++)
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(acc[i])) * 1099511628211ul;
            return h;
        }

        // ScalarPhase with kernel frame base f=0..96 (tl LargeBench OracleHashes).
        static ulong KernelFrameHash(int[] rows, ushort[] phases, float[] lane)
        {
            var acc = new float[N];
            for (var f = 0; f < Ticks; f++)
                for (var e = 0; e < N; e++)
                {
                    var t = phases[e] + f;
                    if (t >= Duration) t -= Duration;
                    acc[rows[e]] += lane[t];
                }
            return Hash(acc);
        }

        // Scalar oracle with World/BurstWorld frame base f=1..97 (World.Tick frame++).
        static ulong WorldFrameOracleHash(int[] rows, ushort[] phases, float[] lane)
        {
            var acc = new float[N];
            for (var f = 1; f <= Ticks; f++)
                for (var e = 0; e < N; e++)
                {
                    var t = phases[e] + f;
                    if (t >= Duration) t -= Duration;
                    acc[rows[e]] += lane[t];
                }
            return Hash(acc);
        }

        static double MeasureTicks(int warmupTicks, int rounds, int ticksPerRound, Action tick)
        {
            for (var i = 0; i < warmupTicks; i++) tick();
            var samples = new double[rounds];
            for (var r = 0; r < rounds; r++)
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < ticksPerRound; i++) tick();
                sw.Stop();
                samples[r] = sw.Elapsed.TotalSeconds * 1e9 / ticksPerRound / N;
            }
            Array.Sort(samples);
            return samples[0];
        }

        static void LogTime(string name, double nsPerEntity, int rounds, int ticksPerRound, int warmupTicks)
        {
            var msPerTick = nsPerEntity * N / 1e6;
            Debug.Log("TL-LARGE TIME " + name + " nsPerEntity=" + nsPerEntity.ToString("F3")
                + " msPerTick=" + msPerTick.ToString("F4")
                + " rounds=" + rounds + " ticksPerRound=" + ticksPerRound
                + " warmupTicks=" + warmupTicks + " min");
        }

        [Test]
        public void Large1MMixedPhaseSparse_BurstMatchesOracle_AndLogsTickTime()
        {
            var lane = VaryingLane(Duration);
            var rows = new int[N];
            var phases = new ushort[N];
            Generate(rows, phases);
            Debug.Log("TL-LARGE shape n=1000000 duration=1024 phase=(e*37+11)%1024 row=(e*97)%N lane=VaryingLane window=[0,1024) ticks=97");

            var kernelFrameHash = KernelFrameHash(rows, phases, lane);
            Debug.Log("TL-LARGE kernel-frame hash=" + kernelFrameHash.ToString("X16")
                + " expected=" + KernelFrameExpected.ToString("X16")
                + " (tl bench-large hostile-1M LaneGatherScalar oracle)");
            Assert.AreEqual(KernelFrameExpected, kernelFrameHash, "kernel-frame hash must match tl bench-large 1M oracle");

            using (var lib = MakeLibrary(lane))
            {
                ulong burstHash;
                ulong worldHash;
                ulong oracleHash;
                long burstSetupMs;
                long worldSetupMs;

                using (var burst = new global::Tl.Unity.BurstWorld(lib, N))
                {
                    var sw = Stopwatch.StartNew();
                    for (var e = 0; e < N; e++)
                        burst.Start(new global::Tl.Row(rows[e]), new global::Tl.TimelineId(1), phases[e]);
                    sw.Stop();
                    burstSetupMs = sw.ElapsedMilliseconds;
                    Assert.AreEqual(N, burst.ActiveCount);
                    sw.Restart();
                    burst.Run(global::Tl.Direction.Forward, Ticks, 8);
                    sw.Stop();
                    Debug.Log("TL-LARGE setup burstWorld collectMs=" + burstSetupMs
                        + " firstRun97Ms=" + sw.ElapsedMilliseconds);
                    Assert.AreEqual(Ticks, burst.Frame);
                    burstHash = Hash(burst.Accumulator.ToArray());

                    const int warmupTicks = 30;
                    const int rounds = 5;
                    const int ticksPerRound = 60;
                    LogTime("burst-tick-loop workers=0", MeasureTicks(warmupTicks, rounds, ticksPerRound,
                        () => burst.Tick(global::Tl.Direction.Forward, 0)), rounds, ticksPerRound, warmupTicks);
                    LogTime("burst-tick-loop workers=1", MeasureTicks(warmupTicks, rounds, ticksPerRound,
                        () => burst.Tick(global::Tl.Direction.Forward, 1)), rounds, ticksPerRound, warmupTicks);

                    Debug.Log("TL-LARGE sink burstHashSuffix=" + (Hash(burst.Accumulator.ToArray()) & 0xff).ToString("X2"));
                }

                using (var world = new global::Tl.World(lib, N))
                {
                    var sw = Stopwatch.StartNew();
                    for (var e = 0; e < N; e++)
                        world.Requests.Start(new global::Tl.Row(rows[e]), new global::Tl.TimelineId(1), phases[e]);
                    sw.Stop();
                    worldSetupMs = sw.ElapsedMilliseconds;
                    Assert.AreEqual(N, world.ActiveCount);
                    sw.Restart();
                    for (var f = 0; f < Ticks; f++) world.Tick(global::Tl.Direction.Forward);
                    sw.Stop();
                    Debug.Log("TL-LARGE setup sequentialWorld collectMs=" + worldSetupMs
                        + " first97TicksMs=" + sw.ElapsedMilliseconds);
                    worldHash = Hash(world.Accumulator);
                    const int warmupTicks = 30;
                    const int rounds = 5;
                    const int ticksPerRound = 60;
                    LogTime("world-tick-loop sequential single-thread", MeasureTicks(warmupTicks, rounds, ticksPerRound,
                        () => world.Tick(global::Tl.Direction.Forward)), rounds, ticksPerRound, warmupTicks);
                    Debug.Log("TL-LARGE sink worldHashSuffix=" + (Hash(world.Accumulator) & 0xff).ToString("X2"));
                }

                oracleHash = WorldFrameOracleHash(rows, phases, lane);
                Debug.Log("TL-LARGE hashes oracle=" + oracleHash.ToString("X16")
                    + " burst=" + burstHash.ToString("X16")
                    + " world=" + worldHash.ToString("X16"));
                Assert.AreEqual(oracleHash, burstHash, "BurstWorld 1M hash must equal scalar oracle");
                Assert.AreEqual(oracleHash, worldHash, "sequential World 1M hash must equal scalar oracle");
            }
        }
    }
}
