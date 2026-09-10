using NUnit.Framework;
using Tl;
using GeneratedJobs = Tl.Samples.GeneratedJobs;
using Unity.Burst;
using Unity.Entities;

namespace Tl.Unity.Tests
{
    public sealed class RuntimeTests
    {
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
    }
}
