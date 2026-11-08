using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Tl.Gen.Tlb;

public static class TimelineJson
{
    public static string Export(TypedTimeline timeline, LibraryManifest? manifest = null)
        => Encode(Read(TimelineBaker.BakeTyped(timeline, manifest: manifest), manifest: manifest), manifest);

    static string Encode(TypedTimeline timeline, LibraryManifest? manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            if (timeline.Name != null) writer.WriteString("name", timeline.Name);
            writer.WriteNumber("duration", timeline.Duration);
            if (timeline.Loops) writer.WriteBoolean("loop", true);
            if (timeline.Hold) writer.WriteBoolean("hold", true);
            if (timeline.Next != null) writer.WriteString("next", timeline.Next);
            if (timeline.Channel != null) writer.WriteString("channel", timeline.Channel);
            writer.WriteStartArray("tracks");
            foreach (var track in timeline.Tracks)
            {
                writer.WriteStartObject();
                if (track.Name != null) writer.WriteString("name", track.Name);
                WriteType(writer, track.Type);
                writer.WritePropertyName("data");
                WriteBytes(writer, track.Type, track.Data, manifest);
                writer.WriteStartArray("clips");
                foreach (var clip in track.Clips)
                {
                    writer.WriteStartObject();
                    if (clip.Name != null) writer.WriteString("name", clip.Name);
                    WriteType(writer, clip.Type);
                    writer.WriteNumber("start", clip.Start);
                    writer.WriteNumber("end", clip.End);
                    writer.WritePropertyName("data");
                    WriteBytes(writer, clip.Type, clip.Data, manifest);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (timeline.Markers.Count > 0)
            {
                writer.WriteStartArray("markers");
                foreach (var marker in timeline.Markers)
                {
                    writer.WriteStartObject();
                    WriteType(writer, marker.Type);
                    writer.WriteNumber("tick", marker.Tick);
                    writer.WritePropertyName("data");
                    WriteBytes(writer, marker.Type, marker.Data, manifest);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    public static string Decode(byte[] tlb, BakerAssemblyResolver? resolver = null, LibraryManifest? manifest = null)
    {
        var timeline = Read(tlb, resolver, manifest);
        if (manifest == null && (timeline.Next != null || timeline.Channel != null))
        {
            manifest = new LibraryManifest();
            if (timeline.Next != null) manifest.Timelines[timeline.Next] = (ushort)Word(tlb, 52);
            if (timeline.Channel != null) manifest.Channels[timeline.Channel] = (ushort)Word(tlb, 56);
        }
        var json = Encode(timeline, manifest);
        if (!TimelineBaker.BakeJson(json, resolver, manifest: manifest).AsSpan().SequenceEqual(tlb))
            throw new BakeDiagnosticException("decode cannot represent this TLB exactly as JSON; use its authoring source and original payload layout.");
        return json;
    }

    public static TypedTimeline Read(byte[] tlb, BakerAssemblyResolver? resolver = null, LibraryManifest? manifest = null)
    {
        TimelineRef.Validate(tlb);
        if (Word(tlb, 4) != 6) throw new BakeDiagnosticException("decode needs TLB format 6.");
        if (!TlbMetadata.HasMetadata(tlb)) throw new BakeDiagnosticException("decode needs the TLB metadata tail; a stripped TLB cannot be decoded.");
        TlbMetadataView metadata;
        try { metadata = TlbMetadata.Read(tlb); }
        catch (ArgumentException error) { throw new BakeDiagnosticException("decode: " + error.Message); }
        var active = resolver ?? new BakerAssemblyResolver();
        var types = metadata.Types.Select(t => active.ResolveType(t.Namespace, t.Name, null, "decode")).ToArray();
        var timeline = new TypedTimeline
        {
            Duration = (uint)Word(tlb, 12), Loops = Word(tlb, 8) != 0, Hold = Word(tlb, 60) != 0,
            Name = Label(-1, -1),
            Next = Reference(Label(-3, -1), manifest?.Timelines, Word(tlb, 52), "timeline"),
            Channel = Reference(Label(-2, -1), manifest?.Channels, Word(tlb, 56), "channel"),
        };
        var pairs = new (Type Track, Type Clip)[Word(tlb, 24)];
        for (var i = 0; i < pairs.Length; i++)
        {
            var (track, clip) = (types[metadata.PairTypes[i].TrackType], types[metadata.PairTypes[i].ClipType]);
            var at = Word(tlb, 28) + i * 48;
            if (Keying.Of(Keying.StableName(track) + "\0" + Keying.StableName(clip)) != BinaryPrimitives.ReadUInt64LittleEndian(tlb.AsSpan(at)))
                throw new BakeDiagnosticException("decode type arguments do not match the baked pair key.");
            if (TimelineBakerFast.HelperFor(track).Size != Word(tlb, at + 20) || TimelineBakerFast.HelperFor(clip).Size != Word(tlb, at + 32))
                throw new BakeDiagnosticException("decode payload layout changed; use the original pack assembly.");
            pairs[i] = (track, clip);
        }
        if (metadata.Tracks.Count != Word(tlb, 16)) throw new BakeDiagnosticException("decode metadata track rows do not match the TLB track count.");
        var next = 0;
        for (var ti = 0; ti < metadata.Tracks.Count; ti++)
        {
            var row = metadata.Tracks[ti];
            var type = types[row.Type];
            if (row.Pair >= 0 && pairs[row.Pair].Track != type) throw new BakeDiagnosticException($"decode metadata track {ti} names a pair of another track type.");
            var track = new TypedTrack { Name = Label(ti, -1), Type = type, Data = row.Pair < 0 ? null : Value(row.Pair, true, row.Value, $"track {ti}") };
            for (var ci = 0; ci < row.Clips; ci++)
            {
                var clip = metadata.Clips[next++];
                if (pairs[clip.Pair].Track != type) throw new BakeDiagnosticException($"decode metadata clip {ci} on track {ti} names a pair of another track type.");
                if (clip.End > timeline.Duration) throw new BakeDiagnosticException($"decode metadata clip {ci} on track {ti} ends after the timeline.");
                track.Clips.Add(new TypedClip { Name = Label(ti, ci), Type = pairs[clip.Pair].Clip, Start = clip.Start, End = clip.End, Data = Value(clip.Pair, false, clip.Value, $"clip {ci} on track {ti}") });
            }
            timeline.Tracks.Add(track);
        }
        var markerAt = Word(tlb, 64);
        for (var i = 0; i < Word(tlb, 68); i++)
        {
            var key = BinaryPrimitives.ReadUInt64LittleEndian(tlb.AsSpan(markerAt + 8));
            var type = types.FirstOrDefault(t => Keying.Of(Keying.StableName(t)) == key);
            if (type == null) throw new BakeDiagnosticException("decode metadata does not name marker type " + key + ".");
            var size = Word(tlb, markerAt + 4);
            timeline.Markers.Add(new TypedMarker { Type = type, Tick = (uint)Word(tlb, markerAt), Data = Canonical(type, tlb.AsSpan(markerAt + 16, size).ToArray(), manifest, $"marker {i}") });
            markerAt += 16 + ((size + 3) & ~3);
        }
        return timeline;

        string? Label(int track, int clip) => metadata.Labels.FirstOrDefault(l => l.TrackEntry == track && l.ClipIndex == clip).Name;
        byte[] Value(int pair, bool track, int index, string context)
        {
            var at = Word(tlb, 28) + 48 * pair;
            var offset = track ? 12 : 24;
            if (index >= Word(tlb, at + offset + 4)) throw new BakeDiagnosticException($"decode metadata {context} names a value beyond its pool.");
            var size = Word(tlb, at + offset + 8);
            return Canonical(track ? pairs[pair].Track : pairs[pair].Clip, tlb.AsSpan(at + Word(tlb, at + offset) + index * size, size).ToArray(), manifest, context);
        }
    }

    static unsafe byte[] Canonical(Type type, byte[] bytes, LibraryManifest? manifest, string context)
    {
        var helper = TimelineBakerFast.HelperFor(type);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            fixed (byte* source = bytes) ValueJson.Write(writer, type, helper.Read(source), manifest);
        using var document = JsonDocument.Parse(stream.ToArray());
        var copy = new byte[helper.Size];
        fixed (byte* target = copy) helper.CopyTo(target, ValueJson.Read(type, document.RootElement, context, manifest));
        var at = 0;
        while (at < copy.Length && copy[at] == bytes[at]) at++;
        if (at < copy.Length)
            throw new BakeDiagnosticException($"decode cannot write {context} ({type.Name}) as JSON: byte {at} does not survive a field round trip (nonzero struct padding or a value JSON cannot hold); rebake it from its authoring source.");
        return bytes;
    }

    static string? Reference(string? name, Dictionary<string, ushort>? values, int id, string kind)
    {
        if (id == 0 && name == null) return null;
        if (id == 0 || name == null) throw new BakeDiagnosticException($"decode metadata and header disagree about the {kind}.");
        if (values != null && (!values.TryGetValue(name, out var known) || known != id))
            throw new BakeDiagnosticException($"decode {kind} '{name}' has id {id} in this TLB but not in the given manifest.");
        return name;
    }

    static void WriteType(Utf8JsonWriter writer, Type type)
    {
        writer.WriteString("namespace", type.Namespace ?? "");
        writer.WriteString("type", TypeNames.JsonName(type));
    }

    static unsafe void WriteBytes(Utf8JsonWriter writer, Type type, byte[]? bytes, LibraryManifest? manifest)
    {
        var helper = TimelineBakerFast.HelperFor(type);
        bytes ??= new byte[helper.Size];
        if (bytes.Length != helper.Size) throw new BakeDiagnosticException($"wrong data size for {type.Name}.");
        fixed (byte* source = bytes) ValueJson.Write(writer, type, helper.Read(source), manifest);
    }

    static int Word(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
    static int Half(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
}
