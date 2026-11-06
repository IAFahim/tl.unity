using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Tl.Unity.Editor
{
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

    public sealed class TimelinePlayerBaker : Baker<TimelinePlayerAuthoring>
    {
        public override void Bake(TimelinePlayerAuthoring authoring)
        {
            DependsOn(authoring.Library);
            DependsOn(authoring.Profile);
            if (authoring.Library != null)
                DependsOn(UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(TimelineKindSets.LibraryFile(authoring.Library)));
            var entity = GetEntity(TransformUsageFlags.None);
            AddBuffer<Playback>(entity);
            var requests = AddBuffer<TimelineRequest>(entity);
            var types = new List<Type>();
            foreach (var component in GetComponents<MonoBehaviour>())
                if (component != null)
                    types.Add(component.GetType());
            var registry = TimelineKindSets.Registry;
            KindSetPlan plan;
            try
            {
                plan = TimelineKindSets.Plan(authoring, types, registry);
            }
            catch (Exception error)
            {
                Debug.LogError("Tl baking: GameObject '" + authoring.name + "': " + error.Message, authoring);
                return;
            }
            foreach (var error in plan.Errors)
                Debug.LogError("Tl baking: GameObject '" + authoring.name + "' " + error, authoring);
            foreach (var warning in plan.Warnings)
                Debug.LogWarning("Tl baking: GameObject '" + authoring.name + "' " + warning, authoring);
            foreach (var start in plan.Starts)
                requests.Add(TimelineRequest.Start(0, start));
            if (!plan.LibraryHasKinds)
                return;
            AddComponent<TimelinePlaying>(entity);
            SetComponentEnabled<TimelinePlaying>(entity, false);
            AddComponent(entity, new TimelineKindBakeOwner { Name = Fit(authoring.name) });
            var reached = AddBuffer<TimelineKindBake>(entity);
            foreach (var kind in plan.Kinds.Values)
            {
                var descriptor = registry[kind.Kind];
                AddComponent(entity, new ComponentTypeSet(ComponentType.ReadWrite(descriptor.Entry), ComponentType.ReadWrite(descriptor.Active), ComponentType.ReadWrite(descriptor.Enabled)));
                reached.Add(new TimelineKindBake { Kind = kind.Kind, Source = Fit(kind.Source) });
            }
        }

        static FixedString128Bytes Fit(string text)
        {
            var fixedText = new FixedString128Bytes();
            fixedText.CopyFromTruncated(text ?? string.Empty);
            return fixedText;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial class TimelineKindBakingSystem : SystemBase
    {
        EntityQuery _players;

        protected override void OnCreate()
        {
            _players = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TimelineKindBake>(), ComponentType.ReadOnly<TimelineKindBakeOwner>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
            });
        }

        protected override void OnUpdate()
        {
            var registry = TimelineKindSets.Registry;
            using var entities = _players.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                var owner = EntityManager.GetComponentData<TimelineKindBakeOwner>(entity).Name.ToString();
                using var kinds = EntityManager.GetBuffer<TimelineKindBake>(entity).ToNativeArray(Allocator.Temp);
                foreach (var reached in kinds)
                {
                    var descriptor = registry[reached.Kind];
                    EntityManager.SetComponentEnabled(entity, ComponentType.ReadWrite(descriptor.Active), false);
                    foreach (var column in descriptor.Columns)
                        if (!EntityManager.HasComponent(entity, ComponentType.ReadWrite(column)))
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
