using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

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
                var owner = manager.GetComponentData<TimelineKindBakeOwner>(entity).Name.ToString();
                using var kinds = manager.GetBuffer<TimelineKindBake>(entity).ToNativeArray(Allocator.Temp);
                foreach (var reached in kinds)
                {
                    var descriptor = registry[reached.Kind];
                    manager.SetComponentEnabled(entity, ComponentType.ReadWrite(descriptor.Active), false);
                    foreach (var column in descriptor.Columns)
                        if (!manager.HasComponent(entity, ComponentType.ReadWrite(column)))
                            Debug.LogError("Tl baking: GameObject '" + owner + "' gets kind '" + descriptor.Name + "' from " + reached.Source + ", fed by " + Feeders(descriptor.Signature) + ", but has no '" + column.Name + "' component.");
                }
            }
        }

        static string Feeders(string signature)
        {
            if (string.IsNullOrEmpty(signature))
                return "game code";
            var names = new List<string>();
            foreach (var line in signature.Split('\n'))
            {
                var end = line.LastIndexOf(':');
                var method = end < 0 ? line : line.Substring(0, end);
                var dot = method.LastIndexOf('.');
                var type = dot < 0 ? method : method.Substring(0, dot);
                var shortType = type.Substring(type.LastIndexOf('.') + 1);
                names.Add(shortType + (dot < 0 ? string.Empty : method.Substring(dot)));
            }
            return string.Join(", ", names);
        }
    }
}
