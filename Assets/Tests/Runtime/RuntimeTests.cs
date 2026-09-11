using System;
using System.Diagnostics;
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
        public void MaterializedJobsPreserveMixedAssetOrderBlendsGapsAndBounds()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled);
            using (var world = new World("Tl generated jobs order gate"))
            {
                var manager = world.EntityManager;
                var clock = CreateClock(manager, 100u, 1);
                var attack = CreateRowsEntity(manager, GeneratedJobs.Combat.Asset.Attack);
                var defense = CreateRowsEntity(manager, GeneratedJobs.Combat.Asset.Defense);
                var gap = CreateGapEntity(manager);
                var complete = CreateRowsEntity(manager, GeneratedJobs.Combat.Asset.Attack, 2u);
                var empty = CreateRowsEntity(manager, GeneratedJobs.Combat.Asset.None);
                var incomplete = CreateIncompleteRowsEntity(manager);
                var disabled = CreateRowsEntity(manager, GeneratedJobs.Combat.Asset.Attack);
                manager.SetComponentEnabled<GeneratedJobs.Combat.Rows>(disabled, false);

                var system = world.CreateSystem<GeneratedJobs.GeneratedTimelineSystem>();
                Update(system, world, manager);

                AssertState(manager, attack, 1u, 0L);
                AssertTrace(manager, attack, 83958L, 22L, 5, 100u);
                AssertState(manager, defense, 1u, 0L);
                AssertTrace(manager, defense, 8938L, 13L, 4, 100u);
                AssertState(manager, gap, 1u, 0L);
                AssertTrace(manager, gap, 0L, 0L, 0, 0u);
                AssertState(manager, complete, 2u, 0L);
                AssertTrace(manager, complete, 0L, 0L, 0, 0u);
                AssertState(manager, empty, 0u, 0L);
                AssertTrace(manager, empty, 0L, 0L, 0, 0u);
                AssertState(manager, incomplete, 0u, 0L);
                AssertState(manager, disabled, 0u, 0L);
                AssertTrace(manager, disabled, 0L, 0L, 0, 0u);

                manager.SetComponentEnabled<GeneratedJobs.Combat.Rows>(disabled, true);
                manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = 101u, Delta = 1 });
                Update(system, world, manager);

                AssertState(manager, attack, 2u, 0L);
                AssertTrace(manager, attack, 8395883958L, 54L, 10, 101u);
                AssertState(manager, defense, 2u, 0L);
                AssertTrace(manager, defense, 89388938L, 26L, 8, 101u);
                AssertState(manager, gap, 2u, 0L);
                AssertTrace(manager, gap, 3L, 9L, 1, 101u);
                AssertState(manager, incomplete, 0u, 0L);
                AssertState(manager, disabled, 1u, 0L);
                AssertTrace(manager, disabled, 83958L, 22L, 5, 101u);

                var forward = Trace(manager, attack);
                Assert.AreEqual(FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedAfter | FrameFlags.ClipStart | FrameFlags.ClipEnd,
                    forward.Flags & (FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedAfter | FrameFlags.ClipStart | FrameFlags.ClipEnd));
                manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = 102u, Delta = 0 });
                Update(system, world, manager);
                AssertState(manager, attack, 2u, 0L);
                AssertTrace(manager, attack, 8395883958L, 54L, 10, 101u);

                SetTrace(manager, attack, new GeneratedJobs.Trace());
                manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = 102u, Delta = -2 });
                Update(system, world, manager);

                AssertState(manager, attack, 0u, 0L);
                AssertTrace(manager, attack, 8593885938L, 54L, 10, 100u);
                var reverse = Trace(manager, attack);
                Assert.AreEqual(FrameFlags.Reverse | FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedBefore | FrameFlags.ClipStart | FrameFlags.ClipEnd,
                    reverse.Flags & (FrameFlags.Reverse | FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedBefore | FrameFlags.ClipStart | FrameFlags.ClipEnd));
            }
        }

        [Test]
        public void MaterializedJobsKeepSameTypeRolesDistinctAndToggleSchemaWithoutStructuralChanges()
        {
            using (var world = new World("Tl generated jobs schema gate"))
            {
                var manager = world.EntityManager;
                var clock = CreateClock(manager, 400u, 1);
                var entity = CreateRowsEntity(manager, GeneratedJobs.Combat.Asset.Attack);
                manager.SetComponentEnabled<GeneratedJobs.Combat.Rows>(entity, false);
                var system = world.CreateSystem<GeneratedJobs.GeneratedTimelineSystem>();

                Update(system, world, manager);
                AssertState(manager, entity, 0u, 0L);
                AssertTrace(manager, entity, 0L, 0L, 0, 0u);
                Assert.AreEqual(2, manager.GetComponentData<GeneratedJobs.Combat.Role0Bias>(entity).Value.Value);
                Assert.AreEqual(7, manager.GetComponentData<GeneratedJobs.Combat.Role2Secondary>(entity).Value.Value);

                manager.SetComponentEnabled<GeneratedJobs.Combat.Rows>(entity, true);
                manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = 401u, Delta = 1 });
                Update(system, world, manager);
                AssertState(manager, entity, 1u, 0L);
                AssertTrace(manager, entity, 83958L, 22L, 5, 401u);
            }
        }

        [Test]
        public void MaterializedJobsPreserveLoopCyclesInBothDirections()
        {
            using (var world = new World("Tl generated jobs loop gate"))
            {
                var manager = world.EntityManager;
                var clock = CreateClock(manager, 300u, 3);
                var entity = CreateLoopEntity(manager);
                var system = world.CreateSystem<GeneratedJobs.GeneratedTimelineSystem>();

                Update(system, world, manager);
                AssertState(manager, entity, 0u, 3L);
                AssertTrace(manager, entity, 333L, 3L, 3, 302u);
                Assert.AreEqual(12L, Trace(manager, entity).CycleOrder);

                SetTrace(manager, entity, new GeneratedJobs.Trace());
                manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = 303u, Delta = -3 });
                Update(system, world, manager);
                AssertState(manager, entity, 0u, 0L);
                AssertTrace(manager, entity, 333L, 3L, 3, 300u);
                var reverse = Trace(manager, entity);
                Assert.AreEqual(210L, reverse.CycleOrder);
                Assert.AreEqual(FrameFlags.Looping | FrameFlags.Reverse | FrameFlags.TimelineStart | FrameFlags.TimelineEnd,
                    reverse.Flags & (FrameFlags.Looping | FrameFlags.Reverse | FrameFlags.TimelineStart | FrameFlags.TimelineEnd));
            }
        }

        [Test]
        public void WarmSchedulingAllocatesNoManagedMemoryAndReportsCost()
        {
            const int warmup = 32;
            const int iterations = 128;
            using (var world = new World("Tl generated jobs allocation gate"))
            {
                var manager = world.EntityManager;
                var clock = CreateClock(manager, 500u, 1);
                var entity = CreateLoopEntity(manager);
                var system = world.CreateSystem<GeneratedJobs.GeneratedTimelineSystem>();
                for (var index = 0; index < warmup; index++)
                {
                    manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = (uint)(500 + index), Delta = 1 });
                    Update(system, world, manager);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var stopwatch = new Stopwatch();
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                stopwatch.Start();
                for (var index = 0; index < iterations; index++)
                {
                    manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = (uint)(500 + warmup + index), Delta = 1 });
                    Update(system, world, manager);
                }
                stopwatch.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                Assert.AreEqual(0L, allocated);
                AssertState(manager, entity, 0u, warmup + iterations);
                TestContext.WriteLine("TL_UNITY_METRICS iterations={0} scheduled_jobs_per_step=20 managed_bytes={1} elapsed_ticks={2} elapsed_ns_per_step={3:F1}",
                    iterations, allocated, stopwatch.ElapsedTicks,
                    stopwatch.ElapsedTicks * (1000000000.0 / Stopwatch.Frequency) / iterations);
            }
        }

        private static Entity CreateClock(EntityManager manager, uint gameTick, int delta)
        {
            var clock = manager.CreateEntity(typeof(GeneratedJobs.Clock));
            manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = gameTick, Delta = delta });
            return clock;
        }

        private static Entity CreateRowsEntity(EntityManager manager, GeneratedJobs.Combat.Asset asset, uint position = 0u)
        {
            var entity = manager.CreateEntity(
                typeof(GeneratedJobs.Combat.TimelineComponent),
                typeof(GeneratedJobs.Combat.Rows),
                typeof(GeneratedJobs.Combat.Role0Bias),
                typeof(GeneratedJobs.Combat.Role1Scale),
                typeof(GeneratedJobs.Combat.Role2Secondary),
                typeof(GeneratedJobs.Combat.Role3Trace));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.TimelineComponent
            {
                Value = new GeneratedJobs.Combat.State(asset, position)
            });
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role0Bias(new GeneratedJobs.Bias { Value = 2 }));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role1Scale(new GeneratedJobs.Scale { Value = 1 }));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role2Secondary(new GeneratedJobs.Bias { Value = 7 }));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role3Trace(new GeneratedJobs.Trace()));
            return entity;
        }

        private static Entity CreateIncompleteRowsEntity(EntityManager manager)
        {
            var entity = manager.CreateEntity(
                typeof(GeneratedJobs.Combat.TimelineComponent),
                typeof(GeneratedJobs.Combat.Rows),
                typeof(GeneratedJobs.Combat.Role0Bias),
                typeof(GeneratedJobs.Combat.Role2Secondary),
                typeof(GeneratedJobs.Combat.Role3Trace));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.TimelineComponent
            {
                Value = new GeneratedJobs.Combat.State(GeneratedJobs.Combat.Asset.Attack)
            });
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role0Bias(new GeneratedJobs.Bias { Value = 2 }));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role2Secondary(new GeneratedJobs.Bias { Value = 7 }));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role3Trace(new GeneratedJobs.Trace()));
            return entity;
        }

        private static Entity CreateGapEntity(EntityManager manager)
        {
            var entity = manager.CreateEntity(
                typeof(GeneratedJobs.Combat.TimelineComponent),
                typeof(GeneratedJobs.Combat.GapRows),
                typeof(GeneratedJobs.Combat.Role0Bias),
                typeof(GeneratedJobs.Combat.Role3Trace));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.TimelineComponent
            {
                Value = new GeneratedJobs.Combat.State(GeneratedJobs.Combat.Asset.Gap)
            });
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role0Bias(new GeneratedJobs.Bias { Value = 2 }));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role3Trace(new GeneratedJobs.Trace()));
            return entity;
        }

        private static Entity CreateLoopEntity(EntityManager manager)
        {
            var entity = manager.CreateEntity(
                typeof(GeneratedJobs.Combat.TimelineComponent),
                typeof(GeneratedJobs.Combat.LoopRows),
                typeof(GeneratedJobs.Combat.Role0Bias),
                typeof(GeneratedJobs.Combat.Role3Trace));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.TimelineComponent
            {
                Value = new GeneratedJobs.Combat.State(GeneratedJobs.Combat.Asset.Loop)
            });
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role0Bias(new GeneratedJobs.Bias { Value = 2 }));
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role3Trace(new GeneratedJobs.Trace()));
            return entity;
        }

        private static void Update(SystemHandle system, World world, EntityManager manager)
        {
            system.Update(world.Unmanaged);
            manager.CompleteAllTrackedJobs();
        }

        private static void AssertState(EntityManager manager, Entity entity, uint position, long cycle)
        {
            var state = manager.GetComponentData<GeneratedJobs.Combat.TimelineComponent>(entity).Value;
            Assert.AreEqual(position, state.Position);
            Assert.AreEqual(cycle, state.Cycle);
        }

        private static GeneratedJobs.Trace Trace(EntityManager manager, Entity entity)
        {
            return manager.GetComponentData<GeneratedJobs.Combat.Role3Trace>(entity).Value;
        }

        private static void SetTrace(EntityManager manager, Entity entity, GeneratedJobs.Trace value)
        {
            manager.SetComponentData(entity, new GeneratedJobs.Combat.Role3Trace(value));
        }

        private static void AssertTrace(EntityManager manager, Entity entity, long order, long clipSum, int calls, uint gameTick)
        {
            var trace = Trace(manager, entity);
            Assert.AreEqual(order, trace.Order);
            Assert.AreEqual(clipSum, trace.ClipSum);
            Assert.AreEqual(calls, trace.Calls);
            Assert.AreEqual(gameTick, trace.LastGameTick);
        }
    }
}
