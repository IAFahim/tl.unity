using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Tl.Unity.Editor
{
    public static class TlKindBaking
    {
        public static void AddReached(IBaker baker, Entity entity, ulong mask, IReadOnlyList<string> catalogKinds, HashSet<ComponentType> added, string owner)
        {
            var registry = TimelineKindSets.Registry;
            var reached = new List<int>();
            for (var k = 0; k < catalogKinds.Count && k < 64; k++)
            {
                if ((mask & (1UL << k)) == 0)
                    continue;
                var index = registry.IndexOf(catalogKinds[k]);
                if (index < 0)
                {
                    Debug.LogWarning(owner + " plays a timeline that feeds " + catalogKinds[k] + ", but no Unity system runs it. Check that the code compiles.");
                    continue;
                }
                var descriptor = registry[index];
                Add(baker, entity, added, ComponentType.ReadWrite(descriptor.Entry));
                Add(baker, entity, added, ComponentType.ReadWrite(descriptor.Active));
                Add(baker, entity, added, ComponentType.ReadWrite(descriptor.Enabled));
                foreach (var column in descriptor.Columns)
                    Add(baker, entity, added, ComponentType.ReadWrite(column));
                reached.Add(index);
            }
            foreach (var buffer in registry.EventBuffers)
                Add(baker, entity, added, ComponentType.ReadWrite(buffer));
            if (reached.Count == 0)
                return;
            var name = new FixedString128Bytes();
            name.CopyFromTruncated(owner ?? string.Empty);
            baker.AddComponent(entity, new TimelineKindBakeOwner { Name = name });
            var bake = baker.AddBuffer<TimelineKindBake>(entity);
            foreach (var index in reached)
                bake.Add(new TimelineKindBake { Kind = index, Source = name });
        }

        static void Add(IBaker baker, Entity entity, HashSet<ComponentType> added, ComponentType type)
        {
            if (added.Add(type))
                baker.AddComponent(entity, type);
        }
    }
}
