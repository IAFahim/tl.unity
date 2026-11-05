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

        public static HashSet<int> CollectKinds(
            ICollection<int> reachableTimelines,
            IDictionary<int, int[]> fedKinds,
            ICollection<int> declaredGamePulses)
        {
            var kinds = new HashSet<int>();
            if (reachableTimelines != null && fedKinds != null)
                foreach (var timeline in reachableTimelines)
                    if (fedKinds.TryGetValue(timeline, out var fed) && fed != null)
                        foreach (var kind in fed)
                            kinds.Add(kind);
            if (declaredGamePulses != null)
                foreach (var kind in declaredGamePulses)
                    kinds.Add(kind);
            return kinds;
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
            public int[] Kinds;
        }

        public int[] StartWith = Array.Empty<int>();
        public SlotBinding[] Slots = Array.Empty<SlotBinding>();
        public NextBinding[] Next = Array.Empty<NextBinding>();
        public FedBinding[] FedKinds = Array.Empty<FedBinding>();
        public int[] DeclaredGamePulses = Array.Empty<int>();

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

        public Dictionary<int, int[]> FedMap()
        {
            var map = new Dictionary<int, List<int>>();
            if (FedKinds != null)
                for (var i = 0; i < FedKinds.Length; i++)
                {
                    if (!map.TryGetValue(FedKinds[i].Timeline, out var kinds))
                    {
                        kinds = new List<int>();
                        map[FedKinds[i].Timeline] = kinds;
                    }
                    if (FedKinds[i].Kinds != null)
                        kinds.AddRange(FedKinds[i].Kinds);
                }
            var frozen = new Dictionary<int, int[]>();
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
            var kinds = TimelineKindReachability.CollectKinds(timelines, fed, authoring.DeclaredGamePulses);
            ThrowOnMissingFedColumn(authoring.gameObject, fed, timelines, registry);
            ThrowOnMissingDeclaredColumn(authoring.gameObject, authoring.DeclaredGamePulses, registry);
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

        static void ThrowOnMissingFedColumn(GameObject gameObject, IDictionary<int, int[]> fed, HashSet<int> timelines, KindRegistry registry)
        {
            foreach (var timeline in timelines)
            {
                if (!fed.TryGetValue(timeline, out var ids) || ids == null)
                    continue;
                foreach (var id in ids)
                    if (id < 0 || id >= registry.Count)
                        throw new InvalidOperationException("GameObject '" + gameObject.name + "' timeline " + timeline + " feeds kind " + id + " with no registered column.");
            }
        }

        static void ThrowOnMissingDeclaredColumn(GameObject gameObject, int[] declared, KindRegistry registry)
        {
            if (declared == null)
                return;
            foreach (var id in declared)
                if (id < 0 || id >= registry.Count)
                    throw new InvalidOperationException("GameObject '" + gameObject.name + "' declares kind " + id + " with no registered column.");
        }
    }
}
