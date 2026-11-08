using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity.Editor
{
#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public static class TimelineKindSets
    {
        static KindRegistry s_Registry;

        public static KindRegistry Registry => s_Registry ??= KindRegistry.Discover();
    }

    [TemporaryBakingType]
    public struct TimelineKindBake : IBufferElementData
    {
        public int Kind;
        public FixedString128Bytes Source;
    }

    [TemporaryBakingType]
    public struct TimelineKindBakeOwner : IComponentData
    {
        public FixedString128Bytes Name;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial struct TimelineKindBakingSystem : ISystem
    {
        EntityQuery _players;

        public void OnCreate(ref SystemState state)
        {
            _players = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<TimelineKindBake, TimelineKindBakeOwner>()
                .WithOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities)
                .Build(ref state);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_players.IsEmpty)
                return;
            var manager = state.EntityManager;
            var registry = TimelineKindSets.Registry;
            using var entities = _players.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                using var kinds = manager.GetBuffer<TimelineKindBake>(entity).ToNativeArray(Allocator.Temp);
                foreach (var reached in kinds)
                {
                    var descriptor = registry[reached.Kind];
                    manager.SetComponentEnabled(entity, ComponentType.ReadWrite(descriptor.Active), false);
                    foreach (var column in descriptor.Columns)
                        if (!manager.HasComponent(entity, ComponentType.ReadWrite(column)))
                            manager.AddComponent(entity, ComponentType.ReadWrite(column));
                }
            }
        }
    }
}
