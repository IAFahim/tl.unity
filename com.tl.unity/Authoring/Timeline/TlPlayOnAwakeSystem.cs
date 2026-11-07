using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    [BurstCompile]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(TlTimelineRegisterSystem))]
    public partial struct TlPlayOnAwakeSystem : ISystem
    {
        EntityQuery _ready;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _ready = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<TlPlayOnAwake, TlTimelineSet, TimelineRequest>()
                .WithDisabled<TlUnregistered>()
                .Build(ref state);
            state.RequireForUpdate(_ready);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (_ready.IsEmpty)
                return;
            var manager = state.EntityManager;
            var entities = _ready.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var start = manager.GetComponentData<TlPlayOnAwake>(entity);
                var set = manager.GetBuffer<TlTimelineSet>(entity, true);
                var id = start.Slot >= 0 && start.Slot < set.Length ? set[start.Slot].Id : (ushort)0;
                if (id != 0)
                    manager.GetBuffer<TimelineRequest>(entity).Add(TimelineRequest.Start(0, id, start.At));
                manager.SetComponentEnabled<TlPlayOnAwake>(entity, false);
            }
        }
    }
}
