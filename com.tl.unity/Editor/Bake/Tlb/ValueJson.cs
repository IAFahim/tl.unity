using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Tl.Gen.Tlb;

internal static class ValueJson
{
    internal static object Read(Type type, JsonElement element, string context, LibraryManifest? manifest = null)
    {
        var split = context.LastIndexOf('.');
        var field = split < 0 ? context : context.Substring(split + 1);
        var owner = split < 0 ? context : context.Substring(0, split);
        if (type == typeof(TlCurve))
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != TlCurve.Samples)
                throw new BakeDiagnosticException($"{context}: TlCurve needs exactly {TlCurve.Samples} finite numbers.");
            var curve = new TlCurve();
            for (var i = 0; i < TlCurve.Samples; i++)
            {
                curve[i] = (float)Read(typeof(float), element[i], context + "[" + i + "]");
                if (float.IsNaN(curve[i]) || float.IsInfinity(curve[i]))
                    throw new BakeDiagnosticException($"{context}: TlCurve needs finite numbers.");
            }
            return curve;
        }
        if (type == typeof(TimelineId))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var name = element.GetString()!;
                if (name.Length == 0) return default(TimelineId);
                if (manifest == null) throw new BakeDiagnosticException($"timeline reference without manifest: field '{field}' in {owner} names timeline '{name}' but no library manifest is available; bake via `tl library`.");
                if (manifest != null && manifest.Timelines.TryGetValue(name, out var id)) return new TimelineId(id);
                throw new BakeDiagnosticException($"unknown timeline: field '{field}' in {owner} names unknown timeline '{name}'.");
            }
            throw new BakeDiagnosticException($"wrong-typed value: field '{field}' in {owner} expected timeline name, got {element.ValueKind}.");
        }
        if (type.IsEnum)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString()!;
                if (Enum.IsDefined(type, text)) return Enum.Parse(type, text, false);
                throw new BakeDiagnosticException($"unknown enum member: field '{field}' in {owner} has no member '{text}' on enum '{type.Name}' (expected one of: {string.Join(", ", Enum.GetNames(type))}).");
            }
            throw new BakeDiagnosticException($"wrong-typed value: field '{field}' in {owner} expected enum member name, got {element.ValueKind}.");
        }
        if (type.IsPrimitive)
        {
            var number = element.ValueKind == JsonValueKind.Number;
            object? value = Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean => element.ValueKind == JsonValueKind.True ? true : element.ValueKind == JsonValueKind.False ? false : null,
                TypeCode.Byte => number && element.TryGetByte(out var b) ? b : null,
                TypeCode.SByte => number && element.TryGetSByte(out var sb) ? sb : null,
                TypeCode.Int16 => number && element.TryGetInt16(out var s) ? s : null,
                TypeCode.UInt16 => number && element.TryGetUInt16(out var us) ? us : null,
                TypeCode.Int32 => number && element.TryGetInt32(out var i) ? i : null,
                TypeCode.UInt32 => number && element.TryGetUInt32(out var ui) ? ui : null,
                TypeCode.Int64 => number && element.TryGetInt64(out var l) ? l : null,
                TypeCode.UInt64 => number && element.TryGetUInt64(out var ul) ? ul : null,
                TypeCode.Single => number && element.TryGetSingle(out var f) && !float.IsNaN(f) && !float.IsInfinity(f) ? f : null,
                TypeCode.Double => number && element.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null,
                _ => null,
            };
            if (value != null) return value;
            var primitive = Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean => "bool", TypeCode.Byte => "byte", TypeCode.SByte => "sbyte", TypeCode.Int16 => "short",
                TypeCode.UInt16 => "ushort", TypeCode.Int32 => "int", TypeCode.UInt32 => "uint", TypeCode.Int64 => "long",
                TypeCode.UInt64 => "ulong", TypeCode.Single => "float", TypeCode.Double => "double", _ => type.Name,
            };
            var actual = type == typeof(bool) ? element.ValueKind.ToString() : element.GetRawText();
            throw new BakeDiagnosticException($"wrong-typed value: field '{field}' in {owner} expected {primitive}, got {actual}.");
        }
        if (element.ValueKind != JsonValueKind.Object)
            throw new BakeDiagnosticException($"wrong-typed value for {context}: expected object, got {element.ValueKind}.");
        var box = Activator.CreateInstance(type)!;
        var table = TimelineBakerFast.TableFor(type);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new BakeDiagnosticException($"duplicate property '{property.Name}' in {context}.");
            if (!table.ByName.TryGetValue(property.Name, out var entry))
                throw new BakeDiagnosticException($"unknown field name in track/payload: field '{property.Name}' not found on type '{type.Name}' in {context}.");
            entry.Field.SetValue(box, ReadField(entry.Field, property.Value, context + "." + property.Name, manifest));
        }
        return box;
    }

    internal static unsafe object ReadField(FieldInfo field, JsonElement element, string context, LibraryManifest? manifest)
    {
        var buffer = field.GetCustomAttribute<FixedBufferAttribute>();
        if (buffer == null) return Read(field.FieldType, element, context, manifest);
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != buffer.Length)
            throw new BakeDiagnosticException($"{context}: expected a fixed array of {buffer.Length} elements.");
        var helper = TimelineBakerFast.HelperFor(buffer.ElementType);
        var bytes = new byte[TimelineBakerFast.HelperFor(field.FieldType).Size];
        fixed (byte* start = bytes)
        {
            for (var i = 0; i < buffer.Length; i++)
                helper.CopyTo(start + i * helper.Size, Read(buffer.ElementType, element[i], context + "[" + i + "]", manifest));
            return TimelineBakerFast.HelperFor(field.FieldType).Read(start);
        }
    }

    internal static void Write(Utf8JsonWriter writer, Type type, object value, LibraryManifest? manifest = null)
    {
        if (type == typeof(TlCurve))
        {
            writer.WriteStartArray();
            var curve = (TlCurve)value;
            for (var i = 0; i < TlCurve.Samples; i++) writer.WriteNumberValue(curve[i]);
            writer.WriteEndArray();
        }
        else if (type == typeof(TimelineId))
        {
            var id = ((TimelineId)value).Value;
            var name = id == 0 ? "" : manifest?.Timelines.FirstOrDefault(p => p.Value == id).Key;
            if (name == null) throw new BakeDiagnosticException($"cannot export unresolved timeline id {id}; provide its manifest.");
            writer.WriteStringValue(name);
        }
        else if (type.IsEnum)
        {
            var name = Enum.GetName(type, value);
            if (name == null) throw new BakeDiagnosticException($"cannot export unnamed {type.Name} value {value}.");
            writer.WriteStringValue(name);
        }
        else if (type.IsPrimitive)
            JsonSerializer.Serialize(writer, value, type);
        else
        {
            writer.WriteStartObject();
            foreach (var entry in TimelineBakerFast.TableFor(type).Entries)
            {
                writer.WritePropertyName(entry.Name);
                WriteField(writer, entry.Field, entry.Field.GetValue(value)!, manifest);
            }
            writer.WriteEndObject();
        }
    }

    static unsafe void WriteField(Utf8JsonWriter writer, FieldInfo field, object value, LibraryManifest? manifest)
    {
        var buffer = field.GetCustomAttribute<FixedBufferAttribute>();
        if (buffer == null) { Write(writer, field.FieldType, value, manifest); return; }
        var bytes = new byte[TimelineBakerFast.HelperFor(field.FieldType).Size];
        var helper = TimelineBakerFast.HelperFor(buffer.ElementType);
        fixed (byte* start = bytes)
        {
            TimelineBakerFast.HelperFor(field.FieldType).CopyTo(start, value);
            writer.WriteStartArray();
            for (var i = 0; i < buffer.Length; i++) Write(writer, buffer.ElementType, helper.Read(start + i * helper.Size), manifest);
            writer.WriteEndArray();
        }
    }
}
