using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Tl;

internal static class LibraryLayout
{
    internal const uint LibraryMagic = 0x42494C54u;
    internal const uint SectionMagic = 0x444E494Bu;
    internal const uint TlbVersionWithHeader = 6u;
    internal const int TlbHeaderBytes = 80;
    internal const int TlbNextAt = 52;
    internal const int TlbChannelAt = 56;
    internal const uint ManifestAt = 64;
    internal const uint ManifestRowBytes = 32;
    internal const int DigestBytes = 32;
    internal const int SectionHeaderBytes = 64;
    internal const int KindRowBytes = 40;
    internal const int FeederRowBytes = 32;
    internal const int KindTimelineRowBytes = 48;
}

public struct LibraryPart
{
    public string Name;
    public byte[] Tlb;
    public byte[]? Kinds;
    public ushort Next;
    public uint[]? Refs;
}

public readonly struct KindOverride
{
    public readonly int? Inline;
    public readonly int? GamePulses;
    public readonly int? MaxInlineBytes;

    public KindOverride(int? inline = null, int? gamePulses = null, int? maxInlineBytes = null)
    {
        Inline = inline;
        GamePulses = gamePulses;
        MaxInlineBytes = maxInlineBytes;
    }
}

public struct LibrarySettings
{
    public int ChannelCount;
    public int StackDepth;
    public int MaxInlineBytes;
    public IReadOnlyDictionary<string, KindOverride>? Kinds;

    public static LibrarySettings Default => new() { StackDepth = 1, MaxInlineBytes = 64 };
}

public readonly struct KindCapacity
{
    public readonly string Name;
    public readonly bool Group;
    public readonly int Worst;
    public readonly int Capacity;
    public readonly int EntryBytes;
    public readonly string SetBy;

    public KindCapacity(string name, bool group, int worst, int capacity, int entryBytes, string setBy)
    {
        Name = name;
        Group = group;
        Worst = worst;
        Capacity = capacity;
        EntryBytes = entryBytes;
        SetBy = setBy;
    }
}

public static class LibraryAssembler
{
    public static byte[] Assemble(IReadOnlyList<LibraryPart> parts, uint tickRate, LibrarySettings settings)
        => Assemble(parts, tickRate, settings, out _);

    public static byte[] Assemble(IReadOnlyList<LibraryPart> parts, uint tickRate, LibrarySettings settings, out IReadOnlyList<KindCapacity> capacities)
    {
        var tlbs = new byte[parts.Count][];
        for (var i = 0; i < parts.Count; i++)
            tlbs[i] = Patched(parts[i], parts.Count);
        var payloads = Payloads(parts, out var catalog);
        capacities = catalog == null ? Array.Empty<KindCapacity>() : Plan(parts, payloads, catalog, settings);
        byte[]? section = null;
        foreach (var payload in payloads)
            if (payload != null && payload.KindMask != 0)
            {
                section = Write(catalog!, payloads, capacities);
                break;
            }
        return Emit(parts, tlbs, tickRate, section);
    }

    public static IReadOnlyList<KindCapacity> Capacities(IReadOnlyList<LibraryPart> parts, LibrarySettings settings)
    {
        var payloads = Payloads(parts, out var catalog);
        return catalog == null ? Array.Empty<KindCapacity>() : Plan(parts, payloads, catalog, settings);
    }

    public static uint ChannelOf(ReadOnlySpan<byte> timelineBytes)
        => HasHeader(timelineBytes) ? U32(timelineBytes, LibraryLayout.TlbChannelAt) : 0u;

    public static uint NextOf(ReadOnlySpan<byte> timelineBytes)
        => HasHeader(timelineBytes) ? U32(timelineBytes, LibraryLayout.TlbNextAt) : 0u;

    static bool HasHeader(ReadOnlySpan<byte> timelineBytes)
        => timelineBytes.Length >= LibraryLayout.TlbHeaderBytes && U32(timelineBytes, 4) == LibraryLayout.TlbVersionWithHeader;

    static byte[] Patched(LibraryPart part, int count)
    {
        if (part.Tlb == null || part.Tlb.Length == 0)
            throw new InvalidOperationException("Tl: timeline '" + part.Name + "' has no TLB bytes.");
        var bytes = (byte[])part.Tlb.Clone();
        if (HasHeader(bytes) && U32(bytes, LibraryLayout.TlbNextAt) != 0)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(LibraryLayout.TlbNextAt), part.Next <= count ? part.Next : 0u);
        if (part.Refs != null)
            for (var i = 0; i + 1 < part.Refs.Length; i += 2)
            {
                var at = (long)part.Refs[i];
                if (at + 2 > bytes.Length)
                    throw new InvalidOperationException("Tl: timeline '" + part.Name + "' has a timeline reference outside its data; rebake it.");
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((int)at), part.Refs[i + 1] <= count ? (ushort)part.Refs[i + 1] : (ushort)0);
            }
        return bytes;
    }

    static KindPayload?[] Payloads(IReadOnlyList<LibraryPart> parts, out KindPayload? catalog)
    {
        var payloads = new KindPayload?[parts.Count];
        catalog = null;
        var catalogName = "";
        for (var i = 0; i < parts.Count; i++)
        {
            var payload = KindPayload.Read(parts[i].Kinds, parts[i].Name);
            payloads[i] = payload;
            if (payload == null)
                continue;
            if (catalog == null)
            {
                catalog = payload;
                catalogName = parts[i].Name;
            }
            else if (!catalog.SameCatalog(payload))
                throw new InvalidOperationException("Tl: timeline '" + parts[i].Name + "' was baked against different kinds than '" + catalogName + "'; rebake both after the compile.");
        }
        return payloads;
    }

    static KindCapacity[] Plan(IReadOnlyList<LibraryPart> parts, KindPayload?[] payloads, KindPayload catalog, LibrarySettings settings)
    {
        var kinds = catalog.KindCount;
        var overrides = settings.Kinds ?? new Dictionary<string, KindOverride>();
        foreach (var name in overrides.Keys)
        {
            var known = false;
            for (var k = 0; k < kinds && !known; k++)
                known = catalog.KindName(k) == name || catalog.GroupOf(k) == name;
            if (!known)
                throw new InvalidOperationException("Tl: library kinds: '" + name + "' is not a kind or kind group.");
        }
        var channels = new uint[parts.Count];
        var channelCount = Math.Max(0, settings.ChannelCount);
        for (var i = 0; i < parts.Count; i++)
        {
            channels[i] = ChannelOf(parts[i].Tlb);
            channelCount = Math.Max(channelCount, (int)channels[i]);
        }
        var rows = new List<KindCapacity>();
        var worst = new int[kinds];
        for (var k = 0; k < kinds; k++)
        {
            var name = catalog.KindName(k);
            overrides.TryGetValue(name, out var o);
            var gamePulses = o.GamePulses ?? catalog.GamePulses[k];
            var total = 0;
            for (var ch = 1; ch <= channelCount; ch++)
                total += Most(payloads, channels, (uint)ch, k, false);
            total += Most(payloads, channels, 0u, k, false) * settings.StackDepth;
            if (catalog.AcceptsPulses(k))
                total += Most(payloads, channels, uint.MaxValue, k, true) * (channelCount + settings.StackDepth) + gamePulses;
            worst[k] = total;
            var entry = catalog.EntryBytes(k);
            var capacity = o.Inline ?? (catalog.Inline[k] >= 0 ? catalog.Inline[k] : Math.Min(total, (o.MaxInlineBytes ?? settings.MaxInlineBytes) / entry));
            rows.Add(new KindCapacity(name, false, total, capacity, entry, SetBy(parts, payloads, k) ?? (gamePulses > 0 ? "gamePulses" : "-")));
        }
        var groups = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        for (var k = 0; k < kinds; k++)
        {
            var group = catalog.GroupOf(k);
            if (group == null)
                continue;
            if (!groups.TryGetValue(group, out var members))
                groups[group] = members = new List<int>();
            members.Add(k);
        }
        foreach (var pair in groups)
        {
            overrides.TryGetValue(pair.Key, out var o);
            var total = 0;
            var largest = 0;
            var names = new List<string>();
            foreach (var k in pair.Value)
            {
                total += worst[k];
                if (catalog.AcceptsPulses(k))
                    largest = Math.Max(largest, catalog.ValueBytes(k));
                names.Add(catalog.KindName(k));
            }
            var entry = 12 + ((largest + 3) & ~3);
            var capacity = o.Inline ?? Math.Min(total, (o.MaxInlineBytes ?? settings.MaxInlineBytes) / entry);
            rows.Add(new KindCapacity(pair.Key, true, total, capacity, entry, string.Join("+", names)));
        }
        return rows.ToArray();
    }

    static string? SetBy(IReadOnlyList<LibraryPart> parts, KindPayload?[] payloads, int kind)
    {
        string? name = null;
        var most = 0;
        for (var i = 0; i < payloads.Length; i++)
        {
            var payload = payloads[i];
            if (payload == null)
                continue;
            var count = payload.MaxSpans[kind] + payload.MaxPulses[kind];
            if (count > most)
            {
                most = count;
                name = parts[i].Name;
            }
        }
        return name;
    }

    static int Most(KindPayload?[] payloads, uint[] channels, uint channel, int kind, bool pulses)
    {
        var most = 0;
        for (var i = 0; i < payloads.Length; i++)
        {
            var payload = payloads[i];
            if (payload == null || channel != uint.MaxValue && channels[i] != channel)
                continue;
            most = Math.Max(most, pulses ? payload.MaxPulses[kind] : payload.MaxSpans[kind]);
        }
        return most;
    }

    static byte[] Write(KindPayload catalog, KindPayload?[] payloads, IReadOnlyList<KindCapacity> capacities)
    {
        var kinds = catalog.KindCount;
        var feeders = catalog.FeederCount;
        var count = payloads.Length;
        var feederTable = LibraryLayout.SectionHeaderBytes + LibraryLayout.KindRowBytes * kinds;
        var timelineTable = feederTable + LibraryLayout.FeederRowBytes * feeders;
        var dataStart = timelineTable + LibraryLayout.KindTimelineRowBytes * count;
        var dataLength = 0;
        foreach (var payload in payloads)
            if (payload != null && payload.KindMask != 0)
                dataLength += payload.Data.Length;
        var stringsAt = dataStart + dataLength;
        var section = new byte[(stringsAt + catalog.StringsLength + 7) & ~7];
        var span = section.AsSpan();
        Put(span, 0, LibraryLayout.SectionMagic);
        Put(span, 4, 1u);
        Put(span, 8, (uint)kinds);
        Put(span, 12, (uint)feeders);
        Put(span, 16, (uint)count);
        Put(span, 20, LibraryLayout.SectionHeaderBytes);
        Put(span, 24, (uint)feederTable);
        Put(span, 28, (uint)timelineTable);
        Put(span, 32, (uint)stringsAt);
        Put(span, 36, (uint)catalog.StringsLength);
        catalog.Catalog.CopyTo(span.Slice(LibraryLayout.SectionHeaderBytes));
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in capacities)
            if (row.Group)
                groups[row.Name] = row.Capacity;
        for (var k = 0; k < kinds; k++)
        {
            var row = LibraryLayout.SectionHeaderBytes + LibraryLayout.KindRowBytes * k;
            Put(span, row + 16, (uint)capacities[k].Capacity);
            var group = catalog.GroupOf(k);
            Put(span, row + 36, group == null ? 0u : (uint)groups[group]);
        }
        var cursor = dataStart;
        for (var i = 0; i < count; i++)
        {
            var payload = payloads[i];
            if (payload == null || payload.KindMask == 0)
                continue;
            var row = timelineTable + LibraryLayout.KindTimelineRowBytes * i;
            payload.Section.AsSpan(payload.TimelineRow, LibraryLayout.KindTimelineRowBytes).CopyTo(span.Slice(row));
            var delta = cursor - payload.DataStart;
            Shift(span, row + 8, delta);
            Shift(span, row + 16, delta);
            Shift(span, row + 20, delta);
            Shift(span, row + 24, delta);
            payload.Data.CopyTo(span.Slice(cursor));
            cursor += payload.Data.Length;
        }
        catalog.Strings.CopyTo(span.Slice(stringsAt));
        return section;
    }

    static void Shift(Span<byte> span, int at, int delta) => Put(span, at, (uint)((int)U32(span, at) + delta));

    static byte[] Emit(IReadOnlyList<LibraryPart> parts, byte[][] tlbs, uint tickRate, byte[]? kindSection)
    {
        var count = parts.Count;
        var unique = new List<byte[]>();
        var uniqueAt = new List<uint>();
        var index = new int[count];
        for (var i = 0; i < count; i++)
        {
            var found = -1;
            for (var u = 0; u < unique.Count && found < 0; u++)
                if (unique[u].AsSpan().SequenceEqual(tlbs[i]))
                    found = u;
            if (found < 0)
            {
                found = unique.Count;
                unique.Add(tlbs[i]);
                uniqueAt.Add(0);
            }
            index[i] = found;
        }
        var names = new byte[count][];
        var strings = 0u;
        for (var i = 0; i < count; i++)
        {
            names[i] = Encoding.UTF8.GetBytes(parts[i].Name);
            strings += (uint)names[i].Length;
        }
        var entries = (uint)count * LibraryLayout.ManifestRowBytes;
        var blobAt = (LibraryLayout.ManifestAt + entries + strings + 7u) & ~7u;
        var cursor = blobAt;
        for (var u = 0; u < unique.Count; u++)
        {
            uniqueAt[u] = cursor;
            cursor += (uint)((unique[u].Length + 7) & ~7);
        }
        var kindAt = kindSection == null ? 0u : cursor;
        if (kindSection != null)
            cursor += (uint)kindSection.Length;
        var total = cursor + LibraryLayout.DigestBytes;
        var bytes = new byte[total];
        var span = bytes.AsSpan();
        Put(span, 0, LibraryLayout.LibraryMagic);
        Put(span, 4, kindSection == null ? 1u : 2u);
        Put(span, 8, (uint)count);
        Put(span, 12, LibraryLayout.ManifestAt);
        Put(span, 16, blobAt);
        Put(span, 20, total);
        Put(span, 24, tickRate);
        Put(span, 28, kindAt);
        Put(span, 32, kindSection == null ? 0u : (uint)kindSection.Length);
        var nameAt = LibraryLayout.ManifestAt + entries;
        for (var i = 0; i < count; i++)
        {
            var at = (int)(LibraryLayout.ManifestAt + (uint)i * LibraryLayout.ManifestRowBytes);
            Put(span, at, (uint)(i + 1));
            Put(span, at + 4, ChannelOf(tlbs[i]));
            Put(span, at + 8, nameAt);
            Put(span, at + 12, (uint)names[i].Length);
            Put(span, at + 16, uniqueAt[index[i]]);
            Put(span, at + 20, (uint)tlbs[i].Length);
            names[i].CopyTo(bytes, (int)nameAt);
            nameAt += (uint)names[i].Length;
        }
        for (var u = 0; u < unique.Count; u++)
            unique[u].CopyTo(bytes, (int)uniqueAt[u]);
        kindSection?.CopyTo(bytes, (int)kindAt);
        using (var sha = SHA256.Create())
            sha.ComputeHash(bytes, 0, (int)(total - LibraryLayout.DigestBytes)).CopyTo(bytes, (int)(total - LibraryLayout.DigestBytes));
        return bytes;
    }

    static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at));

    static void Put(Span<byte> bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(at), value);
}
