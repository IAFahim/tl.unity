using System.Text;
using System.Text.Json;

namespace Tl.Gen.Tlb;

public static class TlJson
{
    public static JsonDocument ReadProject(byte[] json)
    {
        var document = JsonDocument.Parse(json);
        try
        {
            var root = document.RootElement;
            Object(root, "project", "schema", "tickRate", "channels", "timelines", "kinds", "packs");
            Schema(root);
            if (root.TryGetProperty("tickRate", out var rate) && (rate.ValueKind != JsonValueKind.Number || !rate.TryGetUInt32(out var ticks) || ticks == 0)) Fail("project.tickRate must be a positive integer.");
            Strings(root.GetProperty("timelines"), "project.timelines", ushort.MaxValue, false);
            if (root.TryGetProperty("channels", out var channels)) Strings(channels, "project.channels", byte.MaxValue, true);
            if (root.TryGetProperty("packs", out var packs))
            {
                Array(packs, "project.packs");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pack in packs.EnumerateArray())
                {
                    Object(pack, "pack", "name", "assembly", "version", "layout");
                    foreach (var key in new[] { "name", "assembly", "version", "layout" }) Text(pack.GetProperty(key), "pack." + key);
                    if (!names.Add(pack.GetProperty("name").GetString()!)) Fail("duplicate pack name.");
                }
            }
            return document;
        }
        catch (KeyNotFoundException error) { document.Dispose(); throw new BakeDiagnosticException("project is missing a required property: " + error.Message); }
        catch { document.Dispose(); throw; }
    }

    public static JsonDocument ReadScene(byte[] json)
    {
        var document = JsonDocument.Parse(json);
        try
        {
            var root = document.RootElement;
            Object(root, "scene", "schema", "project", "entities", "requests");
            Schema(root);
            if (root.TryGetProperty("project", out var project)) Text(project, "scene.project");
            var entities = root.GetProperty("entities");
            Array(entities, "scene.entities");
            foreach (var entity in entities.EnumerateArray())
            {
                Object(entity, "entity", "name", "states", "play");
                if (entity.TryGetProperty("name", out var name)) Text(name, "entity.name");
                if (entity.TryGetProperty("states", out var states))
                {
                    Array(states, "entity.states");
                    foreach (var state in states.EnumerateArray())
                    {
                        Object(state, "state", "namespace", "type", "data");
                        if (state.GetProperty("namespace").ValueKind != JsonValueKind.String) Fail("state.namespace must be a string.");
                        Text(state.GetProperty("type"), "state.type");
                        if (state.GetProperty("data").ValueKind != JsonValueKind.Object) Fail("state.data must be an object.");
                    }
                }
                if (!entity.TryGetProperty("play", out var play)) continue;
                Array(play, "entity.play");
                foreach (var start in play.EnumerateArray())
                {
                    Object(start, "play", "timeline", "tick");
                    Text(start.GetProperty("timeline"), "play.timeline");
                    if (start.TryGetProperty("tick", out var tick)) Number(tick, "play.tick", ushort.MaxValue);
                }
            }
            if (root.TryGetProperty("requests", out var requests))
            {
                Array(requests, "scene.requests");
                foreach (var request in requests.EnumerateArray())
                {
                    Object(request, "request", "step", "entity", "op", "timeline", "channel", "tick", "speed", "reverse");
                    Number(request.GetProperty("step"), "request.step", int.MaxValue);
                    Number(request.GetProperty("entity"), "request.entity", entities.GetArrayLength() - 1);
                    var op = request.GetProperty("op");
                    Text(op, "request.op");
                    var operation = op.GetString();
                    if (!(operation is "Start" or "Stop" or "Pause" or "Resume" or "Seek" or "SetSpeed" or "SetReverse" or "StopChannel" or "StopAll" or "PauseChannel" or "ResumeChannel" or "SetSpeedChannel" or "SetReverseChannel")) Fail("unknown request operation.");
                    var allowed = new List<string> { "step", "entity", "op" };
                    if (operation!.EndsWith("Channel", StringComparison.Ordinal)) { Text(request.GetProperty("channel"), "request.channel"); allowed.Add("channel"); }
                    else if (operation != "StopAll") { Text(request.GetProperty("timeline"), "request.timeline"); allowed.Add("timeline"); }
                    if (operation is "Seek" or "Start") allowed.Add("tick");
                    if (operation == "Seek") Number(request.GetProperty("tick"), "request.tick", ushort.MaxValue);
                    if (operation.StartsWith("SetSpeed", StringComparison.Ordinal)) { _ = request.GetProperty("speed"); allowed.Add("speed"); }
                    if (operation.StartsWith("SetReverse", StringComparison.Ordinal)) { _ = request.GetProperty("reverse"); allowed.Add("reverse"); }
                    Object(request, "request", allowed.ToArray());
                    if (request.TryGetProperty("timeline", out var timeline)) Text(timeline, "request.timeline");
                    if (request.TryGetProperty("channel", out var channel)) Text(channel, "request.channel");
                    if (request.TryGetProperty("tick", out var tick)) Number(tick, "request.tick", ushort.MaxValue);
                    if (request.TryGetProperty("speed", out var speed) && (speed.ValueKind != JsonValueKind.Number || !speed.TryGetDouble(out var value) || double.IsInfinity(value) || double.IsNaN(value) || value <= 0 || value > 255d / 16)) Fail("request.speed must be greater than 0 and at most 15.9375.");
                    if (request.TryGetProperty("reverse", out var reverse) && reverse.ValueKind != JsonValueKind.True && reverse.ValueKind != JsonValueKind.False) Fail("request.reverse must be a boolean.");
                }
            }
            return document;
        }
        catch (KeyNotFoundException error) { document.Dispose(); throw new BakeDiagnosticException("scene is missing a required property: " + error.Message); }
        catch { document.Dispose(); throw; }
    }

    public static string ExportProject(IReadOnlyList<string> timelines, IReadOnlyList<string> channels, uint tickRate)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteNumber("tickRate", tickRate);
            writer.WriteStartArray("channels");
            foreach (var name in channels) writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteStartArray("timelines");
            foreach (var name in timelines) writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var bytes = stream.ToArray();
        using var valid = ReadProject(bytes);
        return Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n") + "\n";
    }

    static void Schema(JsonElement element)
    {
        if (!element.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var value) || value != 1) Fail("unsupported JSON schema: expected schema 1.");
    }

    static void Object(JsonElement element, string context, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object) Fail(context + " must be an object.");
        Unique(element, context);
        foreach (var property in element.EnumerateObject())
            if (!keys.Contains(property.Name)) Fail($"unknown {context} property '{property.Name}'.");
    }

    static void Unique(JsonElement element, string context)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) Fail($"duplicate property '{property.Name}' in {context}.");
                Unique(property.Value, context + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Unique(item, context);
    }

    static void Strings(JsonElement element, string context, int maximum, bool unique)
    {
        Array(element, context);
        if (element.GetArrayLength() > maximum) Fail(context + " has too many entries.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in element.EnumerateArray())
        {
            Text(value, context);
            if (unique && !seen.Add(value.GetString()!)) Fail("duplicate name in " + context + ".");
        }
    }

    static void Array(JsonElement element, string context) { if (element.ValueKind != JsonValueKind.Array) Fail(context + " must be an array."); }
    static void Text(JsonElement element, string context) { if (element.ValueKind != JsonValueKind.String || element.GetString()!.Length == 0) Fail(context + " must be a non-empty string."); }
    static void Number(JsonElement element, string context, int maximum) { if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < 0 || value > maximum) Fail(context + " is outside its integer range."); }
    static void Fail(string message) => throw new BakeDiagnosticException(message);
}
