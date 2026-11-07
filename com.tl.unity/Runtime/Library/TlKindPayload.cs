using System;
using System.Buffers.Binary;

namespace Tl.Unity
{
    sealed class TlKindPayload
    {
        const uint Magic = 0x444B4C54u;
        const uint Version = 1u;
        const int HeaderBytes = 16;
        const int KindBytes = 16;
        const int SectionHeaderBytes = 64;
        const int KindRowBytes = 40;
        const int FeederRowBytes = 32;
        const int TimelineRowBytes = 48;

        public int KindCount;
        public int FeederCount;
        public int[] MaxSpans;
        public int[] MaxPulses;
        public int[] Inline;
        public int[] GamePulses;
        public byte[] Section;
        public int CatalogEnd;
        public int TimelineRow;
        public int DataStart;
        public int StringsAt;
        public int StringsLength;

        public ulong KindMask => BinaryPrimitives.ReadUInt64LittleEndian(Section.AsSpan(TimelineRow));

        public ReadOnlySpan<byte> Catalog => Section.AsSpan(SectionHeaderBytes, CatalogEnd - SectionHeaderBytes);

        public ReadOnlySpan<byte> Strings => Section.AsSpan(StringsAt, StringsLength);

        public ReadOnlySpan<byte> Data => Section.AsSpan(DataStart, StringsAt - DataStart);

        public static TlKindPayload Read(byte[] bytes, string timeline)
        {
            if (bytes == null || bytes.Length == 0)
                return null;
            var span = bytes.AsSpan();
            if (bytes.Length < HeaderBytes || U32(span, 0) != Magic || U32(span, 4) != Version)
                throw Bad(timeline, "the kind data has an unknown format");
            var kinds = (int)U32(span, 8);
            var sectionLength = (int)U32(span, 12);
            var head = HeaderBytes + KindBytes * kinds;
            if (kinds > 64 || head + (long)sectionLength != bytes.Length || sectionLength < SectionHeaderBytes)
                throw Bad(timeline, "the kind data is truncated");
            var payload = new TlKindPayload
            {
                KindCount = kinds,
                MaxSpans = new int[kinds],
                MaxPulses = new int[kinds],
                Inline = new int[kinds],
                GamePulses = new int[kinds],
                Section = span.Slice(head, sectionLength).ToArray(),
            };
            for (var k = 0; k < kinds; k++)
            {
                var at = HeaderBytes + KindBytes * k;
                payload.MaxSpans[k] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(at));
                payload.MaxPulses[k] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(at + 4));
                payload.Inline[k] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(at + 8));
                payload.GamePulses[k] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(at + 12));
            }
            var section = payload.Section.AsSpan();
            payload.FeederCount = (int)U32(section, 12);
            payload.CatalogEnd = SectionHeaderBytes + KindRowBytes * kinds + FeederRowBytes * payload.FeederCount;
            payload.TimelineRow = (int)U32(section, 28);
            payload.DataStart = payload.TimelineRow + TimelineRowBytes;
            payload.StringsAt = (int)U32(section, 32);
            payload.StringsLength = (int)U32(section, 36);
            if (U32(section, 8) != (uint)kinds || U32(section, 16) != 1u || payload.TimelineRow != payload.CatalogEnd
                || payload.StringsAt < payload.DataStart || (long)payload.StringsAt + payload.StringsLength > sectionLength)
                throw Bad(timeline, "the kind section is malformed");
            return payload;
        }

        public bool SameCatalog(TlKindPayload other)
        {
            if (other.KindCount != KindCount || other.FeederCount != FeederCount)
                return false;
            if (!Catalog.SequenceEqual(other.Catalog) || !Strings.SequenceEqual(other.Strings))
                return false;
            for (var k = 0; k < KindCount; k++)
                if (Inline[k] != other.Inline[k] || GamePulses[k] != other.GamePulses[k])
                    return false;
            return true;
        }

        static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at));

        static InvalidOperationException Bad(string timeline, string what)
            => new InvalidOperationException("Tl: timeline '" + timeline + "': " + what + "; rebake it with this version of the package.");
    }
}
