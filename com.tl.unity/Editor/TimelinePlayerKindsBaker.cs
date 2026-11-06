using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace Tl.Unity.Editor
{
    public static class TimelineKindReachability
    {
        public static HashSet<int> CollectTimelines(
            ICollection<int> startWith,
            IDictionary<int, int[]> slots,
            IDictionary<int, int> next)
        {
            var reachable = new HashSet<int>();
            if (startWith == null)
                return reachable;
            var stack = new Stack<int>(startWith);
            while (stack.Count > 0)
            {
                var timeline = stack.Pop();
                if (!reachable.Add(timeline))
                    continue;
                if (slots != null && slots.TryGetValue(timeline, out var targets) && targets != null)
                    foreach (var target in targets)
                        stack.Push(target);
                if (next != null && next.TryGetValue(timeline, out var follow))
                    stack.Push(follow);
            }
            return reachable;
        }
    }

// SCAFFOLD (tl#557)
    public sealed class TimelinePlayerKindsAuthoring : MonoBehaviour
    {
        [Serializable]
        public struct SlotBinding
        {
            public int Timeline;
            public int Target;
        }

        [Serializable]
        public struct NextBinding
        {
            public int Timeline;
            public int Follow;
        }

        [Serializable]
        public struct FedBinding
        {
            public int Timeline;
            public string[] Kinds;
        }

        public int[] StartWith = Array.Empty<int>();
        public SlotBinding[] Slots = Array.Empty<SlotBinding>();
        public NextBinding[] Next = Array.Empty<NextBinding>();
        public FedBinding[] FedKinds = Array.Empty<FedBinding>();
        public string[] RequiredKinds = Array.Empty<string>();

        public Dictionary<int, int[]> SlotMap()
        {
            var map = new Dictionary<int, List<int>>();
            if (Slots != null)
                for (var i = 0; i < Slots.Length; i++)
                {
                    if (!map.TryGetValue(Slots[i].Timeline, out var targets))
                    {
                        targets = new List<int>();
                        map[Slots[i].Timeline] = targets;
                    }
                    targets.Add(Slots[i].Target);
                }
            var frozen = new Dictionary<int, int[]>();
            foreach (var pair in map)
                frozen[pair.Key] = pair.Value.ToArray();
            return frozen;
        }

        public Dictionary<int, int> NextMap()
        {
            var map = new Dictionary<int, int>();
            if (Next == null)
                return map;
            for (var i = 0; i < Next.Length; i++)
                map[Next[i].Timeline] = Next[i].Follow;
            return map;
        }

        public Dictionary<int, string[]> FedMap()
        {
            var map = new Dictionary<int, List<string>>();
            if (FedKinds != null)
                for (var i = 0; i < FedKinds.Length; i++)
                {
                    if (!map.TryGetValue(FedKinds[i].Timeline, out var kinds))
                    {
                        kinds = new List<string>();
                        map[FedKinds[i].Timeline] = kinds;
                    }
                    if (FedKinds[i].Kinds != null)
                        kinds.AddRange(FedKinds[i].Kinds);
                }
            var frozen = new Dictionary<int, string[]>();
            foreach (var pair in map)
                frozen[pair.Key] = pair.Value.ToArray();
            return frozen;
        }
    }

    public sealed class TimelinePlayerKindsBaker : Baker<TimelinePlayerKindsAuthoring>
    {
        public override void Bake(TimelinePlayerKindsAuthoring authoring)
        {
            var registry = KindRegistry.Discover();
            var fed = authoring.FedMap();
            var timelines = TimelineKindReachability.CollectTimelines(authoring.StartWith, authoring.SlotMap(), authoring.NextMap());
            var kinds = new HashSet<int>();
            CollectFedKinds(authoring.gameObject, fed, timelines, registry, kinds);
            CollectRequiredKinds(authoring.gameObject, authoring.RequiredKinds, registry, kinds);
            CollectAttributedKinds(authoring, registry, kinds);
            var entity = GetEntity(TransformUsageFlags.None);
            for (var i = 0; i < registry.Count; i++)
            {
                if (!kinds.Contains(i))
                    continue;
                KindDescriptor descriptor = registry[i];
                AddComponent(entity, ComponentType.ReadWrite(descriptor.Entry));
                SetComponentEnabled(entity, ComponentType.ReadWrite(descriptor.Active), false);
                SetComponentEnabled(entity, ComponentType.ReadWrite(descriptor.Enabled), true);
            }
        }

        static void CollectFedKinds(GameObject gameObject, IDictionary<int, string[]> fed, HashSet<int> timelines, KindRegistry registry, HashSet<int> kinds)
        {
            foreach (var timeline in timelines)
            {
                if (!fed.TryGetValue(timeline, out var names) || names == null)
                    continue;
                foreach (var name in names)
                    kinds.Add(ResolveKind(gameObject, name, "timeline " + timeline, registry));
            }
        }

        static void CollectRequiredKinds(GameObject gameObject, string[] required, KindRegistry registry, HashSet<int> kinds)
        {
            if (required == null)
                return;
            foreach (var name in required)
                kinds.Add(ResolveKind(gameObject, name, "required kinds", registry));
        }

        static void CollectAttributedKinds(TimelinePlayerKindsAuthoring authoring, KindRegistry registry, HashSet<int> kinds)
        {
            var components = authoring.GetComponents<Component>();
            if (components == null)
                return;
            foreach (var component in components)
            {
                if (component == null)
                    continue;
                var attribute = Attribute.GetCustomAttribute(component.GetType(), typeof(RequiresKindsAttribute), true) as RequiresKindsAttribute;
                if (attribute == null || attribute.Kinds == null)
                    continue;
                foreach (var kind in attribute.Kinds)
                {
                    if (kind == null)
                        continue;
                    var index = registry.IndexOf(kind.Name);
                    if (index < 0)
                        throw new InvalidOperationException("GameObject '" + authoring.gameObject.name + "' component '" + component.GetType().Name + "' requires kind '" + kind.FullName + "' with no registered column.");
                    kinds.Add(index);
                }
            }
        }

        static int ResolveKind(GameObject gameObject, string name, string context, KindRegistry registry)
        {
            var index = string.IsNullOrEmpty(name) ? -1 : registry.IndexOf(ResolveKindName(name));
            if (index < 0)
                throw new InvalidOperationException("GameObject '" + gameObject.name + "' " + context + " names kind '" + name + "' with no registered column.");
            return index;
        }

        static string ResolveKindName(string name)
        {
            var direct = Type.GetType(name, false);
            if (direct != null)
                return direct.Name;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }
                foreach (var candidate in types)
                    if (candidate.Name == name || candidate.FullName == name)
                        return candidate.Name;
            }
            return name ?? string.Empty;
        }
    }
}
