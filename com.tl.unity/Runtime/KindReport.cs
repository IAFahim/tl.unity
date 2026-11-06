using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    public struct KindArchetypeRow
    {
        public string Kinds;
        public int KindCount;
        public int Entities;
        public int Chunks;
        public int ChunkCapacity;
        public int KindBytes;
    }

    public static class KindReport
    {
        public const int MaxKinds = 32;
        public const int MaxKindBytes = 1024;
        public const int MinChunkCapacity = 16;

        public static List<KindArchetypeRow> Capture(EntityManager entities, KindRegistry registry)
        {
            var rows = new Dictionary<EntityArchetype, KindArchetypeRow>();
            using var chunks = entities.GetAllChunks(Allocator.Temp);
            foreach (var chunk in chunks)
            {
                var archetype = chunk.Archetype;
                if (!rows.TryGetValue(archetype, out var row))
                {
                    row = Describe(archetype, registry);
                    if (row.KindCount == 0)
                        continue;
                }
                row.Entities += chunk.Count;
                row.Chunks++;
                rows[archetype] = row;
            }
            var list = new List<KindArchetypeRow>(rows.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.Kinds, b.Kinds));
            return list;
        }

        static KindArchetypeRow Describe(EntityArchetype archetype, KindRegistry registry)
        {
            var row = new KindArchetypeRow { ChunkCapacity = archetype.ChunkCapacity };
            var names = new StringBuilder();
            using var types = archetype.GetComponentTypes(Allocator.Temp);
            for (var k = 0; k < registry.Count; k++)
            {
                var entry = TypeManager.GetTypeIndex(registry[k].Entry);
                var found = false;
                foreach (var type in types)
                    found |= type.TypeIndex == entry;
                if (!found)
                    continue;
                if (names.Length > 0)
                    names.Append('+');
                names.Append(registry[k].Name);
                row.KindCount++;
                row.KindBytes += TypeManager.GetTypeInfo(entry).SizeInChunk;
            }
            row.Kinds = names.ToString();
            return row;
        }

        public static List<string> Warnings(IReadOnlyList<KindArchetypeRow> rows)
        {
            var warnings = new List<string>();
            foreach (var row in rows)
            {
                if (row.KindCount > MaxKinds)
                    warnings.Add(row.Kinds + ": " + row.KindCount + " kinds per entity, more than " + MaxKinds + ".");
                if (row.KindBytes > MaxKindBytes)
                    warnings.Add(row.Kinds + ": " + row.KindBytes + " B of kind storage per entity, more than " + MaxKindBytes + " B.");
                if (row.ChunkCapacity < MinChunkCapacity)
                    warnings.Add(row.Kinds + ": " + row.ChunkCapacity + " entities per chunk, fewer than " + MinChunkCapacity + "; kind storage is " + row.KindBytes + " B per entity.");
            }
            return warnings;
        }

        public static string Text(string world, IReadOnlyList<KindArchetypeRow> rows)
        {
            var text = new StringBuilder();
            text.Append("Tl Kind Report, world '").Append(world).Append("': ").Append(rows.Count).Append(" archetypes with kinds\n");
            foreach (var row in rows)
                text.Append("  ").Append(row.Kinds).Append(": ").Append(row.Entities).Append(" entities in ").Append(row.Chunks)
                    .Append(" chunks of ").Append(row.ChunkCapacity).Append(", ").Append(row.KindCount).Append(" kinds, ")
                    .Append(row.KindBytes).Append(" B kind storage per entity\n");
            foreach (var warning in Warnings(rows))
                text.Append("  warning: ").Append(warning).Append('\n');
            return text.ToString();
        }
    }
}
