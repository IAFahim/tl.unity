using System;
using NUnit.Framework;
using UnityEngine;

namespace Tl.Unity.Tests
{
    [TestFixture]
    public class BurstWorldParityTests
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
                return Hash(world.Accumulator);
            }
        }

        static ulong RunBurstHash(global::Tl.Library lib, Pair[] pairs, int rows, global::Tl.Direction dir, int ticks, int workers)
        {
            using (var world = new global::Tl.Unity.BurstWorld(lib, rows))
            {
                for (var i = 0; i < pairs.Length; i++)
                    world.Start(new global::Tl.Row(pairs[i].Row), new global::Tl.TimelineId((ushort)pairs[i].Timeline), pairs[i].Phase);
                Assert.AreEqual(pairs.Length, world.ActiveCount);
                world.Run(dir, ticks, workers);
                Assert.AreEqual(ticks, world.Frame);
                return Hash(world.Accumulator.ToArray());
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
            var burstHash = RunBurstHash(lib, pairs, 1024, dir, ticks, 8);
            Debug.Log("TL-BURST case=" + name + " hash=" + burstHash.ToString("X16") + " oracle=" + oracleHash.ToString("X16") + " world=" + worldHash.ToString("X16") + " dotnet=" + dotNetHash.ToString("X16"));
            Assert.AreEqual(oracleHash, burstHash);
            Assert.AreEqual(worldHash, burstHash);
            Assert.AreEqual(dotNetHash, burstHash);
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
            var burstHash = RunBurstHash(lib, pairs, 1024, global::Tl.Direction.Forward, 97, 8);
            Debug.Log("TL-BURST case=FourPairsForwardMatchesOracle hash=" + burstHash.ToString("X16") + " oracle=" + oracleHash.ToString("X16") + " world=" + worldHash.ToString("X16") + " dotnet=" + 0x6EE2EE4A060F3325ul.ToString("X16"));
            Assert.AreEqual(oracleHash, burstHash);
            Assert.AreEqual(worldHash, burstHash);
            Assert.AreEqual(0x6EE2EE4A060F3325ul, burstHash);
        }

        void AssertThreadInvariance(string name, float[] lane, Pair[] pairs, global::Tl.Direction dir, ulong expected)
        {
            var lib = MakeLibrary(new[] { Timeline(lane.Length, lane, lane) });
            foreach (var workers in new[] { 1, 2, 4, 8 })
            {
                var burstHash = RunBurstHash(lib, pairs, 1024, dir, 97, workers);
                Debug.Log("TL-BURST case=" + name + " workers=" + workers + " hash=" + burstHash.ToString("X16"));
                Assert.AreEqual(expected, burstHash, name + " workers=" + workers);
            }
        }

        [Test]
        public void UniformThreadInvariance()
        {
            AssertThreadInvariance("UniformForwardMatchesOracle", ConstLane(40, 6f), UniformPairs(1024), global::Tl.Direction.Forward, 0x5A7C42B6B1067325ul);
        }

        [Test]
        public void MixedThreadInvariance()
        {
            AssertThreadInvariance("MixedForwardMatchesOracle", VaryingLane(40), MixedPairs(1024, 40), global::Tl.Direction.Forward, 0x19EFEF6313C65325ul);
        }

        [Test]
        public void FourPairsThreadInvariance()
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
            foreach (var workers in new[] { 1, 2, 4, 8 })
            {
                var burstHash = RunBurstHash(lib, pairs, 1024, global::Tl.Direction.Forward, 97, workers);
                Debug.Log("TL-BURST case=FourPairsForwardMatchesOracle workers=" + workers + " hash=" + burstHash.ToString("X16"));
                Assert.AreEqual(0x6EE2EE4A060F3325ul, burstHash, "workers=" + workers);
            }
        }

        [Test]
        public void SixtyFourCohortsTwoActiveMatchesOracle()
        {
            const int rows = 1024;
            var lane = ConstLane(40, 6f);
            var timelines = new global::Tl.TimelineSpec[64];
            var lanes = new float[64][];
            var durations = new int[64];
            for (var i = 0; i < 64; i++)
            {
                timelines[i] = Timeline(40, lane, lane);
                lanes[i] = lane;
                durations[i] = 40;
            }
            var lib = MakeLibrary(timelines);
            var pairs = new Pair[rows * 2];
            var k = 0;
            for (var i = 0; i < rows; i++)
            {
                pairs[k++] = new Pair { Row = i, Timeline = 1, Phase = 0 };
                pairs[k++] = new Pair { Row = i, Timeline = 2, Phase = 0 };
            }
            var expected = new float[rows];
            OracleRun(expected, pairs, lanes, durations, 97, 1);
            var oracleHash = Hash(expected);
            var worldHash = RunWorldHash(lib, pairs, rows, global::Tl.Direction.Forward, 97);
            var burst8 = RunBurstHash(lib, pairs, rows, global::Tl.Direction.Forward, 97, 8);
            var burst1 = RunBurstHash(lib, pairs, rows, global::Tl.Direction.Forward, 97, 1);
            Debug.Log("TL-BURST case=SixtyFourCohortsTwoActive hash=" + burst8.ToString("X16") + " oracle=" + oracleHash.ToString("X16") + " world=" + worldHash.ToString("X16") + " workers1=" + burst1.ToString("X16"));
            Assert.AreEqual(oracleHash, burst8);
            Assert.AreEqual(worldHash, burst8);
            Assert.AreEqual(burst8, burst1);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(8)]
        public void ChunkPartitionsCoverEveryIndexExactlyOnce(int workers)
        {
            foreach (var total in new[] { 1, 37, 999, 1024 })
            {
                var count = global::Tl.Unity.ParallelApplyBurst.ChunkCount(total, workers);
                Assert.AreEqual(Math.Min(workers, total), count, "total=" + total + " workers=" + workers);
                var cursor = 0;
                for (var chunk = 0; chunk < count; chunk++)
                {
                    global::Tl.Unity.ParallelApplyBurst.Partition(total, workers, chunk, out var start, out var end);
                    Assert.AreEqual(cursor, start, "total=" + total + " workers=" + workers + " chunk=" + chunk);
                    Assert.IsTrue(end > start, "total=" + total + " workers=" + workers + " chunk=" + chunk);
                    cursor = end;
                }
                Assert.AreEqual(total, cursor, "total=" + total + " workers=" + workers);
            }
        }

        [Test]
        public void InvalidStartsAreCountedNotThrown()
        {
            var lib = MakeLibrary(new[] { Timeline(40, ConstLane(40, 6f), ConstLane(40, 6f)) });
            using (var world = new global::Tl.Unity.BurstWorld(lib, 1024))
            {
                world.Start(new global::Tl.Row(-1), new global::Tl.TimelineId(1), 0);
                world.Start(new global::Tl.Row(1024), new global::Tl.TimelineId(1), 0);
                world.Start(new global::Tl.Row(0), new global::Tl.TimelineId(99), 0);
                world.Run(global::Tl.Direction.Forward, 1, 8);
                Assert.AreEqual(3, world.LastInvalidRequests);
                Assert.AreEqual(0, world.ActiveCount);
                Assert.AreEqual(0f, world.Accumulator[0]);
                world.Start(new global::Tl.Row(5), new global::Tl.TimelineId(1), 0);
                world.Run(global::Tl.Direction.Forward, 1, 8);
                Assert.AreEqual(0, world.LastInvalidRequests);
                Assert.AreEqual(1, world.ActiveCount);
                Assert.AreEqual(6f, world.Accumulator[5]);
            }
        }
    }
}
