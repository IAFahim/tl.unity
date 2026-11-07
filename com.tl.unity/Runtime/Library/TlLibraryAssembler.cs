using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Tl.Unity
{
    public struct TlLibraryPart
    {
        public string Name;
        public byte[] Tlb;
        public byte[] Kinds;
        public ushort Next;
        public uint[] Refs;
    }

    public struct TlLibrarySettings
    {
        public int ChannelCount;
        public int StackDepth;
        public int MaxInlineBytes;

        public static TlLibrarySettings Default => new TlLibrarySettings { StackDepth = 1, MaxInlineBytes = 64 };
    }

    public static class TlLibraryAssembler
    {
        const uint LibraryMagic = 0x42494C54u;
        const uint SectionMagic = 0x444E494Bu;
        const uint TlbVersionWithHeader = 6u;
        const int TlbHeaderBytes = 80;
        const int TlbNextAt = 52;
        const int TlbChannelAt = 56;
        const uint ManifestAt = 64;
        const uint ManifestRowBytes = 32;
        const int DigestBytes = 32;
        const int KindRowBytes = 40;
        const int FeederRowBytes = 32;
        const int TimelineRowBytes = 48;
        const int SectionHeaderBytes = 64;

        public static byte[] Assemble(IReadOnlyList<TlLibraryPart> parts, uint tickRate, TlLibrarySettings settings)
        {
            var tlbs = new byte[parts.Count][];
            for (var i = 0; i < parts.Count; i++)
                tlbs[i] = Patched(parts[i], parts.Count);
            return Emit(parts, tlbs, tickRate, KindSection(parts, tlbs, settings));
        }

        public static uint ChannelOf(byte[] tlb)
        {
            return tlb.Length >= TlbHeaderBytes && U32(tlb, 4) == TlbVersionWithHeader ? U32(tlb, TlbChannelAt) : 0u;
        }

        public static uint NextOf(byte[] tlb)
        {
            return tlb.Length >= TlbHeaderBytes && U32(tlb, 4) == TlbVersionWithHeader ? U32(tlb, TlbNextAt) : 0u;
        }

        static byte[] Patched(TlLibraryPart part, int count)
        {
            if (part.Tlb == null || part.Tlb.Length == 0)
                throw new InvalidOperationException("Tl: timeline '" + part.Name + "' has no TLB bytes.");
            var tlb = (byte[])part.Tlb.Clone();
            if (tlb.Length >= TlbHeaderBytes && U32(tlb, 4) == TlbVersionWithHeader && U32(tlb, TlbNextAt) != 0)
                BinaryPrimitives.WriteUInt32LittleEndian(tlb.AsSpan(TlbNextAt), part.Next <= count ? part.Next : 0u);
            if (part.Refs != null)
                for (var i = 0; i + 1 < part.Refs.Length; i += 2)
                {
                    var at = (int)part.Refs[i];
                    if (at < 0 || at + 2 > tlb.Length)
                        throw new InvalidOperationException("Tl: timeline '" + part.Name + "' has a timeline reference outside its data; rebake it.");
                    BinaryPrimitives.WriteUInt16LittleEndian(tlb.AsSpan(at), part.Refs[i + 1] <= count ? (ushort)part.Refs[i + 1] : (ushort)0);
                }
            return tlb;
        }

        static byte[] KindSection(IReadOnlyList<TlLibraryPart> parts, byte[][] tlbs, TlLibrarySettings settings)
        {
            var payloads = new TlKindPayload[parts.Count];
            TlKindPayload first = null;
            var firstName = "";
            var reached = false;
            for (var i = 0; i < parts.Count; i++)
            {
                var payload = TlKindPayload.Read(parts[i].Kinds, parts[i].Name);
                payloads[i] = payload;
                if (payload == null)
                    continue;
                if (first == null)
                {
                    first = payload;
                    firstName = parts[i].Name;
                }
                else if (!first.SameCatalog(payload))
                    throw new InvalidOperationException("Tl: timeline '" + parts[i].Name + "' was baked against different kinds than '" + firstName + "'; rebake both after the compile.");
                reached |= payload.KindMask != 0;
            }
            if (!reached)
                return null;
            var channels = new uint[parts.Count];
            var channelCount = Math.Max(0, settings.ChannelCount);
            for (var i = 0; i < parts.Count; i++)
            {
                channels[i] = ChannelOf(tlbs[i]);
                channelCount = Math.Max(channelCount, (int)channels[i]);
            }
            var capacities = Capacities(first, payloads, channels, channelCount, settings, out var groupCapacities);
            return Write(first, payloads, capacities, groupCapacities);
        }

        static int[] Capacities(TlKindPayload catalog, TlKindPayload[] payloads, uint[] channels, int channelCount, TlLibrarySettings settings, out Dictionary<string, int> groups)
        {
            var section = catalog.Section;
            var kinds = catalog.KindCount;
            var worst = new int[kinds];
            var capacities = new int[kinds];
            var maxInline = settings.MaxInlineBytes;
            for (var k = 0; k < kinds; k++)
            {
                var row = SectionHeaderBytes + KindRowBytes * k;
                var entryBytes = (int)U32(section, row + 12);
                var accepts = section[row + 20];
                var total = 0;
                for (var ch = 1; ch <= channelCount; ch++)
                    total += Most(payloads, channels, (uint)ch, k, false);
                total += Most(payloads, channels, 0u, k, false) * settings.StackDepth;
                if (accepts != 0)
                    total += Most(payloads, channels, uint.MaxValue, k, true) * (channelCount + settings.StackDepth) + catalog.GamePulses[k];
                worst[k] = total;
                capacities[k] = catalog.Inline[k] >= 0 ? catalog.Inline[k] : Math.Min(total, maxInline / entryBytes);
            }
            groups = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new List<string>();
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            var largest = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var k = 0; k < kinds; k++)
            {
                var group = GroupOf(catalog, k);
                if (group == null)
                    continue;
                var row = SectionHeaderBytes + KindRowBytes * k;
                var value = section[row + 20] == 0 ? 0 : (int)U32(section, row + 8);
                if (!totals.ContainsKey(group))
                {
                    names.Add(group);
                    totals[group] = 0;
                    largest[group] = 0;
                }
                totals[group] += worst[k];
                largest[group] = Math.Max(largest[group], value);
            }
            foreach (var group in names)
            {
                var entry = 12 + ((largest[group] + 3) & ~3);
                groups[group] = Math.Min(totals[group], maxInline / entry);
            }
            return capacities;
        }

        static int Most(TlKindPayload[] payloads, uint[] channels, uint channel, int kind, bool pulses)
        {
            var most = 0;
            for (var i = 0; i < payloads.Length; i++)
            {
                if (payloads[i] == null || channel != uint.MaxValue && channels[i] != channel)
                    continue;
                most = Math.Max(most, pulses ? payloads[i].MaxPulses[kind] : payloads[i].MaxSpans[kind]);
            }
            return most;
        }

        static string GroupOf(TlKindPayload catalog, int kind)
        {
            var row = SectionHeaderBytes + KindRowBytes * kind;
            var length = (int)U32(catalog.Section, row + 32);
            if (length == 0)
                return null;
            return Encoding.UTF8.GetString(catalog.Section, catalog.StringsAt + (int)U32(catalog.Section, row + 28), length);
        }

        static byte[] Write(TlKindPayload catalog, TlKindPayload[] payloads, int[] capacities, Dictionary<string, int> groups)
        {
            var kinds = catalog.KindCount;
            var feeders = catalog.FeederCount;
            var count = payloads.Length;
            var feederTable = SectionHeaderBytes + KindRowBytes * kinds;
            var timelineTable = feederTable + FeederRowBytes * feeders;
            var dataStart = timelineTable + TimelineRowBytes * count;
            var dataLength = 0;
            foreach (var payload in payloads)
                if (payload != null && payload.KindMask != 0)
                    dataLength += payload.Data.Length;
            var stringsAt = dataStart + dataLength;
            var section = new byte[(stringsAt + catalog.StringsLength + 7) & ~7];
            var span = section.AsSpan();
            Put(span, 0, SectionMagic);
            Put(span, 4, 1u);
            Put(span, 8, (uint)kinds);
            Put(span, 12, (uint)feeders);
            Put(span, 16, (uint)count);
            Put(span, 20, SectionHeaderBytes);
            Put(span, 24, (uint)feederTable);
            Put(span, 28, (uint)timelineTable);
            Put(span, 32, (uint)stringsAt);
            Put(span, 36, (uint)catalog.StringsLength);
            catalog.Catalog.CopyTo(span.Slice(SectionHeaderBytes));
            for (var k = 0; k < kinds; k++)
            {
                var row = SectionHeaderBytes + KindRowBytes * k;
                Put(span, row + 16, (uint)capacities[k]);
                var group = GroupOf(catalog, k);
                Put(span, row + 36, group == null ? 0u : (uint)groups[group]);
            }
            var cursor = dataStart;
            for (var i = 0; i < count; i++)
            {
                var payload = payloads[i];
                if (payload == null || payload.KindMask == 0)
                    continue;
                var row = timelineTable + TimelineRowBytes * i;
                payload.Section.AsSpan(payload.TimelineRow, TimelineRowBytes).CopyTo(span.Slice(row));
                var delta = cursor - payload.DataStart;
                foreach (var field in new[] { 8, 16, 20, 24 })
                    Put(span, row + field, (uint)((int)U32(section, row + field) + delta));
                payload.Data.CopyTo(span.Slice(cursor));
                cursor += payload.Data.Length;
            }
            catalog.Strings.CopyTo(span.Slice(stringsAt));
            return section;
        }

        static byte[] Emit(IReadOnlyList<TlLibraryPart> parts, byte[][] tlbs, uint tickRate, byte[] kindSection)
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
            var entries = (uint)count * ManifestRowBytes;
            var blobAt = (ManifestAt + entries + strings + 7u) & ~7u;
            var cursor = blobAt;
            for (var u = 0; u < unique.Count; u++)
            {
                uniqueAt[u] = cursor;
                cursor += (uint)((unique[u].Length + 7) & ~7);
            }
            var kindAt = kindSection == null ? 0u : cursor;
            if (kindSection != null)
                cursor += (uint)kindSection.Length;
            var total = cursor + DigestBytes;
            var bytes = new byte[total];
            var span = bytes.AsSpan();
            Put(span, 0, LibraryMagic);
            Put(span, 4, kindSection == null ? 1u : 2u);
            Put(span, 8, (uint)count);
            Put(span, 12, ManifestAt);
            Put(span, 16, blobAt);
            Put(span, 20, total);
            Put(span, 24, tickRate);
            Put(span, 28, kindAt);
            Put(span, 32, kindSection == null ? 0u : (uint)kindSection.Length);
            var nameAt = ManifestAt + entries;
            for (var i = 0; i < count; i++)
            {
                var at = (int)(ManifestAt + (uint)i * ManifestRowBytes);
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
                sha.ComputeHash(bytes, 0, (int)(total - DigestBytes)).CopyTo(bytes, (int)(total - DigestBytes));
            return bytes;
        }

        static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));

        static void Put(Span<byte> bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(at), value);
    }
}
