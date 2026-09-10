using System;
using NUnit.Framework;
using Tl;
using BurstCombat = Tl.Samples.BurstCombat;
using GeneratedJobs = Tl.Samples.GeneratedJobs;
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
        public void ExternalBurstCombatSampleRunsNonzeroSignedSeek()
        {
            using (var world = new World("Tl sample gate"))
            {
                var manager = world.EntityManager;
                var entity = manager.CreateEntity(
                    typeof(BurstCombat.AttackPlayback),
                    typeof(BurstCombat.CurrentPose),
                    typeof(BurstCombat.NextPose),
                    typeof(BurstCombat.FighterCombat));
                manager.SetComponentData(entity, new BurstCombat.AttackPlayback { StartGameTick = 25, Delta = 11 });
                manager.SetComponentData(entity, new BurstCombat.CurrentPose
                {
                    Value = new BurstCombat.FighterPose { X = 3, Y = 4 }
                });
                manager.SetComponentData(entity, new BurstCombat.FighterCombat
                {
                    Value = new BurstCombat.CombatStats { Health = 100 }
                });
                var system = world.CreateSystem<BurstCombat.AdvanceAttackSystem>();

                system.Update(world.Unmanaged);
                manager.CompleteAllTrackedJobs();
                var playback = manager.GetComponentData<BurstCombat.AttackPlayback>(entity);
                var pose = manager.GetComponentData<BurstCombat.NextPose>(entity);
                var combat = manager.GetComponentData<BurstCombat.FighterCombat>(entity);
                Assert.AreEqual(11L, playback.Value.Position);
                Assert.AreEqual(36u, playback.Value.GameTick);
                Assert.AreEqual(5f, pose.Value.X);
                Assert.AreEqual(5f, pose.Value.Y);
                Assert.AreEqual(90f, combat.Value.Health);

                playback.Delta = -1;
                manager.SetComponentData(entity, playback);
                system.Update(world.Unmanaged);
                manager.CompleteAllTrackedJobs();
                playback = manager.GetComponentData<BurstCombat.AttackPlayback>(entity);
                pose = manager.GetComponentData<BurstCombat.NextPose>(entity);
                combat = manager.GetComponentData<BurstCombat.FighterCombat>(entity);
                Assert.AreEqual(10L, playback.Value.Position);
                Assert.AreEqual(35u, playback.Value.GameTick);
                Assert.AreEqual(1f, pose.Value.X);
                Assert.AreEqual(3f, pose.Value.Y);
                Assert.AreEqual(100f, combat.Value.Health);
                Assert.AreEqual(8293u, BurstCombat.Attack.Report.GeneratedSourceBytes);
            }
        }

        [Test]
        public void MaterializedJobsShareSelectionStagesAndCommit()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled);
            using (var world = new World("Tl generated jobs gate"))
            {
                var manager = world.EntityManager;
                var clock = manager.CreateEntity(typeof(GeneratedJobs.Clock));
                manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = 10u, Delta = 2 });
                var entity = CreateGeneratedEntity(manager, new GeneratedJobs.Combat.State(GeneratedJobs.Combat.Asset.Attack));
                var completed = CreateGeneratedEntity(manager, new GeneratedJobs.Combat.State(GeneratedJobs.Combat.Asset.Attack, 2u));
                var incomplete = manager.CreateEntity(
                    typeof(GeneratedJobs.Combat.TimelineComponent),
                    typeof(GeneratedJobs.Combat.Rows),
                    typeof(GeneratedJobs.Combat.Stage0),
                    typeof(GeneratedJobs.Bias));
                manager.SetComponentData(incomplete, new GeneratedJobs.Combat.TimelineComponent
                {
                    Value = new GeneratedJobs.Combat.State(GeneratedJobs.Combat.Asset.Attack)
                });
                manager.SetComponentData(incomplete, new GeneratedJobs.Bias { Value = 1 });
                manager.SetComponentEnabled<GeneratedJobs.Combat.Stage0>(incomplete, false);

                Assert.IsTrue(manager.IsComponentEnabled<GeneratedJobs.Combat.Rows>(entity));
                Assert.IsFalse(manager.IsComponentEnabled<GeneratedJobs.Combat.Stage0>(entity));

                var system = world.CreateSystem<GeneratedJobs.GeneratedTimelineSystem>();
                system.Update(world.Unmanaged);
                manager.CompleteAllTrackedJobs();

                var timeline = manager.GetComponentData<GeneratedJobs.Combat.TimelineComponent>(entity).Value;
                var total = manager.GetComponentData<GeneratedJobs.Total>(entity);
                Assert.AreEqual(2u, timeline.Position);
                Assert.AreEqual(12, total.Value);
                Assert.AreEqual(2, total.Calls);
                Assert.AreEqual(11u, total.LastGameTick);
                Assert.IsTrue((total.Flags & (FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.ClipStart | FrameFlags.ClipEnd | FrameFlags.CompletedAfter))
                    == (FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.ClipStart | FrameFlags.ClipEnd | FrameFlags.CompletedAfter));
                Assert.AreEqual(2u, manager.GetComponentData<GeneratedJobs.Combat.TimelineComponent>(completed).Value.Position);
                Assert.AreEqual(0, manager.GetComponentData<GeneratedJobs.Total>(completed).Calls);
                Assert.AreEqual(0u, manager.GetComponentData<GeneratedJobs.Combat.TimelineComponent>(incomplete).Value.Position);

                manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = 12u, Delta = -2 });
                system.Update(world.Unmanaged);
                manager.CompleteAllTrackedJobs();

                timeline = manager.GetComponentData<GeneratedJobs.Combat.TimelineComponent>(entity).Value;
                total = manager.GetComponentData<GeneratedJobs.Total>(entity);
                Assert.AreEqual(0u, timeline.Position);
                Assert.AreEqual(0, total.Value);
                Assert.AreEqual(4, total.Calls);
                Assert.AreEqual(10u, total.LastGameTick);
                Assert.IsTrue((total.Flags & FrameFlags.Reverse) != 0);
            }
        }

        private static Entity CreateGeneratedEntity(EntityManager manager, GeneratedJobs.Combat.State state)
        {
            var entity = manager.CreateEntity(
                typeof(GeneratedJobs.Combat.TimelineComponent),
                typeof(GeneratedJobs.Combat.Rows),
                typeof(GeneratedJobs.Combat.Stage0),
                typeof(GeneratedJobs.Bias),
                typeof(GeneratedJobs.Total));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.TimelineComponent { Value = state });
            manager.SetComponentData(entity, new GeneratedJobs.Bias { Value = 1 });
            manager.SetComponentData(entity, new GeneratedJobs.Total());
            manager.SetComponentEnabled<GeneratedJobs.Combat.Stage0>(entity, false);
            return entity;
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

            playback = Timeline.CreateTypedPlayback<LoopGate>(long.MaxValue, 11u, PlaybackFlags.Started);
            data = new LoopGate.Data(ref playback, ref receipt);
            var before = receipt;
            Assert.IsFalse(LoopGate.TrySeek(ref data, 1));
            Assert.AreEqual(long.MaxValue, playback.Position);
            Assert.AreEqual(before.Calls, receipt.Calls);

            playback = Timeline.CreateTypedPlayback<LoopGate>(long.MinValue, 11u, PlaybackFlags.Started);
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
        public void CheckedCompilationPreservesExplicitGameTickWrap()
        {
            Assert.Throws<OverflowException>(() => AmbientCheckedIncrement(uint.MaxValue));
            var pose = default(Pose);
            var health = new Health { Value = 100 };
            var receipt = default(Receipt);
            var playback = Gate.Start(uint.MaxValue);
            var data = new Gate.Data(ref playback, in pose, ref pose, ref health, ref receipt);

            Assert.IsTrue(Gate.TrySeek(ref data, 2));
            Assert.AreEqual(1u, playback.GameTick);
            Assert.IsTrue(Gate.TrySeek(ref data, -2));
            Assert.AreEqual(uint.MaxValue, playback.GameTick);
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
            Assert.IsTrue(UnsafeUtility.IsUnmanaged<Playback<Gate>>());
            Assert.IsTrue(typeof(Gate.Data).IsByRefLike);
            Assert.IsTrue(typeof(LoopGate.Data).IsByRefLike);
            Assert.AreEqual(16, UnsafeUtility.SizeOf<Playback<Gate>>());
            Assert.AreEqual(16, UnsafeUtility.SizeOf<Playback<LoopGate>>());
            Assert.AreNotEqual(typeof(Playback<Gate>), typeof(Playback<LoopGate>));
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
            ref Playback<Gate> playback,
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

        private static uint AmbientCheckedIncrement(uint value)
        {
            return value + 1u;
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
