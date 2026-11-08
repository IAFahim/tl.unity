using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Tl.Gen.Tlb;

namespace Tl.Unity.Bake.Tests
{
    sealed class KindGameFixture
    {
        public readonly List<string> Names = new List<string>();
        public readonly List<string> Channels = new List<string>();
        public readonly List<TypedTimeline> Timelines = new List<TypedTimeline>();
        public LibrarySettings Settings = LibrarySettings.Default;
        public uint TickRate = 60;
        public LibraryManifest Manifest = null!;

        public static string Folder => Path.GetFullPath("Packages/com.tl.unity/Tests/Kinds/Timelines~");

        public static Assembly Game => typeof(KindGame.WalkTrack).Assembly;

        public static KindGameFixture Load()
        {
            var fixture = new KindGameFixture();
            using var library = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Folder, "library.json")));
            var root = library.RootElement;
            if (root.TryGetProperty("tickRate", out var rate)) fixture.TickRate = rate.GetUInt32();
            foreach (var channel in root.GetProperty("channels").EnumerateArray()) fixture.Channels.Add(channel.GetString()!);
            var overrides = new Dictionary<string, KindOverride>(StringComparer.Ordinal);
            fixture.Settings.Kinds = overrides;
            if (root.TryGetProperty("kinds", out var kinds))
                foreach (var kind in kinds.EnumerateObject())
                {
                    int? inline = null, maxInline = null, pulses = null;
                    foreach (var field in kind.Value.EnumerateObject())
                    {
                        if (field.Name == "inline") inline = field.Value.GetInt32();
                        else if (field.Name == "gamePulses") pulses = field.Value.GetInt32();
                        else if (field.Name == "maxInlineBytes") maxInline = field.Value.GetInt32();
                        else throw new InvalidOperationException("unknown kinds key " + field.Name);
                    }
                    overrides[kind.Name] = new KindOverride(inline, pulses, maxInline);
                }
            var docs = new List<(string Name, byte[] Json)>();
            foreach (var file in root.GetProperty("timelines").EnumerateArray())
            {
                var json = File.ReadAllBytes(Path.Combine(Folder, file.GetString()!));
                using var doc = JsonDocument.Parse(json);
                docs.Add((doc.RootElement.GetProperty("name").GetString()!, json));
            }
            docs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            fixture.Names.AddRange(docs.Select(d => d.Name));
            fixture.Manifest = LibraryBake.Manifest(fixture.Names, fixture.Channels, fixture.TickRate);
            foreach (var (_, json) in docs) fixture.Timelines.Add(Typed(json, fixture.Names));
            return fixture;
        }

        public static Dictionary<string, byte[]> TlibTimelines(byte[] tlib)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var count = (int)BitConverter.ToUInt32(tlib, 8);
            var manifest = (int)BitConverter.ToUInt32(tlib, 12);
            for (var i = 0; i < count; i++)
            {
                var at = manifest + 32 * i;
                var name = System.Text.Encoding.UTF8.GetString(tlib, (int)BitConverter.ToUInt32(tlib, at + 8), (int)BitConverter.ToUInt32(tlib, at + 12));
                var bytes = new byte[BitConverter.ToUInt32(tlib, at + 20)];
                Array.Copy(tlib, (int)BitConverter.ToUInt32(tlib, at + 16), bytes, 0, bytes.Length);
                result[name] = bytes;
            }
            return result;
        }

        static TypedTimeline Typed(byte[] json, List<string> names)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var timeline = new TypedTimeline
            {
                Name = Text(root, "name"),
                Duration = root.GetProperty("duration").GetUInt32(),
                Loops = root.TryGetProperty("loop", out var loop) && loop.GetBoolean(),
                Hold = root.TryGetProperty("hold", out var hold) && hold.GetBoolean(),
                Next = Text(root, "next"),
                Channel = Text(root, "channel"),
            };
            foreach (var t in root.GetProperty("tracks").EnumerateArray())
            {
                var trackType = TypeOf(t);
                var track = new TypedTrack { Name = Text(t, "name"), Type = trackType, Data = Bytes(trackType, t, names) };
                foreach (var c in t.GetProperty("clips").EnumerateArray())
                {
                    var clipType = TypeOf(c);
                    track.Clips.Add(new TypedClip { Name = Text(c, "name"), Type = clipType, Start = c.GetProperty("start").GetUInt32(), End = c.GetProperty("end").GetUInt32(), Data = Bytes(clipType, c, names) });
                }
                timeline.Tracks.Add(track);
            }
            if (root.TryGetProperty("markers", out var markers))
                foreach (var m in markers.EnumerateArray())
                {
                    var markerType = TypeOf(m);
                    timeline.Markers.Add(new TypedMarker { Type = markerType, Tick = m.GetProperty("tick").GetUInt32(), Data = Bytes(markerType, m, names) });
                }
            return timeline;
        }

        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var value) ? value.GetString() : null;

        static Type TypeOf(JsonElement e)
            => Game.GetType(e.GetProperty("namespace").GetString() + "." + e.GetProperty("type").GetString(), true)!;

        static byte[] Bytes(Type type, JsonElement e, List<string> names)
        {
            var box = Activator.CreateInstance(type)!;
            if (e.TryGetProperty("data", out var data))
                foreach (var p in data.EnumerateObject())
                {
                    var field = type.GetField(p.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                    object value = field.FieldType == typeof(bool) ? p.Value.GetBoolean()
                        : field.FieldType == typeof(TimelineId) ? new TimelineId((ushort)(names.IndexOf(p.Value.GetString()!) + 1))
                        : field.FieldType.IsEnum ? Enum.ToObject(field.FieldType, p.Value.GetInt64())
                        : Convert.ChangeType(p.Value.GetDouble(), field.FieldType);
                    field.SetValue(box, value);
                }
            return (byte[])typeof(KindGameFixture).GetMethod(nameof(Raw), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type).Invoke(null, new[] { box })!;
        }

        static unsafe byte[] Raw<T>(object box) where T : unmanaged
        {
            var value = (T)box;
            var bytes = new byte[sizeof(T)];
            fixed (byte* at = bytes)
                *(T*)at = value;
            return bytes;
        }
    }
}
