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
        public void GeneratedTimelineRunsInsideBurstEcsJob()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled);
            using (var world = new World("Tl Burst gate"))
            {
                var manager = world.EntityManager;
                var entity = manager.CreateEntity(typeof(GatePlayback), typeof(CurrentPose), typeof(NextPose), typeof(TargetHealth));
                manager.SetComponentData(entity, new GatePlayback { Tick = 10 });
                manager.SetComponentData(entity, new CurrentPose { Value = new Pose { X = 3, Y = 4 } });
                manager.SetComponentData(entity, new TargetHealth { Value = new Health { Value = 100 } });
                world.CreateSystem<AdvanceGateSystem>().Update(world.Unmanaged);
                Assert.AreEqual(10u, manager.GetComponentData<GatePlayback>(entity).Value.Tick);
                Assert.AreEqual(11u, manager.GetComponentData<GatePlayback>(entity).Tick);
                Assert.AreEqual(5f, manager.GetComponentData<NextPose>(entity).Value.X);
                Assert.AreEqual(5f, manager.GetComponentData<NextPose>(entity).Value.Y);
                Assert.AreEqual(90f, manager.GetComponentData<TargetHealth>(entity).Value.Value);
            }
        }

        [Test]
        public void ScalarForwardBackwardAndFailureReceiptsAreExact()
        {
            var current = new Pose { X = 3, Y = 4 };
            var nextPose = default(Pose);
            var health = new Health { Value = 100 };
            var input = new Gate.Input(in current);
            var output = new Gate.Output(ref nextPose, ref health);
            var playback = Timeline.Start(Gate.Id);
            Assert.IsTrue(Timeline.All[Gate.Id].TryForward(in playback, 10, in input, ref output, out playback));
            Assert.AreEqual(5f, nextPose.X);
            Assert.AreEqual(5f, nextPose.Y);
            Assert.AreEqual(90f, health.Value);
            Assert.IsTrue(Timeline.TryBackward(Gate.Id, in playback, 10, in input, ref output, out playback));
            Assert.AreEqual(1f, nextPose.X);
            Assert.AreEqual(3f, nextPose.Y);
            Assert.AreEqual(100f, health.Value);

            var before = playback;
            var beforePose = nextPose;
            var beforeHealth = health;
            Assert.IsFalse(Timeline.TryForward(8, in playback, 10, in input, ref output, out playback));
            Assert.AreEqual(before, playback);
            Assert.AreEqual(beforePose.X, nextPose.X);
            Assert.AreEqual(beforePose.Y, nextPose.Y);
            Assert.AreEqual(beforeHealth.Value, health.Value);
        }

        [Test]
        public void WarmScalarPlaybackAllocatesZeroManagedBytes()
        {
            var current = new Pose { X = 3, Y = 4 };
            var nextPose = default(Pose);
            var health = new Health { Value = 100 };
            var input = new Gate.Input(in current);
            var output = new Gate.Output(ref nextPose, ref health);
            var playback = Timeline.Start(Gate.Id);
            for (var i = 0; i < 64; i++)
                Assert.IsTrue(Timeline.TryForward(Gate.Id, in playback, (uint)i & 31, in input, ref output, out playback));
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1048576; i++)
            {
                Playback next;
                if (!Timeline.TryForward(Gate.Id, in playback, (uint)i & 31, in input, ref output, out next))
                    throw new InvalidOperationException();
                playback = next;
            }
            Assert.AreEqual(before, GC.GetAllocatedBytesForCurrentThread());
            GC.KeepAlive(nextPose.X + health.Value + playback.Tick);
        }

        [Test]
        public void RuntimeContextsAndBlobAreUnmanaged()
        {
            Assert.IsTrue(UnsafeUtility.IsUnmanaged<Playback>());
            Assert.IsTrue(UnsafeUtility.IsUnmanaged<Gate.Input>());
            Assert.IsTrue(UnsafeUtility.IsUnmanaged<Gate.Output>());
            Assert.AreEqual(12, UnsafeUtility.SizeOf<Playback>());
            using (var blob = Gate.CreateBlob(Allocator.TempJob))
            {
                ref var root = ref blob.Value;
                Assert.AreEqual(1, root.PlanVersion);
                Assert.AreEqual(Gate.Id, root.TimelineId);
                Assert.AreEqual(Gate.Duration, root.Duration);
                Assert.AreEqual(3, root.Regions.Length);
                Assert.AreEqual(4, root.Frames.Length);
                Assert.AreEqual(2, root.StaticData.Length);
            }
        }
    }
}
