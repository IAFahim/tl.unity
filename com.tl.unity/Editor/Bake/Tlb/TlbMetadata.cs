using System.Buffers.Binary;
using System.Text;

namespace Tl.Gen.Tlb;

public static class TlbMetadata
{
    internal const uint MetadataMagic = 0x4D42544Cu;

    public static bool HasMetadata(ReadOnlySpan<byte> tlb)
    {
        if (tlb.Length < 64) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(tlb) != 0x31424C54u) return false;
        return HotLength(tlb) < tlb.Length;
    }

    public static byte[] Strip(ReadOnlySpan<byte> tlb)
    {
        var hotLength = HotLength(tlb);
        var stripped = tlb.Slice(0, hotLength).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(stripped.AsSpan(44), (uint)hotLength);
        BinaryPrimitives.WriteUInt32LittleEndian(stripped.AsSpan(48), (uint)hotLength);
        return stripped;
    }

    public static TlbMetadataView Read(ReadOnlySpan<byte> tlb)
    {
        var metadataOffset = HotLength(tlb);
        if (metadataOffset == tlb.Length)
            throw new ArgumentException("TLB asset carries no metadata tail.");
        return TlbMetadataView.Parse(tlb.Slice(metadataOffset));
    }

    internal static int HotLength(ReadOnlySpan<byte> tlb)
    {
        if (tlb.Length < 64)
            throw new ArgumentException("TLB asset is truncated.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(tlb.Slice(4));
        if (BinaryPrimitives.ReadUInt32LittleEndian(tlb) != 0x31424C54u ||
            (version != 5u && version != TlbLayout.Version) ||
            BinaryPrimitives.ReadUInt32LittleEndian(tlb.Slice(48)) != (uint)tlb.Length)
            throw new ArgumentException("TLB asset header is invalid.");
        if (version == TlbLayout.Version && tlb.Length < 80)
            throw new ArgumentException("TLB asset is truncated.");
        var hotLength = BinaryPrimitives.ReadUInt32LittleEndian(tlb.Slice(44));
        if (hotLength == 0 || hotLength > (uint)tlb.Length)
            throw new ArgumentException("TLB hot length is invalid.");
        return (int)hotLength;
    }
}

public readonly struct TlbTypeIdentity
{
    public string Namespace { get; init; }
    public string Name { get; init; }
    public string Assembly { get; init; }
}

public readonly struct TlbLabel
{
    public int TrackEntry { get; init; }
    public int ClipIndex { get; init; }
    public string Name { get; init; }
}

public readonly struct TlbTrackRow
{
    public int Type { get; init; }
    public int Pair { get; init; }
    public int Value { get; init; }
    public int Clips { get; init; }
}

public readonly struct TlbClipRow
{
    public int Pair { get; init; }
    public int Value { get; init; }
    public uint Start { get; init; }
    public uint End { get; init; }
}

public sealed class TlbMetadataView
{
    internal const int HeaderLength = 64;
    internal const uint Version = 2u;

    private TlbMetadataView(
        IReadOnlyList<string> strings,
        IReadOnlyList<TlbTypeIdentity> types,
        IReadOnlyList<(int TrackType, int ClipType)> pairTypes,
        IReadOnlyList<TlbLabel> labels,
        IReadOnlyList<TlbTrackRow> tracks,
        IReadOnlyList<TlbClipRow> clips)
    {
        Strings = strings;
        Types = types;
        PairTypes = pairTypes;
        Labels = labels;
        Tracks = tracks;
        Clips = clips;
    }

    public IReadOnlyList<string> Strings { get; }
    public IReadOnlyList<TlbTypeIdentity> Types { get; }
    public IReadOnlyList<(int TrackType, int ClipType)> PairTypes { get; }
    public IReadOnlyList<TlbLabel> Labels { get; }
    public IReadOnlyList<TlbTrackRow> Tracks { get; }
    public IReadOnlyList<TlbClipRow> Clips { get; }

    internal static TlbMetadataView Parse(ReadOnlySpan<byte> metadata)
    {
        if (metadata.Length < HeaderLength)
            throw new ArgumentException("TLB1 metadata tail is truncated.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(metadata) != TlbMetadata.MetadataMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(4)) != Version)
            throw new ArgumentException("TLB1 metadata tail magic or version invalid; rebake the asset.");

        var header = new uint[HeaderLength / 4];
        for (var i = 0; i < header.Length; i++)
            header[i] = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(4 * i));
        var (stringCount, stringIndexOffset, stringBlobOffset, stringBlobLen) = (header[2], header[3], header[4], header[5]);
        var (typeCount, typeTableOffset, pairTypeCount, pairTypeTableOffset) = (header[6], header[7], header[8], header[9]);
        var (labelCount, labelTableOffset, trackCount, trackTableOffset) = (header[10], header[11], header[12], header[13]);
        var (clipCount, clipTableOffset) = (header[14], header[15]);

        Check(metadata.Length, stringIndexOffset, 4ul * stringCount);
        Check(metadata.Length, stringBlobOffset, stringBlobLen);
        Check(metadata.Length, typeTableOffset, 12ul * typeCount);
        Check(metadata.Length, pairTypeTableOffset, 8ul * pairTypeCount);
        Check(metadata.Length, labelTableOffset, 12ul * labelCount);
        Check(metadata.Length, trackTableOffset, 16ul * trackCount);
        Check(metadata.Length, clipTableOffset, 16ul * clipCount);

        var words = metadata.ToArray();
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(at));
        var strings = new List<string>((int)stringCount);
        for (var i = 0; i < stringCount; i++)
        {
            var start = U((int)stringIndexOffset + 4 * i);
            var end = i + 1 < stringCount ? U((int)stringIndexOffset + 4 * (i + 1)) : stringBlobLen;
            if (start > end || end > stringBlobLen)
                throw new ArgumentException("TLB1 metadata tail string out of bounds.");
            strings.Add(Encoding.UTF8.GetString(words, (int)(stringBlobOffset + start), (int)(end - start)));
        }

        string Str(int at)
        {
            var index = U(at);
            if (index >= stringCount) throw new ArgumentException("TLB1 metadata tail names a string beyond StringCount.");
            return strings[(int)index];
        }

        int Index(int at, uint count, bool optional = false)
        {
            var value = U(at);
            if (optional && value == uint.MaxValue) return -1;
            if (value >= count) throw new ArgumentException("TLB1 metadata tail row index out of bounds.");
            return (int)value;
        }

        var types = new List<TlbTypeIdentity>((int)typeCount);
        for (var i = 0; i < typeCount; i++)
        {
            var at = (int)typeTableOffset + 12 * i;
            types.Add(new TlbTypeIdentity { Namespace = Str(at), Name = Str(at + 4), Assembly = Str(at + 8) });
        }

        var pairTypes = new List<(int TrackType, int ClipType)>((int)pairTypeCount);
        for (var i = 0; i < pairTypeCount; i++)
        {
            var at = (int)pairTypeTableOffset + 8 * i;
            pairTypes.Add((Index(at, typeCount), Index(at + 4, typeCount)));
        }

        var labels = new List<TlbLabel>((int)labelCount);
        for (var i = 0; i < labelCount; i++)
        {
            var at = (int)labelTableOffset + 12 * i;
            labels.Add(new TlbLabel { TrackEntry = (int)U(at), ClipIndex = (int)U(at + 4), Name = Str(at + 8) });
        }

        var tracks = new List<TlbTrackRow>((int)trackCount);
        var covered = 0L;
        for (var i = 0; i < trackCount; i++)
        {
            var at = (int)trackTableOffset + 16 * i;
            var row = new TlbTrackRow { Type = Index(at, typeCount), Pair = Index(at + 4, pairTypeCount, true), Value = (int)U(at + 8), Clips = (int)U(at + 12) };
            if (row.Pair < 0 != (row.Clips == 0))
                throw new ArgumentException("TLB1 metadata track row needs a pair exactly when it has clips.");
            covered += (uint)row.Clips;
            tracks.Add(row);
        }
        if (covered != clipCount)
            throw new ArgumentException("TLB1 metadata track rows do not cover the clip table.");

        var clips = new List<TlbClipRow>((int)clipCount);
        for (var i = 0; i < clipCount; i++)
        {
            var at = (int)clipTableOffset + 16 * i;
            var row = new TlbClipRow { Pair = Index(at, pairTypeCount), Value = (int)U(at + 4), Start = U(at + 8), End = U(at + 12) };
            if (row.Start >= row.End)
                throw new ArgumentException("TLB1 metadata clip row window is empty.");
            clips.Add(row);
        }

        return new TlbMetadataView(strings, types, pairTypes, labels, tracks, clips);
    }

    private static void Check(int total, uint offset, ulong length)
    {
        if (offset + length > (ulong)total)
            throw new ArgumentException("TLB1 metadata tail sections out of bounds.");
    }
}

internal static class TlbMetadataBuilder
{
    public static byte[] Build(
        (Type Track, Type Clip)[] pairTypes,
        List<(int Track, int Clip, string Name)> labels,
        List<FastMarker>? markers,
        IReadOnlyList<(Type Type, int Pair, int Value, int Clips)> tracks,
        IReadOnlyList<(int Pair, int Value, uint Start, uint End)> clips)
    {
        var distinctTypes = pairTypes
            .SelectMany(pair => new[] { pair.Track, pair.Clip })
            .Concat(tracks.Select(t => t.Type))
            .Concat((markers ?? new List<FastMarker>()).Where(m => m.MarkerType != null).Select(m => m.MarkerType!))
            .Distinct()
            .Select(t => (Type: t, Namespace: t.Namespace ?? string.Empty, Name: TypeNames.JsonName(t), Assembly: t.Assembly.GetName().Name ?? string.Empty))
            .OrderBy(t => t.Namespace, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Assembly, StringComparer.Ordinal)
            .ToArray();

        var pool = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in distinctTypes)
        {
            pool.Add(type.Namespace);
            pool.Add(type.Name);
            pool.Add(type.Assembly);
        }
        foreach (var label in labels)
            pool.Add(label.Name);

        var strings = pool.ToArray();
        var index = new Dictionary<string, int>(strings.Length, StringComparer.Ordinal);
        for (var i = 0; i < strings.Length; i++)
            index[strings[i]] = i;

        var header = TlbMetadataView.HeaderLength;
        var blobLimit = header + 4 * strings.Length;
        var offsets = new uint[strings.Length];
        using var blob = new MemoryStream();
        for (var i = 0; i < strings.Length; i++)
        {
            offsets[i] = (uint)blob.Length;
            var encoded = Encoding.UTF8.GetBytes(strings[i]);
            blob.Write(encoded, 0, encoded.Length);
        }
        var stringBlobLen = (uint)blob.Length;

        var typeIndex = new Dictionary<Type, int>(distinctTypes.Length);
        for (var i = 0; i < distinctTypes.Length; i++)
            typeIndex[distinctTypes[i].Type] = i;

        var typeTableOffset = blobLimit + (int)stringBlobLen;
        var pairTypeTableOffset = typeTableOffset + 12 * distinctTypes.Length;
        var labelTableOffset = pairTypeTableOffset + 8 * pairTypes.Length;
        var trackTableOffset = labelTableOffset + 12 * labels.Count;
        var clipTableOffset = trackTableOffset + 16 * tracks.Count;
        var total = clipTableOffset + 16 * clips.Count;

        var metadata = new byte[total];
        void W(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(at), value);
        W(0, TlbMetadata.MetadataMagic);
        W(4, TlbMetadataView.Version);
        W(8, (uint)strings.Length);
        W(12, (uint)header);
        W(16, (uint)blobLimit);
        W(20, stringBlobLen);
        W(24, (uint)distinctTypes.Length);
        W(28, (uint)typeTableOffset);
        W(32, (uint)pairTypes.Length);
        W(36, (uint)pairTypeTableOffset);
        W(40, (uint)labels.Count);
        W(44, (uint)labelTableOffset);
        W(48, (uint)tracks.Count);
        W(52, (uint)trackTableOffset);
        W(56, (uint)clips.Count);
        W(60, (uint)clipTableOffset);

        for (var i = 0; i < strings.Length; i++)
            W(header + 4 * i, offsets[i]);
        blob.ToArray().CopyTo(metadata, blobLimit);

        for (var i = 0; i < distinctTypes.Length; i++)
        {
            var at = typeTableOffset + 12 * i;
            W(at, (uint)index[distinctTypes[i].Namespace]);
            W(at + 4, (uint)index[distinctTypes[i].Name]);
            W(at + 8, (uint)index[distinctTypes[i].Assembly]);
        }

        for (var i = 0; i < pairTypes.Length; i++)
        {
            var at = pairTypeTableOffset + 8 * i;
            W(at, (uint)typeIndex[pairTypes[i].Track]);
            W(at + 4, (uint)typeIndex[pairTypes[i].Clip]);
        }

        for (var i = 0; i < labels.Count; i++)
        {
            var at = labelTableOffset + 12 * i;
            W(at, (uint)labels[i].Track);
            W(at + 4, (uint)labels[i].Clip);
            W(at + 8, (uint)index[labels[i].Name]);
        }

        for (var i = 0; i < tracks.Count; i++)
        {
            var at = trackTableOffset + 16 * i;
            W(at, (uint)typeIndex[tracks[i].Type]);
            W(at + 4, tracks[i].Pair < 0 ? uint.MaxValue : (uint)tracks[i].Pair);
            W(at + 8, (uint)tracks[i].Value);
            W(at + 12, (uint)tracks[i].Clips);
        }

        for (var i = 0; i < clips.Count; i++)
        {
            var at = clipTableOffset + 16 * i;
            W(at, (uint)clips[i].Pair);
            W(at + 4, (uint)clips[i].Value);
            W(at + 8, clips[i].Start);
            W(at + 12, clips[i].End);
        }

        return metadata;
    }
}
