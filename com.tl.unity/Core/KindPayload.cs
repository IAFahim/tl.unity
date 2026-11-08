using System.Buffers.Binary;

namespace Tl;

public sealed class KindPayload
{
    public const uint Magic = 0x444B4C54u;
    public const uint Version = 1u;
    internal const int HeaderBytes = 16;
    internal const int KindBytes = 16;

    public int KindCount { get; private set; }
    public int FeederCount { get; private set; }
    public int[] MaxSpans { get; private set; } = [];
    public int[] MaxPulses { get; private set; } = [];
    public int[] Inline { get; private set; } = [];
    public int[] GamePulses { get; private set; } = [];

    internal byte[] Section = [];
    internal int CatalogEnd;
    internal int TimelineRow;
    internal int DataStart;
    internal int StringsAt;
    internal int StringsLength;

    public ulong KindMask => BinaryPrimitives.ReadUInt64LittleEndian(Section.AsSpan(TimelineRow));

    internal ReadOnlySpan<byte> Catalog => Section.AsSpan(LibraryLayout.SectionHeaderBytes, CatalogEnd - LibraryLayout.SectionHeaderBytes);

    internal ReadOnlySpan<byte> Strings => Section.AsSpan(StringsAt, StringsLength);

    internal ReadOnlySpan<byte> Data => Section.AsSpan(DataStart, StringsAt - DataStart);

    public static byte[] Write(IReadOnlyList<int> maxSpans, IReadOnlyList<int> maxPulses, IReadOnlyList<int> inline, IReadOnlyList<int> gamePulses, byte[] section)
    {
        var kinds = maxSpans.Count;
        if (maxPulses.Count != kinds || inline.Count != kinds || gamePulses.Count != kinds)
            throw new ArgumentException("Tl: kind payload columns differ in length.");
        var head = HeaderBytes + KindBytes * kinds;
        var bytes = new byte[head + section.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), (uint)kinds);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), (uint)section.Length);
        for (var k = 0; k < kinds; k++)
        {
            var at = HeaderBytes + KindBytes * k;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(at), maxSpans[k]);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(at + 4), maxPulses[k]);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(at + 8), inline[k]);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(at + 12), gamePulses[k]);
        }
        section.CopyTo(bytes, head);
        return bytes;
    }

    public static KindPayload? Read(byte[]? bytes, string timeline)
    {
        if (bytes == null || bytes.Length == 0)
            return null;
        var span = bytes.AsSpan();
        if (bytes.Length < HeaderBytes || U32(span, 0) != Magic || U32(span, 4) != Version)
            throw Bad(timeline, "the kind data has an unknown format");
        var kinds = (int)U32(span, 8);
        var sectionLength = (int)U32(span, 12);
        var head = HeaderBytes + (long)KindBytes * kinds;
        if (kinds > 64 || head + (long)sectionLength != bytes.Length || sectionLength < LibraryLayout.SectionHeaderBytes)
            throw Bad(timeline, "the kind data is truncated");
        var payload = new KindPayload
        {
            KindCount = kinds,
            MaxSpans = new int[kinds],
            MaxPulses = new int[kinds],
            Inline = new int[kinds],
            GamePulses = new int[kinds],
            Section = span.Slice((int)head, sectionLength).ToArray(),
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
        payload.CatalogEnd = LibraryLayout.SectionHeaderBytes + LibraryLayout.KindRowBytes * kinds + LibraryLayout.FeederRowBytes * payload.FeederCount;
        payload.TimelineRow = (int)U32(section, 28);
        payload.DataStart = payload.TimelineRow + LibraryLayout.KindTimelineRowBytes;
        payload.StringsAt = (int)U32(section, 32);
        payload.StringsLength = (int)U32(section, 36);
        if (U32(section, 0) != LibraryLayout.SectionMagic || U32(section, 8) != (uint)kinds || U32(section, 16) != 1u || payload.TimelineRow != payload.CatalogEnd
            || payload.StringsAt < payload.DataStart || (long)payload.StringsAt + payload.StringsLength > sectionLength)
            throw Bad(timeline, "the kind section is malformed");
        return payload;
    }

    public bool SameCatalog(KindPayload other)
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

    internal string KindName(int kind) => Text(LibraryLayout.SectionHeaderBytes + LibraryLayout.KindRowBytes * kind)!;

    internal string? GroupOf(int kind) => Text(LibraryLayout.SectionHeaderBytes + LibraryLayout.KindRowBytes * kind + 28);

    internal int ValueBytes(int kind) => (int)U32(Section, LibraryLayout.SectionHeaderBytes + LibraryLayout.KindRowBytes * kind + 8);

    internal int EntryBytes(int kind) => (int)U32(Section, LibraryLayout.SectionHeaderBytes + LibraryLayout.KindRowBytes * kind + 12);

    internal bool AcceptsPulses(int kind) => Section[LibraryLayout.SectionHeaderBytes + LibraryLayout.KindRowBytes * kind + 20] != 0;

    string? Text(int at)
    {
        var length = (int)U32(Section, at + 4);
        return length == 0 ? null : System.Text.Encoding.UTF8.GetString(Section, StringsAt + (int)U32(Section, at), length);
    }

    static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at));

    static InvalidOperationException Bad(string timeline, string what)
        => new("Tl: timeline '" + timeline + "': " + what + "; rebake it with this version of Tl.");
}
