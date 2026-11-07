using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Tl.Gen.Tlb;

namespace Tl.Unity.Bake
{
    static class TlKindBytes
    {
        const uint Magic = 0x444B4C54u;
        const uint Version = 1u;
        const int HeaderBytes = 16;
        const int KindBytes = 16;

        internal static byte[] Write(KindTimeline timeline, TlKindCatalog catalog)
        {
            var kinds = catalog.Value.Kinds;
            var rows = kinds.Select(k => new KindCapacityRow(k.Name, false, 0, 0, k.EntryBytes, "-")).ToList();
            foreach (var group in kinds.Where(k => k.Group != null).Select(k => k.Group!).Distinct(StringComparer.Ordinal))
                rows.Add(new KindCapacityRow(group, true, 0, 0, 0, "-"));
            var section = KindLayout.Section(catalog.Value, new List<KindTimeline> { timeline }, rows);
            var head = HeaderBytes + KindBytes * kinds.Count;
            var bytes = new byte[head + section.Length];
            var span = bytes.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), Version);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), (uint)kinds.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), (uint)section.Length);
            for (var k = 0; k < kinds.Count; k++)
            {
                var at = HeaderBytes + KindBytes * k;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(at), timeline.MaxSpans[k]);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(at + 4), timeline.MaxPulses[k]);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(at + 8), catalog.Knobs[k].Inline);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(at + 12), catalog.Knobs[k].GamePulses);
            }
            section.CopyTo(bytes, head);
            return bytes;
        }
    }
}
