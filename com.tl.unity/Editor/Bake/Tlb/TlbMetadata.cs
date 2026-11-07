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

public sealed class TlbMetadataView
{
    private const int HeaderLength = 48;

    private TlbMetadataView(
        IReadOnlyList<string> strings,
        IReadOnlyList<TlbTypeIdentity> types,
        IReadOnlyList<(int TrackType, int ClipType)> pairTypes,
        IReadOnlyList<TlbLabel> labels)
    {
        Strings = strings;
        Types = types;
        PairTypes = pairTypes;
        Labels = labels;
    }

    public IReadOnlyList<string> Strings { get; }
    public IReadOnlyList<TlbTypeIdentity> Types { get; }
    public IReadOnlyList<(int TrackType, int ClipType)> PairTypes { get; }
    public IReadOnlyList<TlbLabel> Labels { get; }

    internal static TlbMetadataView Parse(ReadOnlySpan<byte> metadata)
    {
        if (metadata.Length < HeaderLength)
            throw new ArgumentException("TLB1 metadata tail is truncated.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(metadata) != TlbMetadata.MetadataMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(4)) != 1u)
            throw new ArgumentException("TLB1 metadata tail magic or version invalid.");

        var stringCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(8));
        var stringIndexOffset = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(12));
        var stringBlobOffset = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(16));
        var stringBlobLen = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(20));
        var typeCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(24));
        var typeTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(28));
        var pairTypeCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(32));
        var pairTypeTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(36));
        var labelCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(40));
        var labelTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(44));

        Check(metadata.Length, stringIndexOffset, 4ul * stringCount);
        Check(metadata.Length, stringBlobOffset, stringBlobLen);
        Check(metadata.Length, typeTableOffset, 12ul * typeCount);
        Check(metadata.Length, pairTypeTableOffset, 8ul * pairTypeCount);
        Check(metadata.Length, labelTableOffset, 12ul * labelCount);

        var blob = metadata.Slice((int)stringBlobOffset, (int)stringBlobLen);
        var strings = new List<string>((int)stringCount);
        for (var i = 0; i < stringCount; i++)
        {
            var start = BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice((int)stringIndexOffset + 4 * i));
            var end = i + 1 < stringCount
                ? BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice((int)stringIndexOffset + 4 * (i + 1)))
                : stringBlobLen;
            strings.Add(Encoding.UTF8.GetString(blob.Slice((int)start, (int)(end - start))));
        }

        var types = new List<TlbTypeIdentity>((int)typeCount);
        for (var i = 0; i < typeCount; i++)
        {
            var at = (int)typeTableOffset + 12 * i;
            types.Add(new TlbTypeIdentity
            {
                Namespace = strings[(int)BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(at))],
                Name = strings[(int)BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(at + 4))],
                Assembly = strings[(int)BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(at + 8))],
            });
        }

        var pairTypes = new List<(int TrackType, int ClipType)>((int)pairTypeCount);
        for (var i = 0; i < pairTypeCount; i++)
        {
            var at = (int)pairTypeTableOffset + 8 * i;
            pairTypes.Add((
                (int)BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(at)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(at + 4))));
        }

        var labels = new List<TlbLabel>((int)labelCount);
        for (var i = 0; i < labelCount; i++)
        {
            var at = (int)labelTableOffset + 12 * i;
            labels.Add(new TlbLabel
            {
                TrackEntry = (int)BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(at)),
                ClipIndex = (int)BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(at + 4)),
                Name = strings[(int)BinaryPrimitives.ReadUInt32LittleEndian(metadata.Slice(at + 8))],
            });
        }

        return new TlbMetadataView(strings, types, pairTypes, labels);
    }

    private static void Check(int total, uint offset, ulong length)
    {
        if (offset + length > (ulong)total)
            throw new ArgumentException("TLB1 metadata tail sections out of bounds.");
    }
}

internal static class TlbMetadataBuilder
{
    public static byte[] Build((Type Track, Type Clip)[] pairTypes, List<(int Track, int Clip, string Name)> labels, List<FastMarker>? markers = null)
    {
        var pool = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (track, clip) in pairTypes)
        {
            pool.Add(track.Namespace ?? string.Empty);
            pool.Add(track.Name);
            pool.Add(track.Assembly.GetName().Name ?? string.Empty);
            pool.Add(clip.Namespace ?? string.Empty);
            pool.Add(clip.Name);
            pool.Add(clip.Assembly.GetName().Name ?? string.Empty);
        }
        foreach (var label in labels)
            pool.Add(label.Name);
        if (markers != null)
            foreach (var marker in markers)
                if (marker.MarkerType != null)
                {
                    pool.Add(marker.MarkerType.Namespace ?? string.Empty);
                    pool.Add(marker.MarkerType.Name);
                    pool.Add(marker.MarkerType.Assembly.GetName().Name ?? string.Empty);
                }

        var strings = pool.ToArray();
        var index = new Dictionary<string, int>(strings.Length, StringComparer.Ordinal);
        for (var i = 0; i < strings.Length; i++)
            index[strings[i]] = i;

        var blobLimit = 48 + 4 * strings.Length;
        var offsets = new uint[strings.Length];
        using var blob = new MemoryStream();
        for (var i = 0; i < strings.Length; i++)
        {
            offsets[i] = (uint)blob.Length;
            var encoded = Encoding.UTF8.GetBytes(strings[i]);
            blob.Write(encoded);
        }
        var stringBlobLen = (uint)blob.Length;

        var distinctTypes = pairTypes
            .SelectMany(pair => new[] { pair.Track, pair.Clip })
            .Concat((markers ?? new List<FastMarker>()).Where(m => m.MarkerType != null).Select(m => m.MarkerType!))
            .Distinct()
            .OrderBy(t => t.Namespace ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Assembly.GetName().Name ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
        var typeIndex = new Dictionary<Type, int>(distinctTypes.Length);
        for (var i = 0; i < distinctTypes.Length; i++)
            typeIndex[distinctTypes[i]] = i;

        var typeTableOffset = blobLimit + (int)stringBlobLen;
        var pairTypeTableOffset = typeTableOffset + 12 * distinctTypes.Length;
        var labelTableOffset = pairTypeTableOffset + 8 * pairTypes.Length;
        var total = labelTableOffset + 12 * labels.Count;

        var metadata = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(0), TlbMetadata.MetadataMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(8), (uint)strings.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(12), 48u);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(16), (uint)blobLimit);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(20), stringBlobLen);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(24), (uint)distinctTypes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(28), (uint)typeTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(32), (uint)pairTypes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(36), (uint)pairTypeTableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(40), (uint)labels.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(44), (uint)labelTableOffset);

        for (var i = 0; i < strings.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(48 + 4 * i), offsets[i]);
        blob.ToArray().CopyTo(metadata, blobLimit);

        for (var i = 0; i < distinctTypes.Length; i++)
        {
            var at = typeTableOffset + 12 * i;
            var type = distinctTypes[i];
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(at), (uint)index[type.Namespace ?? string.Empty]);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(at + 4), (uint)index[type.Name]);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(at + 8), (uint)index[type.Assembly.GetName().Name ?? string.Empty]);
        }

        for (var i = 0; i < pairTypes.Length; i++)
        {
            var at = pairTypeTableOffset + 8 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(at), (uint)typeIndex[pairTypes[i].Track]);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(at + 4), (uint)typeIndex[pairTypes[i].Clip]);
        }

        for (var i = 0; i < labels.Count; i++)
        {
            var at = labelTableOffset + 12 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(at), (uint)labels[i].Track);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(at + 4), (uint)labels[i].Clip);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(at + 8), (uint)index[labels[i].Name]);
        }

        return metadata;
    }
}
