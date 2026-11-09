using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Tl.Gen.Tlb;

public sealed class TlScene
{
    public sealed class Entity
    {
        public string? Name;
        public readonly List<(Type Type, byte[] Bytes)> States = new();
        public readonly List<(ushort Timeline, ushort Tick)> Play = new();
    }

    public readonly struct Request
    {
        public Request(int step, int entity, RequestOp op, ushort target, ushort value)
        {
            Step = step;
            Entity = entity;
            Op = op;
            Target = target;
            Value = value;
        }

        public readonly int Step;
        public readonly int Entity;
        public readonly RequestOp Op;
        public readonly ushort Target;
        public readonly ushort Value;
    }

    public string? Project;
    public readonly List<Entity> Entities = new();
    public readonly List<Request> Requests = new();
    public readonly List<(int Step, bool Forward)> Direction = new();
    public readonly List<Type> Columns = new();

    public bool ForwardAt(int step, bool current)
    {
        foreach (var turn in Direction)
            if (turn.Step == step) current = turn.Forward;
        return current;
    }

    public unsafe string ToJson(LibraryManifest manifest)
    {
        string TimelineName(ushort id) => manifest.Timelines.FirstOrDefault(p => p.Value == id).Key ?? throw new BakeDiagnosticException($"scene uses timeline id {id} that the library does not name.");
        string ChannelName(ushort id) => manifest.Channels.FirstOrDefault(p => p.Value == id).Key ?? throw new BakeDiagnosticException($"scene uses channel {id} that the library does not name.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            if (Project != null) writer.WriteString("project", Project);
            writer.WriteStartArray("entities");
            foreach (var entity in Entities)
            {
                writer.WriteStartObject();
                if (entity.Name != null) writer.WriteString("name", entity.Name);
                if (entity.States.Count > 0)
                {
                    writer.WriteStartArray("states");
                    foreach (var (type, bytes) in entity.States)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("namespace", type.Namespace ?? "");
                        writer.WriteString("type", type.Name);
                        writer.WritePropertyName("data");
                        var helper = TimelineBakerFast.HelperFor(type);
                        fixed (byte* start = bytes) ValueJson.Write(writer, type, helper.Read(start), manifest);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                if (entity.Play.Count > 0)
                {
                    writer.WriteStartArray("play");
                    foreach (var (timeline, tick) in entity.Play)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("timeline", TimelineName(timeline));
                        if (tick != 0) writer.WriteNumber("tick", tick);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (Requests.Count > 0)
            {
                writer.WriteStartArray("requests");
                foreach (var request in Requests)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("step", request.Step);
                    writer.WriteNumber("entity", request.Entity);
                    var op = request.Op;
                    writer.WriteString("op", op switch
                    {
                        RequestOp.Reverse => "SetReverse",
                        RequestOp.ReverseChannel => "SetReverseChannel",
                        RequestOp.Speed => "SetSpeed",
                        RequestOp.SpeedChannel => "SetSpeedChannel",
                        _ => op.ToString()
                    });
                    if (op is RequestOp.StopChannel or RequestOp.PauseChannel or RequestOp.ResumeChannel or RequestOp.SeekChannel or RequestOp.ReverseChannel or RequestOp.SpeedChannel)
                        writer.WriteString("channel", ChannelName(request.Target));
                    else if (op != RequestOp.StopAll)
                        writer.WriteString("timeline", TimelineName(request.Target));
                    if (op is RequestOp.Seek or RequestOp.SeekChannel || op == RequestOp.Start && request.Value != 0) writer.WriteNumber("tick", request.Value);
                    if (op is RequestOp.Reverse or RequestOp.ReverseChannel) writer.WriteBoolean("reverse", request.Value != 0);
                    if (op is RequestOp.Speed or RequestOp.SpeedChannel) writer.WriteNumber("speed", request.Value / 16d);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (Direction.Count > 0)
            {
                writer.WriteStartArray("direction");
                foreach (var (step, forward) in Direction)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("step", step);
                    writer.WriteBoolean("forward", forward);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static TlScene Read(byte[] json, BakerAssemblyResolver resolver, LibraryManifest manifest)
    {
        using var document = TlJson.ReadScene(json);
        var root = document.RootElement;
        var scene = new TlScene();
        if (root.TryGetProperty("project", out var project)) scene.Project = project.GetString();
        var columns = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in root.GetProperty("entities").EnumerateArray())
        {
            var entity = new Entity();
            var context = $"scene entity {index++}";
            if (item.TryGetProperty("name", out var name)) entity.Name = name.GetString();
            if (item.TryGetProperty("states", out var states))
                foreach (var state in states.EnumerateArray())
                {
                    var type = resolver.ResolveType(state.GetProperty("namespace").GetString()!, state.GetProperty("type").GetString()!, null, context);
                    if (!TimelineBakerFast.IsUnmanagedCached(type)) throw new BakeDiagnosticException($"{context}: state '{type.FullName}' is not an unmanaged struct.");
                    if (entity.States.Exists(s => s.Type == type)) throw new BakeDiagnosticException($"{context}: state '{type.FullName}' is listed twice.");
                    entity.States.Add((type, Bytes(type, state.GetProperty("data"), context + "." + type.Name, manifest)));
                    columns[Keying.StableName(type)] = type;
                }
            if (item.TryGetProperty("play", out var play))
                foreach (var start in play.EnumerateArray())
                    entity.Play.Add((Timeline(manifest, start.GetProperty("timeline").GetString()!), start.TryGetProperty("tick", out var tick) ? (ushort)tick.GetInt32() : (ushort)0));
            scene.Entities.Add(entity);
        }
        scene.Columns.AddRange(columns.Values);
        if (root.TryGetProperty("requests", out var requests))
            foreach (var item in requests.EnumerateArray())
                scene.Requests.Add(RequestOf(item, manifest));
        if (root.TryGetProperty("direction", out var direction))
            foreach (var turn in direction.EnumerateArray())
                scene.Direction.Add((turn.GetProperty("step").GetInt32(), turn.GetProperty("forward").GetBoolean()));
        return scene;
    }

    static Request RequestOf(JsonElement item, LibraryManifest manifest)
    {
        var step = item.GetProperty("step").GetInt32();
        var entity = item.GetProperty("entity").GetInt32();
        var tick = item.TryGetProperty("tick", out var t) ? (ushort)t.GetInt32() : (ushort)0;
        var reverse = item.TryGetProperty("reverse", out var r) && r.GetBoolean() ? (ushort)1 : (ushort)0;
        ushort Speed()
        {
            var value = item.GetProperty("speed").GetDouble() * 16;
            var sixteenths = Math.Round(value);
            if (sixteenths < 1 || sixteenths > 255 || Math.Abs(value - sixteenths) > 1e-9) throw new BakeDiagnosticException($"scene request speed {item.GetProperty("speed").GetRawText()} is not a multiple of 1/16 between 1/16 and 15.9375.");
            return (ushort)sixteenths;
        }
        ushort Line() => Timeline(manifest, item.GetProperty("timeline").GetString()!);
        ushort Lane()
        {
            var name = item.GetProperty("channel").GetString()!;
            return manifest.Channels.TryGetValue(name, out var id) ? id : throw new BakeDiagnosticException($"scene request names unknown channel '{name}'.");
        }
        return item.GetProperty("op").GetString() switch
        {
            "Start" => new Request(step, entity, RequestOp.Start, Line(), tick),
            "Stop" => new Request(step, entity, RequestOp.Stop, Line(), 0),
            "Pause" => new Request(step, entity, RequestOp.Pause, Line(), 0),
            "Resume" => new Request(step, entity, RequestOp.Resume, Line(), 0),
            "Seek" => new Request(step, entity, RequestOp.Seek, Line(), tick),
            "SetReverse" => new Request(step, entity, RequestOp.Reverse, Line(), reverse),
            "StopAll" => new Request(step, entity, RequestOp.StopAll, 0, 0),
            "StopChannel" => new Request(step, entity, RequestOp.StopChannel, Lane(), 0),
            "PauseChannel" => new Request(step, entity, RequestOp.PauseChannel, Lane(), 0),
            "ResumeChannel" => new Request(step, entity, RequestOp.ResumeChannel, Lane(), 0),
            "SeekChannel" => new Request(step, entity, RequestOp.SeekChannel, Lane(), tick),
            "SetReverseChannel" => new Request(step, entity, RequestOp.ReverseChannel, Lane(), reverse),
            "SetSpeed" => new Request(step, entity, RequestOp.Speed, Line(), Speed()),
            "SetSpeedChannel" => new Request(step, entity, RequestOp.SpeedChannel, Lane(), Speed()),
            var op => throw new BakeDiagnosticException($"scene request '{op}' is not a known operation.")
        };
    }

    static ushort Timeline(LibraryManifest manifest, string name)
        => manifest.Timelines.TryGetValue(name, out var id) ? id : throw new BakeDiagnosticException($"scene names unknown timeline '{name}'.");

    static unsafe byte[] Bytes(Type type, JsonElement data, string context, LibraryManifest manifest)
    {
        var helper = TimelineBakerFast.HelperFor(type);
        var bytes = new byte[helper.Size];
        fixed (byte* p = bytes) helper.CopyTo(p, ValueJson.Read(type, data, context, manifest));
        return bytes;
    }

    public static LibraryManifest ManifestOf(byte[] lib, out List<(string Name, byte[] Tlb)> entries)
    {
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(lib.AsSpan(at));
        if (lib.Length < 96 || U(0) != 0x42494C54u || U(20) != lib.Length) throw new BakeDiagnosticException("game.tlib header is invalid.");
        var manifest = new LibraryManifest { TickRate = U(24) };
        entries = new List<(string, byte[])>();
        for (var i = 0; i < (int)U(8); i++)
        {
            var at = (int)U(12) + 32 * i;
            var name = Encoding.UTF8.GetString(lib, (int)U(at + 8), (int)U(at + 12));
            entries.Add((name, lib.AsSpan((int)U(at + 16), (int)U(at + 20)).ToArray()));
            manifest.Timelines[name] = (ushort)U(at);
        }
        var channels = new SortedDictionary<int, string>();
        foreach (var (name, tlb) in entries)
        {
            var id = (int)BinaryPrimitives.ReadUInt32LittleEndian(tlb.AsSpan(56));
            if (id == 0) continue;
            var label = TlbMetadata.Read(tlb).Labels.FirstOrDefault(l => l.TrackEntry == -2).Name
                ?? throw new BakeDiagnosticException($"timeline '{name}' has channel {id} but its metadata names no channel.");
            if (channels.TryGetValue(id, out var known) && known != label) throw new BakeDiagnosticException($"channel {id} is named '{known}' and '{label}'.");
            channels[id] = label;
        }
        for (var id = 1; id <= channels.Count; id++)
            if (!channels.ContainsKey(id)) throw new BakeDiagnosticException($"game.tlib uses channel {channels.Keys.Max()} but no timeline names channel {id}; unused channel names are not stored in game.tlib.");
        foreach (var pair in channels) manifest.Channels[pair.Value] = (ushort)pair.Key;
        return manifest;
    }
}
