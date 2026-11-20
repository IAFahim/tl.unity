using System;
using System.Collections.Generic;
using Tl.Unity;
using Unity.Burst;
using Unity.Entities;

namespace KindPure
{
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public partial struct PureMovementSystem : ISystem
    {
        TimelineJobs.Movement _jobs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _jobs = TimelineJobs.Movement.Create(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }

    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public partial struct PureVitalsSystem : ISystem
    {
        TimelineJobs.Vitals _jobs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _jobs = TimelineJobs.Vitals.Create(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }

    public sealed unsafe class PureWorld : IDisposable
    {
        public readonly World World;
        public readonly EntityManager Entities;
        public readonly SystemHandle TickHandle;
        public readonly SystemHandle MovementHandle;
        public readonly SystemHandle VitalsHandle;
        public readonly Entity[] Rows;
        readonly TlLibrary _library;

        public PureWorld(int count, byte[] library)
        {
            World = new World("TlKindsPure");
            Entities = World.EntityManager;
            _library = TlLibrary.FromBytes(library);
            _library.Publish(Entities);
            TickHandle = World.CreateSystem<TimelineKindTickSystem>();
            MovementHandle = World.CreateSystem<PureMovementSystem>();
            VitalsHandle = World.CreateSystem<PureVitalsSystem>();
            Rows = new Entity[count];
            for (var i = 0; i < count; i++)
                Rows[i] = Spawn();
        }

        Entity Spawn()
        {
            var types = new List<ComponentType>
            {
                typeof(Playback),
                typeof(TimelineRequest),
                typeof(TimelinePlaying),
                typeof(Movement_MotionData),
                typeof(Movement_WalkStatData),
                typeof(Movement_JumpPowerData),
                typeof(Movement_StaminaData),
                typeof(Vitals_HealthData),
            };
            types.AddRange(LocomotionKind.Types);
            types.AddRange(LiftKind.Types);
            types.AddRange(StatChangeKind.Types);
            types.AddRange(DamageKind.Types);
            var entity = Entities.CreateEntity(types.ToArray());
            Entities.SetComponentEnabled<TimelinePlaying>(entity, false);
            LocomotionKind.Init(Entities, entity);
            LiftKind.Init(Entities, entity);
            StatChangeKind.Init(Entities, entity);
            DamageKind.Init(Entities, entity);
            Entities.SetComponentData(entity, new Movement_WalkStatData { Value = new WalkStat { Value = 1f } });
            Entities.SetComponentData(entity, new Movement_JumpPowerData { Value = new JumpPower { Value = 1f } });
            Entities.SetComponentData(entity, new Vitals_HealthData { Value = new Health { Value = 1_000_000 } });
            return entity;
        }

        public void Start(int row, ushort timeline, ushort at)
        {
            Entities.GetBuffer<TimelineRequest>(Rows[row]).Add(TimelineRequest.Start(0, timeline, at));
        }

        public void Step()
        {
            TickHandle.Update(World.Unmanaged);
            MovementHandle.Update(World.Unmanaged);
            VitalsHandle.Update(World.Unmanaged);
        }

        public void StepEachSystemCompleted(double[] ticksPerSystem)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            TickHandle.Update(World.Unmanaged);
            Entities.CompleteAllTrackedJobs();
            var afterTick = System.Diagnostics.Stopwatch.GetTimestamp();
            MovementHandle.Update(World.Unmanaged);
            Entities.CompleteAllTrackedJobs();
            var afterMovement = System.Diagnostics.Stopwatch.GetTimestamp();
            VitalsHandle.Update(World.Unmanaged);
            Entities.CompleteAllTrackedJobs();
            var afterVitals = System.Diagnostics.Stopwatch.GetTimestamp();
            ticksPerSystem[0] = afterTick - start;
            ticksPerSystem[1] = afterMovement - afterTick;
            ticksPerSystem[2] = afterVitals - afterMovement;
        }

        public bool Precombined(string kind)
        {
            Entities.CompleteAllTrackedJobs();
            using var query = Entities.CreateEntityQuery(typeof(TimelineLibrary));
            var index = query.GetSingleton<TimelineLibrary>().Kinds;
            var id = index.KindOf(KindIndex.IdOf(kind));
            return id >= 0 && index.Values[id].Data != null;
        }

        public ulong Hash()
        {
            Entities.CompleteAllTrackedJobs();
            var h = global::Tl.TlStateHash.Begin();
            foreach (var row in Rows)
            {
                var motion = Entities.GetComponentData<Movement_MotionData>(row).Value;
                h.Float(motion.X);
                h.Float(motion.Y);
                h.Int(Entities.GetComponentData<Movement_StaminaData>(row).Value.Value);
                h.Int(Entities.GetComponentData<Vitals_HealthData>(row).Value.Value);
                var slots = Entities.GetBuffer<Playback>(row, true);
                h.Slots((global::Tl.Playback*)slots.GetUnsafeReadOnlyPtr(), slots.Length);
            }
            return h.Value;
        }

        public void Dispose()
        {
            Entities.CompleteAllTrackedJobs();
            if (World.IsCreated)
                World.Dispose();
            _library?.Dispose();
        }
    }
}
