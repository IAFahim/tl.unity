using System;
using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace KindGame
{
    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public partial struct MovementSystem : ISystem
    {
        ComponentLookup<Anchor> _anchors;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _anchors = state.GetComponentLookup<Anchor>(true);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            _anchors.Update(ref state);
            var anchors = new AnchorLookup { Anchors = _anchors };
            state.Dependency = TimelineJobs.Movement(ref state, library.View, library.Kinds, step.Forward, anchors, state.Dependency);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public partial struct VitalsSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            state.Dependency = TimelineJobs.Vitals(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }

    public sealed class KindWorld : IDisposable
    {
        public const ushort Jump = 1, PotionHop = 2, Run = 3, RunLegacy = 4, SpeedBoost = 5, Stomp = 6, Vault = 7, Waltz = 8;

        public readonly World World;
        public readonly EntityManager Entities;
        public readonly SystemHandle LibraryHandle;
        public readonly SystemHandle TickHandle;
        public readonly SystemHandle MovementHandle;
        public readonly SystemHandle VitalsHandle;
        public readonly Entity[] Rows;
        readonly EntityQuery _step;
        readonly bool _events;
        bool _forward = true;

        public KindWorld(int count, bool events = false)
        {
            _events = events;
            World = new World("TlKinds");
            Entities = World.EntityManager;
            TimelineLibraryBootstrap.Use("kinds.tlib", KindLibraryBytes.Bytes());
            LibraryHandle = World.CreateSystem<TimelineLibrarySystem>();
            LibraryHandle.Update(World.Unmanaged);
            TickHandle = World.CreateSystem<TimelineKindTickSystem>();
            MovementHandle = World.CreateSystem<MovementSystem>();
            VitalsHandle = World.CreateSystem<VitalsSystem>();
            _step = Entities.CreateEntityQuery(typeof(TimelineStep));
            Rows = new Entity[count];
            for (var i = 0; i < count; i++)
                Rows[i] = Spawn();
        }

        public TimelineKindTick Tick => Entities.GetComponentData<TimelineKindTick>(TickHandle);

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
                typeof(Movement_TargetData),
                typeof(Movement_StaminaData),
                typeof(Vitals_HealthData),
            };
            types.AddRange(LocomotionKind.Types);
            types.AddRange(LiftKind.Types);
            types.AddRange(StatChangeKind.Types);
            types.AddRange(DamageKind.Types);
            if (_events)
                types.Add(typeof(TimelineEvent<Cue>));
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
            if (forward != _forward)
            {
                _step.SetSingleton(new TimelineStep { Forward = forward });
                _forward = forward;
            }
            TickHandle.Update(World.Unmanaged);
            MovementHandle.Update(World.Unmanaged);
            VitalsHandle.Update(World.Unmanaged);
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

        public Playback[] Slots(int row)
        {
            var buffer = Entities.GetBuffer<Playback>(Rows[row]);
            var slots = new Playback[buffer.Length];
            for (var i = 0; i < slots.Length; i++)
                slots[i] = buffer[i];
            return slots;
        }

        public void Edit(int row, int slot, Func<Playback, Playback> change)
        {
            var buffer = Entities.GetBuffer<Playback>(Rows[row]);
            buffer[slot] = change(buffer[slot]);
        }

        public int Entries<T>(int row) where T : unmanaged, IBufferElementData
        {
            return Entities.GetBuffer<T>(Rows[row]).Length;
        }

        public bool Active<T>(int row) where T : unmanaged, IComponentData, IEnableableComponent
        {
            return Entities.IsComponentEnabled<T>(Rows[row]);
        }

        public DynamicBuffer<TimelineEvent<Cue>> Cues(int row)
        {
            Entities.CompleteAllTrackedJobs();
            return Entities.GetBuffer<TimelineEvent<Cue>>(Rows[row]);
        }

        public Motion Motion(int row) => Entities.GetComponentData<Movement_MotionData>(Rows[row]).Value;

        public int Stamina(int row) => Entities.GetComponentData<Movement_StaminaData>(Rows[row]).Value.Value;

        public int Health(int row) => Entities.GetComponentData<Vitals_HealthData>(Rows[row]).Value.Value;

        public void Dispose()
        {
            Entities.CompleteAllTrackedJobs();
            TimelineLibraryBootstrap.Release();
            if (World.IsCreated)
                World.Dispose();
        }
    }
}
