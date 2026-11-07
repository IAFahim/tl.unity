using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Demo
{
    public struct DemoClock : IComponentData
    {
        public int Step;
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateBefore(typeof(TimelineKindTickSystem))]
    public partial struct DemoDirectorSystem : ISystem
    {
        EntityQuery _runners;
        EntityQuery _damageable;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineKindTick>();
            state.RequireForUpdate<TimelineStep>();
            _runners = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TimelineRequest>().WithAll<LocomotionEntry>().Build(ref state);
            _damageable = new EntityQueryBuilder(Allocator.Temp).WithAll<DamageEntry>().Build(ref state);
            state.EntityManager.CreateSingleton(new DemoClock());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var step = SystemAPI.GetSingleton<DemoClock>().Step;
            if (step == 3)
                Start(ref state, Timelines.Dash);
            if (step == 6)
                Start(ref state, Timelines.Stomp);
            if (step == 12)
                Hurt(ref state, 7);
            if (step == 40)
                SystemAPI.SetSingleton(new TimelineStep { Forward = false });
            if (step == 50)
                SystemAPI.SetSingleton(new TimelineStep { Forward = true });
            SystemAPI.GetSingletonRW<DemoClock>().ValueRW.Step = step + 1;
        }

        void Start(ref SystemState state, ushort timeline)
        {
            var runners = _runners.ToEntityArray(Allocator.Temp);
            foreach (var runner in runners)
                SystemAPI.GetBuffer<TimelineRequest>(runner).Add(TimelineRequest.Start(0, timeline));
        }

        void Hurt(ref SystemState state, int amount)
        {
            var sender = SystemAPI.GetSingleton<TimelineKindTick>().BeginPulses(1);
            var writer = sender.Writer;
            writer.Begin(0);
            var targets = _damageable.ToEntityArray(Allocator.Temp);
            foreach (var target in targets)
                DamageKind.Pulses(writer).To(target, amount);
            writer.End();
        }
    }
}
