using System;
using NUnit.Framework;
using UnityEngine;

namespace Tl.Unity.Tests
{
    [TestFixture]
    public class WorldV5ParityTests
    {
        struct Pair
        {
            public int Row;
            public int Timeline;
            public int Phase;
        }

        static int OracleTick(int phase, int frame, int duration, int dir)
        {
            var t = phase + dir * frame;
            t %= duration;
            if (t < 0) t += duration;
            return t;
        }

        static void OracleRun(float[] acc, Pair[] pairs, float[][] lanes, int[] durations, int ticks, int dir)
        {
            var sorted = new Pair[pairs.Length];
            Array.Copy(pairs, sorted, pairs.Length);
            for (var i = 1; i < sorted.Length; i++)
            {
                var p = sorted[i];
                var j = i - 1;
                while (j >= 0 && (sorted[j].Timeline > p.Timeline || (sorted[j].Timeline == p.Timeline && sorted[j].Row > p.Row)))
                {
                    sorted[j + 1] = sorted[j];
                    j--;
                }
                sorted[j + 1] = p;
            }
            for (var f = 1; f <= ticks; f++)
            {
                for (var i = 0; i < sorted.Length; i++)
                {
                    var p = sorted[i];
                    acc[p.Row] += lanes[p.Timeline - 1][OracleTick(p.Phase, f, durations[p.Timeline - 1], dir)];
                }
            }
        }

        static ulong Hash(float[] acc)
        {
            var h = 14695981039346656037ul;
            for (var i = 0; i < acc.Length; i++)
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(acc[i])) * 1099511628211ul;
            return h;
        }

        static float[] ConstLane(int duration, float value)
        {
            var lane = new float[duration];
            for (var i = 0; i < duration; i++) lane[i] = value;
            return lane;
        }

        static float[] VaryingLane(int duration)
        {
            var lane = new float[duration];
            for (var i = 0; i < duration; i++) lane[i] = 1f + (i * 7 % 13) * 0.5f + (i % 5) * 0.25f;
            return lane;
        }

        static global::Tl.TimelineSpec Timeline(int duration, float[] laneF, float[] laneR, uint kindId = 0)
        {
            return new global::Tl.TimelineSpec((uint)duration, true, new[]
            {
                new global::Tl.StepSpec(0u, (uint)duration, kindId, 1ul, laneF, laneR),
            });
        }

        static global::Tl.Library MakeLibrary(global::Tl.TimelineSpec[] timelines)
        {
            var blob = global::Tl.LibraryWriter.Write(timelines);
            var result = global::Tl.Library.Verify(blob);
            Assert.IsTrue(result.Ok);
            Assert.IsNotNull(result.Library);
            return result.Library!;
        }

        static Pair[] UniformPairs(int count)
        {
            var pairs = new Pair[count];
            for (var i = 0; i < count; i++) pairs[i] = new Pair { Row = i, Timeline = 1, Phase = 0 };
            return pairs;
        }

        static Pair[] MixedPairs(int count, int duration)
        {
            var pairs = new Pair[count];
            for (var i = 0; i < count; i++) pairs[i] = new Pair { Row = i, Timeline = 1, Phase = (i * 37 + 11) % duration };
            return pairs;
        }

        static Pair[] UniquePairs(int count, int duration)
        {
            var pairs = new Pair[count];
            for (var i = 0; i < count; i++) pairs[i] = new Pair { Row = i, Timeline = 1, Phase = i % duration };
            return pairs;
        }

        static ulong RunWorldHash(global::Tl.Library lib, Pair[] pairs, int rows, global::Tl.Direction dir, int ticks)
        {
            using (var world = new global::Tl.World(lib, rows))
            {
                for (var i = 0; i < pairs.Length; i++)
                    world.Requests.Start(new global::Tl.Row(pairs[i].Row), new global::Tl.TimelineId((ushort)pairs[i].Timeline), pairs[i].Phase);
                for (var f = 0; f < ticks; f++) world.Tick(dir);
                Assert.AreEqual(ticks, world.Frame);
                Assert.AreEqual(pairs.Length, world.ActiveCount);
                return Hash(world.Accumulator);
            }
        }

        static void CheckShape(string name, float[] lane, Pair[] pairs, global::Tl.Direction dir, int ticks, ulong dotNetHash)
        {
            var lib = MakeLibrary(new[] { Timeline(lane.Length, lane, lane) });
            var expected = new float[1024];
            var dirSign = dir == global::Tl.Direction.Reverse ? -1 : 1;
            OracleRun(expected, pairs, new[] { lane }, new[] { lane.Length }, ticks, dirSign);
            var oracleHash = Hash(expected);
            var worldHash = RunWorldHash(lib, pairs, 1024, dir, ticks);
            Debug.Log("TL-PARITY case=" + name + " hash=" + worldHash.ToString("X16") + " oracle=" + oracleHash.ToString("X16") + " dotnet=" + dotNetHash.ToString("X16"));
            Assert.AreEqual(oracleHash, worldHash);
            Assert.AreEqual(dotNetHash, worldHash);
        }

        [Test]
        public void UniformForwardMatchesOracle()
        {
            CheckShape("UniformForwardMatchesOracle", ConstLane(40, 6f), UniformPairs(1024), global::Tl.Direction.Forward, 97, 0x5A7C42B6B1067325ul);
        }

        [Test]
        public void UniformRewindMatchesOracle()
        {
            CheckShape("UniformRewindMatchesOracle", ConstLane(40, 6f), UniformPairs(1024), global::Tl.Direction.Reverse, 97, 0x5A7C42B6B1067325ul);
        }

        [Test]
        public void MixedForwardMatchesOracle()
        {
            CheckShape("MixedForwardMatchesOracle", VaryingLane(40), MixedPairs(1024, 40), global::Tl.Direction.Forward, 97, 0x19EFEF6313C65325ul);
        }

        [Test]
        public void MixedRewindMatchesOracle()
        {
            CheckShape("MixedRewindMatchesOracle", VaryingLane(40), MixedPairs(1024, 40), global::Tl.Direction.Reverse, 97, 0xB9EF75341D971325ul);
        }

        [Test]
        public void UniqueForwardMatchesOracle()
        {
            CheckShape("UniqueForwardMatchesOracle", VaryingLane(1024), UniquePairs(1024, 1024), global::Tl.Direction.Forward, 97, 0xD77E142E80A83325ul);
        }

        [Test]
        public void UniqueRewindMatchesOracle()
        {
            CheckShape("UniqueRewindMatchesOracle", VaryingLane(1024), UniquePairs(1024, 1024), global::Tl.Direction.Reverse, 97, 0x2627DB56B4AC7325ul);
        }

        [Test]
        public void FourPairsForwardMatchesOracle()
        {
            var lanes = new[] { ConstLane(40, 6f), ConstLane(40, 7f), ConstLane(40, 8f), ConstLane(40, 9f) };
            var lib = MakeLibrary(new[]
            {
                Timeline(40, lanes[0], lanes[0]),
                Timeline(40, lanes[1], lanes[1]),
                Timeline(40, lanes[2], lanes[2]),
                Timeline(40, lanes[3], lanes[3]),
            });
            var pairs = new Pair[4096];
            var k = 0;
            for (var t = 1; t <= 4; t++)
                for (var i = 0; i < 1024; i++) pairs[k++] = new Pair { Row = i, Timeline = t, Phase = 0 };
            var expected = new float[1024];
            OracleRun(expected, pairs, lanes, new[] { 40, 40, 40, 40 }, 97, 1);
            var oracleHash = Hash(expected);
            var worldHash = RunWorldHash(lib, pairs, 1024, global::Tl.Direction.Forward, 97);
            Debug.Log("TL-PARITY case=FourPairsForwardMatchesOracle hash=" + worldHash.ToString("X16") + " oracle=" + oracleHash.ToString("X16") + " dotnet=" + 0x6EE2EE4A060F3325ul.ToString("X16"));
            Assert.AreEqual(oracleHash, worldHash);
            Assert.AreEqual(0x6EE2EE4A060F3325ul, worldHash);
        }

        [Test]
        public void WarmTickAllocatesZeroBytes()
        {
            var lib = MakeLibrary(new[] { Timeline(40, ConstLane(40, 6f), ConstLane(40, 6f)) });
            using (var world = new global::Tl.World(lib, 1024))
            {
                for (var i = 0; i < 1024; i++) world.Requests.Start(new global::Tl.Row(i), new global::Tl.TimelineId(1), 0);
                for (var f = 0; f < 100; f++) world.Tick(global::Tl.Direction.Forward);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var f = 0; f < 50; f++) world.Tick(global::Tl.Direction.Forward);
                var delta = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(0, delta);
            }
        }

        [Test]
        public void MixedWarmTickAllocatesZeroBytes()
        {
            var lib = MakeLibrary(new[] { Timeline(40, VaryingLane(40), VaryingLane(40)) });
            var pairs = MixedPairs(1024, 40);
            using (var world = new global::Tl.World(lib, 1024))
            {
                for (var i = 0; i < pairs.Length; i++)
                    world.Requests.Start(new global::Tl.Row(pairs[i].Row), new global::Tl.TimelineId(1), pairs[i].Phase);
                for (var f = 0; f < 100; f++) world.Tick(global::Tl.Direction.Reverse);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var f = 0; f < 50; f++) world.Tick(global::Tl.Direction.Reverse);
                var delta = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(0, delta);
            }
        }

        [Test]
        public void InvalidStartsAreCountedNotThrown()
        {
            var lib = MakeLibrary(new[] { Timeline(40, ConstLane(40, 6f), ConstLane(40, 6f)) });
            using (var world = new global::Tl.World(lib, 1024))
            {
                world.Requests.Start(new global::Tl.Row(-1), new global::Tl.TimelineId(1));
                world.Requests.Start(new global::Tl.Row(1024), new global::Tl.TimelineId(1));
                world.Requests.Start(new global::Tl.Row(0), new global::Tl.TimelineId(99));
                world.Tick(global::Tl.Direction.Forward);
                Assert.AreEqual(3, world.Report.InvalidRequests);
                Assert.AreEqual(0, world.ActiveCount);
                Assert.AreEqual(0f, world.Accumulator[0]);
                world.Requests.Start(new global::Tl.Row(5), new global::Tl.TimelineId(1), 0);
                world.Tick(global::Tl.Direction.Forward);
                Assert.AreEqual(0, world.Report.InvalidRequests);
                Assert.AreEqual(1, world.Report.ActiveCount);
                Assert.AreEqual(6f, world.Accumulator[5]);
            }
        }

        [Test]
        public void BroadcastDenseMatchesScalarLoop()
        {
            const int count = 1024;
            const int off = 37;
            var a = new float[count + off + 64];
            var b = new float[count + off + 64];
            for (var i = 0; i < a.Length; i++)
            {
                var v = (i % 13) * 0.25f;
                a[i] = v;
                b[i] = v;
            }
            global::Tl.Kernels.BroadcastDense(a, off, 3.5f, count);
            for (var i = 0; i < count; i++) b[off + i] += 3.5f;
            Assert.AreEqual(b, a);
        }

        [Test]
        public void LaneKernelsMatchScalarOracle()
        {
            const int n = 1024;
            var lane = VaryingLane(40);
            var phase = new ushort[n];
            var row = new int[n];
            for (var i = 0; i < n; i++)
            {
                phase[i] = (ushort)((i * 37 + 11) % 40);
                row[i] = i;
            }
            var a = new float[n];
            var b = new float[n];
            const int frame = 13;
            const int dir = -1;
            global::Tl.Kernels.LaneGatherScalar(a, 0, row, phase, lane, frame, 40, dir);
            for (var i = 0; i < n; i++) b[row[i]] += lane[OracleTick(phase[i], frame, 40, dir)];
            Assert.AreEqual(b, a);
            var c = new float[40];
            var d = new float[40];
            global::Tl.Kernels.LaneDense(c, 0, lane, 40);
            for (var i = 0; i < 40; i++) d[i] += lane[i];
            Assert.AreEqual(d, c);
        }

        [Test]
        public void RunBroadcastMatchesScalarOracle()
        {
            var lane = VaryingLane(10);
            var sortedRow = new int[] { 7, 3, 1, 5, 2, 6, 0, 4 };
            var runs = new[]
            {
                new global::Tl.Run(0, 3, 5),
                new global::Tl.Run(3, 2, 2),
                new global::Tl.Run(5, 3, 9),
            };
            var phases = new ushort[] { 5, 5, 5, 2, 2, 9, 9, 9 };
            const int frame = 3;
            const int dir = -1;
            var a = new float[8];
            var b = new float[8];
            global::Tl.Kernels.RunBroadcast(a, 0, sortedRow, runs, lane, frame, 10, dir);
            for (var i = 0; i < 8; i++) b[sortedRow[i]] += lane[OracleTick(phases[i], frame, 10, dir)];
            Assert.AreEqual(b, a);
        }

        [Test]
        public void StableRadixSortsStably()
        {
            var keys = new uint[] { 3, 1, 3, 0, 2 };
            var order = new int[] { 0, 1, 2, 3, 4 };
            var scratch = new int[5];
            global::Tl.Kernels.StableRadix(keys, order, scratch);
            Assert.AreEqual(new[] { 3, 1, 4, 0, 2 }, order);
        }
    }
}
