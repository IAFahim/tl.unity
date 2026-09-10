using System;
using NUnit.Framework;
using Tl;
using TlUnity.PlayerProbe;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Tl.Unity.Tests
{
    public sealed class RuntimeTests
    {
        [Test]
        public void GeneratedSignedSeekRunsInsideBurstEcsJob()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled);
            using (var world = new World("Tl Burst gate"))
            {
                var manager = world.EntityManager;
                var entity = manager.CreateEntity(
                    typeof(GatePlayback),
                    typeof(CurrentPose),
                    typeof(NextPose),
                    typeof(TargetHealth),
                    typeof(GateReceipt));
                manager.SetComponentData(entity, new GatePlayback { StartGameTick = 10, Delta = 2 });
                manager.SetComponentData(entity, new CurrentPose { Value = new Pose { X = 3, Y = 4 } });
                manager.SetComponentData(entity, new TargetHealth { Value = new Health { Value = 100 } });
                world.CreateSystem<AdvanceGateSystem>().Update(world.Unmanaged);
                var state = manager.GetComponentData<GatePlayback>(entity).Value;
                Assert.AreEqual(2L, state.Position);
                Assert.AreEqual(12u, state.GameTick);
                Assert.AreEqual(5f, manager.GetComponentData<NextPose>(entity).Value.X);
                Assert.AreEqual(5f, manager.GetComponentData<NextPose>(entity).Value.Y);
                Assert.AreEqual(90f, manager.GetComponentData<TargetHealth>(entity).Value.Value);
                Assert.AreEqual(3, manager.GetComponentData<GateReceipt>(entity).Value.Calls);
            }
        }

        [Test]
        public void SignedSeekReplaysFramesAndReportsBoundaries()
        {
            var current = new Pose { X = 3, Y = 4 };
            var next = default(Pose);
            var health = new Health { Value = 100 };
            var receipt = default(Receipt);
            var playback = Gate.Start(100);
            var data = new Gate.Data(ref playback, in current, ref next, ref health, ref receipt);

            Assert.AreEqual(0L, playback.Position);
            Assert.AreEqual(100u, playback.GameTick);
            Assert.IsTrue(Gate.TrySeek(ref data, 0));
            Assert.AreEqual(0, receipt.Calls);
            Assert.IsTrue(Gate.TrySeek(ref data, 4));
            Assert.AreEqual(4L, playback.Position);
            Assert.AreEqual(104u, playback.GameTick);
            Assert.AreEqual(5f, next.X);
            Assert.AreEqual(5f, next.Y);
            Assert.AreEqual(90f, health.Value);
            AssertReceipt(receipt, 5, 2, 2, 2, 0, 5, 1, 1, 0, 1);
            Assert.AreEqual(103u, receipt.LastGameTick);
            Assert.AreEqual(3u, receipt.LastTimelineTick);
            Assert.AreEqual(0L, receipt.LastCycle);
            Assert.IsTrue((receipt.LastFlags & (FrameFlags.ClipEnd | FrameFlags.TimelineEnd | FrameFlags.CompletedAfter))
                == (FrameFlags.ClipEnd | FrameFlags.TimelineEnd | FrameFlags.CompletedAfter));

            Assert.IsTrue(Gate.TrySeek(ref data, -4));
            Assert.AreEqual(0L, playback.Position);
            Assert.AreEqual(100u, playback.GameTick);
            Assert.AreEqual(1f, next.X);
            Assert.AreEqual(3f, next.Y);
            Assert.AreEqual(100f, health.Value);
            AssertReceipt(receipt, 10, 4, 4, 4, 5, 0, 2, 2, 1, 1);
            Assert.AreEqual(100u, receipt.LastGameTick);
            Assert.AreEqual(0u, receipt.LastTimelineTick);
            Assert.AreEqual(0L, receipt.LastCycle);
            Assert.IsTrue((receipt.LastFlags & (FrameFlags.ClipStart | FrameFlags.TimelineStart | FrameFlags.Reverse))
                == (FrameFlags.ClipStart | FrameFlags.TimelineStart | FrameFlags.Reverse));
        }

        [Test]
        public void LoopingSeekNormalizesNegativeCyclesAndWrapsGameTicks()
        {
            var receipt = default(Receipt);
            var playback = LoopGate.Start(0);
            var data = new LoopGate.Data(ref playback, ref receipt);

            Assert.IsTrue(LoopGate.TrySeek(ref data, -4));
            Assert.AreEqual(-4L, playback.Position);
            Assert.AreEqual(uint.MaxValue - 3u, playback.GameTick);
            Assert.AreEqual(4, receipt.Calls);
            Assert.AreEqual(4, receipt.ReverseCalls);
            Assert.AreEqual(-4, receipt.DirectionTotal);
            Assert.AreEqual(2u, receipt.LastTimelineTick);
            Assert.AreEqual(-2L, receipt.LastCycle);
            Assert.AreEqual(uint.MaxValue - 3u, receipt.LastGameTick);
            Assert.IsTrue((receipt.LastFlags & (FrameFlags.Looping | FrameFlags.Reverse | FrameFlags.TimelineEnd | FrameFlags.ClipEnd))
                == (FrameFlags.Looping | FrameFlags.Reverse | FrameFlags.TimelineEnd | FrameFlags.ClipEnd));

            Assert.IsTrue(LoopGate.TrySeek(ref data, 7));
            Assert.AreEqual(3L, playback.Position);
            Assert.AreEqual(3u, playback.GameTick);
            Assert.AreEqual(11, receipt.Calls);
            Assert.AreEqual(4, receipt.ReverseCalls);
            Assert.AreEqual(3, receipt.DirectionTotal);
            Assert.AreEqual(2u, receipt.LastTimelineTick);
            Assert.AreEqual(0L, receipt.LastCycle);
            Assert.AreEqual(2u, receipt.LastGameTick);

            playback = new Playback(long.MaxValue, 11u, LoopGate.Id, PlaybackFlags.Started);
            data = new LoopGate.Data(ref playback, ref receipt);
            var before = receipt;
            Assert.IsFalse(LoopGate.TrySeek(ref data, 1));
            Assert.AreEqual(long.MaxValue, playback.Position);
            Assert.AreEqual(before.Calls, receipt.Calls);

            playback = new Playback(long.MinValue, 11u, LoopGate.Id, PlaybackFlags.Started);
            data = new LoopGate.Data(ref playback, ref receipt);
            Assert.IsFalse(LoopGate.TrySeek(ref data, -1));
            Assert.AreEqual(long.MinValue, playback.Position);
            Assert.AreEqual(before.Calls, receipt.Calls);
        }

        [Test]
        public void AliasedInputAndOutputObserveWritesInOrder()
        {
            var pose = default(Pose);
            var health = new Health { Value = 100 };
            var receipt = default(Receipt);
            var playback = Gate.Start(uint.MaxValue - 1u);
            var data = new Gate.Data(ref playback, in pose, ref pose, ref health, ref receipt);

            Assert.IsTrue(Gate.TrySeek(ref data, 4));
            Assert.AreEqual(8f, pose.X);
            Assert.AreEqual(4f, pose.Y);
            Assert.AreEqual(90f, health.Value);
            Assert.AreEqual(2u, playback.GameTick);
            Assert.IsTrue(Gate.TrySeek(ref data, -4));
            Assert.AreEqual(0f, pose.X);
            Assert.AreEqual(0f, pose.Y);
            Assert.AreEqual(100f, health.Value);
            Assert.AreEqual(uint.MaxValue - 1u, playback.GameTick);
        }

        [Test]
        public void RejectionsAreAtomicAndStopIsIdempotent()
        {
            var current = new Pose { X = 3, Y = 4 };
            var next = default(Pose);
            var health = new Health { Value = 100 };
            var receipt = default(Receipt);
            var playback = Gate.Start(10);
            var data = new Gate.Data(ref playback, in current, ref next, ref health, ref receipt);

            AssertRejected(ref data, ref playback, ref next, ref health, ref receipt, -1);
            AssertRejected(ref data, ref playback, ref next, ref health, ref receipt, int.MinValue);
            Assert.IsTrue(Gate.TryStop(in playback, out playback));
            var stopped = playback;
            Assert.IsTrue(Gate.TryStop(in playback, out playback));
            Assert.AreEqual(stopped, playback);
            AssertRejected(ref data, ref playback, ref next, ref health, ref receipt, 1);

            var empty = default(Gate.Data);
            Assert.IsFalse(Gate.TrySeek(ref empty, 0));
        }

        [Test]
        public void WarmSignedSeekAllocatesZeroManagedBytes()
        {
            var pose = default(Pose);
            var health = new Health { Value = 100 };
            var receipt = default(Receipt);
            var playback = Gate.Start(0);
            var data = new Gate.Data(ref playback, in pose, ref pose, ref health, ref receipt);
            for (var i = 0; i < 128; i++)
                if (!Gate.TrySeek(ref data, (i & 1) == 0 ? 1 : -1))
                    throw new InvalidOperationException();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1048576; i++)
                if (!Gate.TrySeek(ref data, (i & 1) == 0 ? 1 : -1))
                    throw new InvalidOperationException();
            Assert.AreEqual(before, GC.GetAllocatedBytesForCurrentThread());
            GC.KeepAlive(pose.X + health.Value + receipt.Calls + playback.Position);
        }

        [Test]
        public void RuntimeStateAndPlanBlobHaveCurrentLayouts()
        {
            Assert.IsTrue(UnsafeUtility.IsUnmanaged<Playback>());
            Assert.IsTrue(UnsafeUtility.IsUnmanaged<Gate.Data>());
            Assert.AreEqual(16, UnsafeUtility.SizeOf<Playback>());
            Assert.AreEqual(1, UnsafeUtility.SizeOf<PlaybackFlags>());
            Assert.AreEqual(1, UnsafeUtility.SizeOf<FrameFlags>());
            Assert.AreEqual(1, (byte)FrameFlags.ClipStart);
            Assert.AreEqual(2, (byte)FrameFlags.ClipEnd);
            Assert.AreEqual(4, (byte)FrameFlags.TimelineStart);
            Assert.AreEqual(8, (byte)FrameFlags.TimelineEnd);
            Assert.AreEqual(16, (byte)FrameFlags.CompletedBefore);
            Assert.AreEqual(32, (byte)FrameFlags.CompletedAfter);
            Assert.AreEqual(64, (byte)FrameFlags.Looping);
            Assert.AreEqual(128, (byte)FrameFlags.Reverse);
            using (var blob = Gate.CreateBlob(Allocator.TempJob))
            {
                ref var root = ref blob.Value;
                Assert.AreEqual(1, root.PlanVersion);
                Assert.AreEqual(Gate.Id, root.TimelineId);
                Assert.AreEqual(Gate.Duration, root.Duration);
                Assert.AreEqual(2, root.Tracks.Length);
                Assert.AreEqual(2, root.Clips.Length);
                Assert.AreEqual(1u, root.Clips[1].Start);
                Assert.AreEqual(2u, root.Clips[1].End);
            }
        }

        private static void AssertRejected(
            ref Gate.Data data,
            ref Playback playback,
            ref Pose next,
            ref Health health,
            ref Receipt receipt,
            int delta)
        {
            var beforePlayback = playback;
            var beforePose = next;
            var beforeHealth = health;
            var beforeReceipt = receipt;
            Assert.IsFalse(Gate.TrySeek(ref data, delta));
            Assert.AreEqual(beforePlayback, playback);
            Assert.AreEqual(beforePose.X, next.X);
            Assert.AreEqual(beforePose.Y, next.Y);
            Assert.AreEqual(beforeHealth.Value, health.Value);
            AssertReceipt(
                receipt,
                beforeReceipt.Calls,
                beforeReceipt.Starts,
                beforeReceipt.Interiors,
                beforeReceipt.Ends,
                beforeReceipt.ReverseCalls,
                beforeReceipt.DirectionTotal,
                beforeReceipt.TimelineStarts,
                beforeReceipt.TimelineEnds,
                beforeReceipt.CompletedBefore,
                beforeReceipt.CompletedAfter);
        }

        private static void AssertReceipt(
            Receipt actual,
            int calls,
            int starts,
            int interiors,
            int ends,
            int reverseCalls,
            int directionTotal,
            int timelineStarts,
            int timelineEnds,
            int completedBefore,
            int completedAfter)
        {
            Assert.AreEqual(calls, actual.Calls);
            Assert.AreEqual(starts, actual.Starts);
            Assert.AreEqual(interiors, actual.Interiors);
            Assert.AreEqual(ends, actual.Ends);
            Assert.AreEqual(reverseCalls, actual.ReverseCalls);
            Assert.AreEqual(directionTotal, actual.DirectionTotal);
            Assert.AreEqual(timelineStarts, actual.TimelineStarts);
            Assert.AreEqual(timelineEnds, actual.TimelineEnds);
            Assert.AreEqual(completedBefore, actual.CompletedBefore);
            Assert.AreEqual(completedAfter, actual.CompletedAfter);
        }
    }
}
