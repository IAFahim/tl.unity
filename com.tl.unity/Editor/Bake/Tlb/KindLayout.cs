using System.Buffers.Binary;
using System.Text;

namespace Tl.Gen.Tlb;

internal enum KindEventOp : byte { Remove, Insert, PulseStart, PulseEnd, PulseMarker }

internal readonly record struct KindWindow(ushort Track, ushort Pair, ushort Start, ushort End, uint Slot, bool Marker);

internal readonly record struct KindEvent(ushort Window, ushort Feeder, KindEventOp Op);

internal sealed class KindTimeline
{
    public string Name = "";
    public ushort Duration;
    public uint Channel;
    public ulong KindMask;
    public readonly List<KindWindow> Windows = new();
    public readonly List<(ushort Window, ushort Feeder)> Entries = new();
    public byte[] EventBits = [];
    public uint[] EventIndex = [];
    public readonly List<KindEvent> Events = new();
    public readonly List<KindEvent> WrapForward = new();
    public readonly List<KindEvent> WrapBackward = new();
    public int[] MaxSpans = [];
    public int[] MaxPulses = [];

    public static KindTimeline Analyze(string name, ReadOnlySpan<byte> tlb, KindCatalog catalog)
    {
        var t = new KindTimeline { Name = name };
        var version = U32(tlb, 4);
        t.Duration = (ushort)U32(tlb, 12);
        var stageCount = (int)U32(tlb, 20);
        var pairCount = (int)U32(tlb, 24);
        var pairOffset = (int)U32(tlb, 28);
        var stageOffset = (int)U32(tlb, 32);
        t.Channel = version >= 6 ? U32(tlb, 56) : 0;
        var pairKeys = new ulong[pairCount];
        for (var p = 0; p < pairCount; p++) pairKeys[p] = BinaryPrimitives.ReadUInt64LittleEndian(tlb[(pairOffset + 48 * p)..]);
        var seen = new Dictionary<(byte, int, uint, uint), int>();
        for (var s = 0; s < stageCount; s++)
        {
            var stage = stageOffset + 16 * s;
            var program = (int)U32(tlb, stage + 8);
            var count = (int)U32(tlb, stage + 12);
            for (var k = 0; k < count; k++)
            {
                var slot = U32(tlb, program + 8 * k);
                var pair = (int)U32(tlb, program + 8 * k + 4);
                var track = tlb[(int)slot + 6];
                var start = U32(tlb, (int)slot + 8);
                var end = U32(tlb, (int)slot + 12);
                if (seen.ContainsKey((track, pair, start, end))) continue;
                seen[(track, pair, start, end)] = t.Windows.Count;
                t.Windows.Add(new KindWindow(track, (ushort)pair, (ushort)start, (ushort)end, slot, false));
            }
        }
        var slotWindows = t.Windows.Count;
        if (version >= 6)
        {
            var markerOffset = (int)U32(tlb, 64);
            var markerCount = (int)U32(tlb, 68);
            var at = markerOffset;
            for (var m = 0; m < markerCount; m++)
            {
                var tick = U32(tlb, at);
                var size = (int)U32(tlb, at + 4);
                var key = BinaryPrimitives.ReadUInt64LittleEndian(tlb[(at + 8)..]);
                at += 16 + ((size + 3) & ~3);
                if (!catalog.Feeders.Any(f => f.Trigger == FeederTrigger.Marker && f.SourceKey == key)) continue;
                t.Windows.Add(new KindWindow(0, (ushort)m, (ushort)tick, (ushort)(tick + 1), 0, true));
                for (var f = 0; f < catalog.Feeders.Count; f++)
                    if (catalog.Feeders[f].Trigger == FeederTrigger.Marker && catalog.Feeders[f].SourceKey == key) t.Entries.Add(((ushort)(t.Windows.Count - 1), (ushort)f));
            }
        }
        var entries = new List<(ushort, ushort)>();
        for (var w = 0; w < slotWindows; w++)
            for (var f = 0; f < catalog.Feeders.Count; f++)
            {
                var feeder = catalog.Feeders[f];
                if (feeder.Trigger != FeederTrigger.Marker && feeder.SourceKey == pairKeys[t.Windows[w].Pair]) entries.Add(((ushort)w, (ushort)f));
            }
        t.Entries.InsertRange(0, entries);
        if (t.Windows.Count > ushort.MaxValue) throw KindCatalog.Bad($"timeline '{name}': over 65,535 kind windows.");
        foreach (var (_, f) in t.Entries) t.KindMask |= 1ul << catalog.Feeders[f].Kind;
        t.Build(catalog);
        return t;
    }

    void Build(KindCatalog catalog)
    {
        int d = Duration, kinds = catalog.Kinds.Count;
        EventBits = new byte[d];
        EventIndex = new uint[d * 2 + 1];
        MaxSpans = new int[kinds];
        MaxPulses = new int[kinds];
        for (var tick = 0; tick < d; tick++)
        {
            var spans = new int[kinds];
            var pulses = new int[kinds];
            for (var dir = 0; dir < 2; dir++)
            {
                var first = (uint)Events.Count;
                EventIndex[tick * 2 + dir] = first;
                var groups = new[] { new List<KindEvent>(), new List<KindEvent>(), new List<KindEvent>() };
                foreach (var (w, f) in Entries)
                {
                    var x = Windows[w];
                    var feeder = catalog.Feeders[f];
                    if (feeder.Trigger == FeederTrigger.Span)
                    {
                        if (dir == 0 && x.Start <= tick && tick < x.End) spans[feeder.Kind]++;
                        if (dir == 0 ? x.End == tick : x.Start == tick + 1) groups[0].Add(new(w, f, KindEventOp.Remove));
                        if (dir == 0 ? x.Start == tick : x.End - 1 == tick) groups[1].Add(new(w, f, KindEventOp.Insert));
                    }
                    else if (Fires(feeder.Trigger, x, tick) is { } op)
                    {
                        if (dir == 0) pulses[feeder.Kind]++;
                        groups[2].Add(new(w, f, op));
                    }
                }
                foreach (var g in groups) Events.AddRange(g);
                if (Events.Count != first) EventBits[tick] |= (byte)(1 << dir);
            }
            for (var k = 0; k < kinds; k++)
            {
                MaxSpans[k] = Math.Max(MaxSpans[k], spans[k]);
                MaxPulses[k] = Math.Max(MaxPulses[k], pulses[k]);
            }
        }
        EventIndex[d * 2] = (uint)Events.Count;
        foreach (var (w, f) in Entries)
        {
            if (catalog.Feeders[f].Trigger != FeederTrigger.Span) continue;
            if (Windows[w].End == d) WrapForward.Add(new KindEvent(w, f, KindEventOp.Remove));
            if (Windows[w].Start == 0) WrapBackward.Add(new KindEvent(w, f, KindEventOp.Remove));
        }
    }

    static KindEventOp? Fires(FeederTrigger trigger, KindWindow window, int tick) => trigger switch
    {
        FeederTrigger.Start when window.Start == tick => KindEventOp.PulseStart,
        FeederTrigger.End when window.End - 1 == tick => KindEventOp.PulseEnd,
        FeederTrigger.Marker when window.Start == tick => KindEventOp.PulseMarker,
        _ => null,
    };

    static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
}

internal static class KindLayout
{
    public const uint SectionMagic = 0x444E494Bu;

    public static byte[] Payload(KindCatalog catalog, KindTimeline timeline, IReadOnlyList<int> inline, IReadOnlyList<int> gamePulses)
        => KindPayload.Write(timeline.MaxSpans, timeline.MaxPulses, inline, gamePulses, Section(catalog, [timeline]));

    public static string Report(IReadOnlyList<KindCapacity> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.Append($"kind {(r.Group ? "group " : "")}{r.Name}: worst {r.Worst}, capacity {r.Capacity}, entry {r.EntryBytes} B, inline {r.Capacity * r.EntryBytes} B, set by {r.SetBy}\n");
        return sb.ToString();
    }

    public static string CapacitySource(string ns, IReadOnlyList<KindCapacity> rows)
    {
        var sb = new StringBuilder();
        sb.Append("// <auto-generated by tlb library; do not edit>\n");
        sb.Append("namespace ").Append(ns).Append("\n{\n");
        sb.Append("    public static class KindCapacity\n    {\n");
        foreach (var r in rows.OrderBy(r => r.Name, StringComparer.Ordinal))
            sb.Append("        public const int ").Append(r.Name).Append(" = ").Append(r.Capacity).Append(";\n");
        sb.Append("    }\n}\n");
        return sb.ToString();
    }

    public static bool WriteIfChanged(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, bytes);
        return true;
    }

    static byte[] Section(KindCatalog catalog, IReadOnlyList<KindTimeline> timelines)
    {
        var strings = new MemoryStream();
        (uint, uint) Str(string? text)
        {
            if (string.IsNullOrEmpty(text)) return (0, 0);
            var b = Encoding.UTF8.GetBytes(text);
            var at = (uint)strings.Length;
            strings.Write(b);
            return (at, (uint)b.Length);
        }
        var feederTable = 64 + 40 * catalog.Kinds.Count;
        var timelineTable = feederTable + 32 * catalog.Feeders.Count;
        var dataStart = timelineTable + 48 * timelines.Count;
        var main = new BinaryWriter(new MemoryStream());
        var data = new BinaryWriter(new MemoryStream());
        foreach (var v in new[] { SectionMagic, 1u, (uint)catalog.Kinds.Count, (uint)catalog.Feeders.Count, (uint)timelines.Count, 64u, (uint)feederTable, (uint)timelineTable }) main.Write(v);
        main.Write(new byte[32]);
        foreach (var k in catalog.Kinds)
        {
            var (no, nl) = Str(k.Name);
            var (go, gl) = Str(k.Group);
            foreach (var v in new[] { no, nl, (uint)k.ValueBytes, (uint)k.EntryBytes, 0u }) main.Write(v);
            main.Write((byte)k.Accepts);
            main.Write((byte)k.Mode);
            main.Write((ushort)(k.Folds ? 1 : 0));
            foreach (var v in new[] { (uint)k.Order, go, gl, 0u }) main.Write(v);
        }
        foreach (var f in catalog.Feeders)
        {
            var (co, cl) = Str(f.Consumer);
            var (mo, ml) = Str(f.Method);
            main.Write((ushort)f.Kind);
            main.Write((byte)f.Trigger);
            main.Write(f.Ordinal);
            main.Write(0u);
            main.Write(f.SourceKey);
            foreach (var v in new[] { co, cl, mo, ml }) main.Write(v);
        }
        uint At()
        {
            while (data.BaseStream.Length % 8 != 0) data.Write((byte)0);
            return (uint)(dataStart + data.BaseStream.Length);
        }
        foreach (var t in timelines)
        {
            if (t.KindMask == 0)
            {
                main.Write(new byte[48]);
                continue;
            }
            var windows = At();
            foreach (var w in t.Windows)
            {
                foreach (var v in new[] { w.Track, w.Pair, w.Start, w.End }) data.Write(v);
                data.Write(w.Slot);
                data.Write(w.Marker ? 1u : 0u);
            }
            var bits = At();
            data.Write(t.EventBits);
            var index = At();
            foreach (var i in t.EventIndex) data.Write(i);
            var events = At();
            foreach (var e in t.Events.Concat(t.WrapForward).Concat(t.WrapBackward))
            {
                data.Write(e.Window);
                data.Write(e.Feeder);
                data.Write((uint)e.Op);
            }
            var n = (uint)t.Events.Count;
            var f = (uint)t.WrapForward.Count;
            main.Write(t.KindMask);
            foreach (var v in new[] { windows, (uint)t.Windows.Count, bits, index, events, n, n, f, n + f, (uint)t.WrapBackward.Count }) main.Write(v);
        }
        At();
        var stringsAt = (uint)(dataStart + data.BaseStream.Length);
        var section = new byte[(stringsAt + strings.Length + 7) & ~7];
        ((MemoryStream)main.BaseStream).ToArray().CopyTo(section, 0);
        ((MemoryStream)data.BaseStream).ToArray().CopyTo(section, dataStart);
        strings.ToArray().CopyTo(section, stringsAt);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(32), stringsAt);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(36), (uint)strings.Length);
        return section;
    }
}
