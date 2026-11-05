using System;
using System.Collections.Generic;
using System.Text;
using Tl.Unity;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Tools
{
    [Serializable]
    public struct KindArchetypeRow
    {
        public string Archetype;
        public int Entities;
        public int Kinds;
        public int KindBytesPerEntity;
    }

    [Serializable]
    public struct KindReportWarning
    {
        public string Archetype;
        public string Code;
        public string Detail;
    }

    public static class KindReport
    {
        public const int MaxKinds = 32;
        public const int MaxStorageBytesPerEntity = 1024;
        public const int MinEntitiesPerChunk = 16;

        public static List<KindReportWarning> Analyze(IList<KindArchetypeRow> rows)
        {
            var warnings = new List<KindReportWarning>();
            if (rows == null)
                return warnings;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Kinds > MaxKinds)
                    warnings.Add(Warn(row.Archetype, "TLKIND001", row.Kinds + " kinds exceeds " + MaxKinds));
                if (row.KindBytesPerEntity > MaxStorageBytesPerEntity)
                    warnings.Add(Warn(row.Archetype, "TLKIND002", row.KindBytesPerEntity + " B exceeds " + MaxStorageBytesPerEntity + " B"));
                if (row.Entities > 0 && row.Entities < MinEntitiesPerChunk)
                    warnings.Add(Warn(row.Archetype, "TLKIND003", row.Entities + " entities/chunk below " + MinEntitiesPerChunk));
            }
            return warnings;
        }

        static KindReportWarning Warn(string archetype, string code, string detail)
        {
            return new KindReportWarning { Archetype = archetype, Code = code, Detail = detail };
        }

// SCAFFOLD (tl#557)
        public static List<KindArchetypeRow> CaptureRows(World world, KindRegistry registry)
        {
            var rows = new List<KindArchetypeRow>();
            if (world == null || !world.IsCreated || registry == null)
                return rows;
            var archetypes = world.EntityManager.GetAllArchetypes(Allocator.Temp);
            try
            {
                for (var i = 0; i < archetypes.Length; i++)
                {
                    var archetype = archetypes[i];
                    if (archetype.EntityCount == 0)
                        continue;
                    var names = new StringBuilder();
                    var kinds = 0;
                    var bytes = 0;
                    for (var k = 0; k < registry.Count; k++)
                    {
                        var descriptor = registry[k];
                        if (!archetype.HasComponent(ComponentType.ReadWrite(descriptor.Entry)))
                            continue;
                        if (names.Length > 0)
                            names.Append('+');
                        names.Append(descriptor.Name);
                        kinds++;
                        bytes += TypeManager.GetTypeInfo(TypeManager.GetTypeIndex(descriptor.Entry)).SizeInChunk;
                    }
                    if (kinds == 0)
                        continue;
                    var chunks = archetype.ChunkCount;
                    rows.Add(new KindArchetypeRow
                    {
                        Archetype = names.ToString(),
                        Entities = chunks > 0 ? archetype.EntityCount / chunks : archetype.EntityCount,
                        Kinds = kinds,
                        KindBytesPerEntity = bytes,
                    });
                }
            }
            finally
            {
                archetypes.Dispose();
            }
            return rows;
        }
    }
}
