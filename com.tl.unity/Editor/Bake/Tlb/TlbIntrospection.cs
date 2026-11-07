using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Tl.Gen.Tlb;

internal static class TlbIntrospection
{
    private static readonly Dictionary<Type, string> PrimitiveNames = new()
    {
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
    };

    internal static string Introspect(IReadOnlyList<Assembly> assemblies)
    {
        var scope = assemblies.Distinct().ToArray();
        var types = scope.SelectMany(GetTypesSafe).ToArray();
        var pairs = CollectPairs(types);
        var consumers = CollectConsumers(types);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("assemblies");
            foreach (var name in scope.Select(AssemblyName).Distinct().OrderBy(n => n, StringComparer.Ordinal))
                writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteStartArray("pairs");
            foreach (var pair in pairs)
            {
                writer.WriteStartObject();
                WriteSide(writer, "track", pair.Track);
                WriteSide(writer, "clip", pair.Clip);
                writer.WriteBoolean("blendable", pair.Blendable);
                writer.WriteBoolean("unmanaged", pair.Unmanaged);
                writer.WriteStartArray("trackPairings");
                foreach (var name in TrackPairings(pair.Track))
                    writer.WriteStringValue(name);
                writer.WriteEndArray();
                writer.WriteStartArray("consumers");
                foreach (var consumer in consumers
                             .Where(c => c.Track == pair.Track && c.Clip == pair.Clip)
                             .OrderBy(c => c.Type.FullName, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", consumer.Type.FullName);
                    writer.WriteString("assembly", AssemblyName(consumer.Type.Assembly));
                    writer.WriteStartArray("outputs");
                    foreach (var output in consumer.Outputs)
                        writer.WriteStringValue(output);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSide(Utf8JsonWriter writer, string key, Type type)
    {
        writer.WriteStartObject(key);
        writer.WriteString("namespace", type.Namespace ?? string.Empty);
        writer.WriteString("name", type.Name);
        writer.WriteString("assembly", AssemblyName(type.Assembly));
        if (BakerAssemblyResolver.IsUnmanaged(type))
            writer.WriteNumber("size", SizeOfUnmanaged(type));
        writer.WriteStartArray("fields");
        foreach (var field in InputFields(type))
        {
            writer.WriteStartObject();
            writer.WriteString("name", field.Key);
            writer.WriteString("type", field.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static List<(Type Track, Type Clip, bool Blendable, bool Unmanaged)> CollectPairs(Type[] types)
    {
        var blendable = new Dictionary<(Type, Type), bool>();
        foreach (var type in types)
        {
            foreach (var iface in type.GetInterfaces())
            {
                if (!iface.IsGenericType)
                    continue;
                var definition = iface.GetGenericTypeDefinition();
                if (definition == typeof(IBlend<>))
                {
                    blendable[(type, iface.GetGenericArguments()[0])] = true;
                }
                else if (definition == typeof(ITrack<,>))
                {
                    var arguments = iface.GetGenericArguments();
                    var key = (arguments[0], arguments[1]);
                    blendable.TryAdd(key, false);
                }
            }
        }

        return blendable
            .Select(item => (item.Key.Item1, item.Key.Item2, item.Value))
            .OrderBy(item => item.Item1.Namespace ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Item1.Name, StringComparer.Ordinal)
            .ThenBy(item => AssemblyName(item.Item1.Assembly), StringComparer.Ordinal)
            .ThenBy(item => item.Item2.Namespace ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Item2.Name, StringComparer.Ordinal)
            .ThenBy(item => AssemblyName(item.Item2.Assembly), StringComparer.Ordinal)
            .Select(item => (
                item.Item1, item.Item2, item.Item3,
                BakerAssemblyResolver.IsUnmanaged(item.Item1) && BakerAssemblyResolver.IsUnmanaged(item.Item2)))
            .ToList();
    }

    private static List<string> TrackPairings(Type track) =>
        track.GetInterfaces()
            .Where(iface => iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IBlend<>))
            .Select(iface => iface.GetGenericArguments()[0].FullName ?? iface.GetGenericArguments()[0].Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static List<(Type Type, Type Track, Type Clip, string[] Outputs)> CollectConsumers(Type[] types)
    {
        var consumers = new List<(Type, Type, Type, string[])>();
        foreach (var type in types)
        {
            foreach (var iface in type.GetInterfaces())
            {
                if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(ITrack<,>))
                    continue;
                var arguments = iface.GetGenericArguments();
                consumers.Add((type, arguments[0], arguments[1], OutputTypes(type, arguments[0], arguments[1])));
            }
        }

        return consumers;
    }

    private static string[] OutputTypes(Type jobType, Type trackType, Type clipType)
    {
        var frame = typeof(Frame<,>).MakeGenericType(trackType, clipType);
        var outputs = new List<string>();
        CollectColumns(jobType.GetMethod("ExecuteActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), frame, outputs);
        CollectColumns(jobType.GetMethod("Fold", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), frame, outputs);
        return outputs.ToArray();
    }

    private static void CollectColumns(MethodInfo? method, Type frame, List<string> outputs)
    {
        if (method is null)
            return;
        foreach (var parameter in method.GetParameters())
        {
            if (!parameter.ParameterType.IsByRef || IsReadOnlyParameter(parameter))
                continue;
            var element = parameter.ParameterType.GetElementType()!;
            if (element == frame)
                continue;
            outputs.Add(PrimitiveNames.TryGetValue(element, out var primitive) ? primitive : element.Name);
        }
    }

    private static bool IsReadOnlyParameter(ParameterInfo parameter) =>
        (parameter.Attributes & ParameterAttributes.In) != 0;

    private static List<KeyValuePair<string, string>> InputFields(Type type)
    {
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var name = field.Name;
            if (name.StartsWith('<') && name.Contains(">k__BackingField", StringComparison.Ordinal))
                name = name[1..name.IndexOf('>')];
            if (PrimitiveNames.TryGetValue(field.FieldType, out var primitive))
                byName[name] = primitive;
        }

        return byName
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new KeyValuePair<string, string>(item.Key, item.Value))
            .ToList();
    }

    private static int SizeOfUnmanaged(Type type) =>
        (int)typeof(SizeCell<>).MakeGenericType(type)
            .GetField("Value", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    private static string AssemblyName(Assembly assembly) => assembly.GetName().Name ?? string.Empty;

    private static IEnumerable<Type> GetTypesSafe(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null).Select(t => t!);
        }
    }
}

internal static class SizeCell<T> where T : unmanaged
{
    public static readonly int Value = Unsafe.SizeOf<T>();
}
