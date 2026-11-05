using System;
using Tl.Unity;
using Unity.Collections;
using Unity.Entities;

namespace KindGame
{
    public partial class MovementSystem : SystemBase
    {
        public bool Forward = true;

        protected override void OnUpdate()
        {
            var view = GetSingleton<TimelineLibrary>().View;
            Dependency = TimelineJobs.Movement(ref CheckedStateRef, view, TimelineLibraryBootstrap.Kinds, Forward, Dependency);
        }
    }

    public partial class VitalsSystem : SystemBase
    {
        public bool Forward = true;

        protected override void OnUpdate()
        {
            var view = GetSingleton<TimelineLibrary>().View;
            Dependency = TimelineJobs.Vitals(ref CheckedStateRef, view, TimelineLibraryBootstrap.Kinds, Forward, Dependency);
        }
    }

    public sealed class KindWorld : IDisposable
    {
        public const ushort Jump = 1, PotionHop = 2, Run = 3, RunLegacy = 4, SpeedBoost = 5, Stomp = 6;

        public readonly World World;
        public readonly EntityManager Entities;
        public readonly TimelineKindTickSystem Tick;
        public readonly MovementSystem Movement;
        public readonly VitalsSystem Vitals;
        public readonly Entity[] Rows;

        public KindWorld(int count)
        {
            TlGeneratedBootstrap.InstallDiscoveredModules();
            World = new World("TlKinds");
            Entities = World.EntityManager;
            TimelineLibraryBootstrap.Ensure(Entities, "kinds.tlib", KindLibraryBytes.Bytes());
            Tick = World.CreateSystemManaged<TimelineKindTickSystem>();
            Movement = World.CreateSystemManaged<MovementSystem>();
            Vitals = World.CreateSystemManaged<VitalsSystem>();
            Rows = new Entity[count];
            for (var i = 0; i < count; i++)
                Rows[i] = Spawn();
        }

        Entity Spawn()
        {
            var types = new System.Collections.Generic.List<ComponentType>
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
            Entities.SetComponentData(entity, new Movement_JumpPowerData { Value = new JumpPower { Value = 2f } });
            Entities.SetComponentData(entity, new Vitals_HealthData { Value = new Health { Value = 1000 } });
            return entity;
        }

        public void Start(int row, ushort timeline, ushort at = 0)
        {
            Entities.GetBuffer<TimelineRequest>(Rows[row]).Add(TimelineRequest.Start(0, timeline, at));
        }

        public void Stop(int row, ushort timeline)
        {
            Entities.GetBuffer<TimelineRequest>(Rows[row]).Add(TimelineRequest.Stop(0, timeline));
        }

        public void StopChannel(int row, byte channel)
        {
            Entities.GetBuffer<TimelineRequest>(Rows[row]).Add(TimelineRequest.StopChannel(0, channel));
        }

        public void StopAll(int row)
        {
            Entities.GetBuffer<TimelineRequest>(Rows[row]).Add(TimelineRequest.StopAll(0));
        }

        public void Step(bool forward = true)
        {
            Tick.Forward = forward;
            Movement.Forward = forward;
            Vitals.Forward = forward;
            Tick.Update();
            Movement.Update();
            Vitals.Update();
        }

        public void Damage(int target, int amount)
        {
            var sender = Tick.BeginPulses(1);
            var writer = sender.Writer;
            writer.Begin(0);
            DamageKind.Pulses(writer).To(Rows[target], amount);
            writer.End();
        }

        public void Stat(int target, int delta)
        {
            var sender = Tick.BeginPulses(1);
            var writer = sender.Writer;
            writer.Begin(0);
            StatChangeKind.Pulses(writer).To(Rows[target], delta);
            writer.End();
        }

        public Motion Motion(int row) => Entities.GetComponentData<Movement_MotionData>(Rows[row]).Value;

        public int Stamina(int row) => Entities.GetComponentData<Movement_StaminaData>(Rows[row]).Value.Value;

        public int Health(int row) => Entities.GetComponentData<Vitals_HealthData>(Rows[row]).Value.Value;

        public void Dispose()
        {
            Entities.CompleteAllTrackedJobs();
            TimelineLibraryBootstrap.Shutdown(Entities);
            if (World.IsCreated)
                World.Dispose();
        }
    }
}
