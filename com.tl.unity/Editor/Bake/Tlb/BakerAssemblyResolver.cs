using System.Reflection;
using System.Text.Json;

namespace Tl.Gen.Tlb;

public sealed class BakerAssemblyResolver
{
    private readonly List<Assembly> _assemblies = [];
    private readonly List<Assembly> _referenced = [];

    public BakerAssemblyResolver(IEnumerable<string>? assemblyPaths = null)
    {
        _assemblies.AddRange(AppDomain.CurrentDomain.GetAssemblies());

        if (assemblyPaths != null)
        {
            foreach (var path in assemblyPaths)
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    var asm = Assembly.LoadFrom(fullPath);
                    if (!_assemblies.Contains(asm))
                        _assemblies.Add(asm);
                    if (!_referenced.Contains(asm))
                        _referenced.Add(asm);
                }
                else
                {
                    throw new FileNotFoundException($"Referenced assembly file not found: '{path}'", path);
                }
            }
        }
    }

    internal IReadOnlyList<Assembly> ReferencedAssemblies => _referenced;

    public static BakerAssemblyResolver FromAssemblies(IEnumerable<Assembly> assemblies)
    {
        var resolver = new BakerAssemblyResolver();
        resolver._assemblies.Clear();
        foreach (var asm in assemblies)
        {
            if (!resolver._assemblies.Contains(asm))
                resolver._assemblies.Add(asm);
            if (!resolver._referenced.Contains(asm))
                resolver._referenced.Add(asm);
        }
        return resolver;
    }

    public void AddAssembly(Assembly assembly)
    {
        if (!_assemblies.Contains(assembly))
            _assemblies.Add(assembly);
    }

    public Type ResolveType(string @namespace, string typeName, string? assemblyName, string context)
    {
        ValidateBareName(@namespace, allowGlobal: true, "namespace", context);
        ValidateBareName(typeName, allowGlobal: false, "type", context);

        var candidates = new List<Type>();
        foreach (var asm in EnumerateCandidateAssemblies())
            foreach (var type in GetTypesSafe(asm))
                if (string.Equals(type.Name, typeName, StringComparison.Ordinal) && MatchesNamespace(type, @namespace))
                    candidates.Add(type);

        IReadOnlyList<Type> scope = candidates;
        if (assemblyName != null)
            scope = candidates.Where(t => string.Equals(t.Assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();

        if (scope.Count == 1)
            return scope[0];

        if (scope.Count == 0)
        {
            if (assemblyName != null && candidates.Count > 0)
            {
                var assemblies = candidates.Select(t => t.Assembly.GetName().Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal);
                throw new BakeDiagnosticException($"type resolution failed: assembly '{assemblyName}' does not contain ({FormatNamespace(@namespace)}, {typeName}) for {context}; matching types exist in assembly(es): {string.Join(", ", assemblies)}.");
            }
            ThrowIfCoreSkew(context);
            throw new BakeDiagnosticException($"unknown/unresolvable type: no loaded type named ({FormatNamespace(@namespace)}, {typeName}) for {context}; reference the defining assembly and check the bare namespace/type spelling.");
        }

        var names = string.Join(", ", scope.Select(t => $"{t.FullName} in {t.Assembly.GetName().Name}").OrderBy(n => n, StringComparer.Ordinal));
        throw new BakeDiagnosticException($"ambiguous type: bare name ({FormatNamespace(@namespace)}, {typeName}) matches multiple loaded types for {context}; declare 'assembly' with one of: {names}.");
    }

    internal Type ResolveInferredType(string typeName, string? assemblyName, string context)
    {
        ValidateBareName(typeName, allowGlobal: false, "type", context);

        var candidates = new List<Type>();
        foreach (var asm in EnumerateCandidateAssemblies())
        {
            if (assemblyName != null && !string.Equals(asm.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var type in GetTypesSafe(asm))
                if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
                    candidates.Add(type);
        }

        if (candidates.Count == 1)
            return candidates[0];

        if (candidates.Count == 0)
        {
            ThrowIfCoreSkew(context);
            var scope = assemblyName == null ? string.Empty : $" in assembly '{assemblyName}'";
            throw new BakeDiagnosticException($"unknown bare name: no loaded type named '{typeName}'{scope} for {context}; --auto fills a missing 'namespace' only when exactly one loaded type carries that bare name; write the 'namespace' explicitly.");
        }
        var found = string.Join(", ", candidates
            .Select(t => $"({FormatNamespace(t.Namespace ?? "")}, {t.Name}) in {t.Assembly.GetName().Name}")
            .OrderBy(n => n, StringComparer.Ordinal));
        throw new BakeDiagnosticException($"ambiguous bare name: '{typeName}' matches {candidates.Count} loaded types for {context}: {found}; --auto fills a missing 'namespace' only when exactly one candidate exists; write the 'namespace' explicitly.");
    }

    private void ThrowIfCoreSkew(string context)
    {
        var tool = typeof(IBlend<>).Assembly.GetName().Version ?? new Version(0, 0);
        foreach (var asm in _referenced)
        {
            var built = CoreSkew(asm.GetReferencedAssemblies(), tool);
            if (built != null)
                throw new BakeDiagnosticException($"tool skew: '{asm.GetName().Name}' was built against Tl.Core {built}, but this tlb loads Tl.Core {tool}, so its timeline types cannot load for {context}; update the tool (dotnet tool update --global Tl.Bake, or the local tool manifest) or bake with the tlb that matches the game's Tl.Core.");
        }
        foreach (var asm in _referenced)
        {
            try
            {
                asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var first = ex.LoaderExceptions.FirstOrDefault(static e => e != null)?.Message ?? "unknown loader failure";
                throw new BakeDiagnosticException($"type load failure: {ex.Types.Count(static t => t == null)} types of '{asm.GetName().Name}' cannot load in this tlb for {context}, so their names are invisible to resolution; first loader error: {first} Rebuild the game against the Tl.Core this tlb ships ({tool}) or bake with the matching tlb.");
            }
        }
    }

    internal static Version? CoreSkew(IEnumerable<AssemblyName> references, Version tool)
    {
        foreach (var reference in references)
            if (string.Equals(reference.Name, "Tl.Core", StringComparison.OrdinalIgnoreCase) && reference.Version is { } built && built > tool)
                return built;
        return null;
    }

    private static string FormatNamespace(string @namespace) => @namespace.Length == 0 ? "<global>" : @namespace;

    private static void ValidateBareName(string value, bool allowGlobal, string field, string context)
    {
        if (value.Length == 0)
        {
            if (allowGlobal) return;
            throw new BakeDiagnosticException($"empty bare name: '{field}' in {context} must be a bare name.");
        }
        if (value.Contains('.') || value.Contains(',') || value.Contains('+') || value.Contains('='))
            throw new BakeDiagnosticException($"dotted name rejected: '{field}' in {context} must be a bare name without '.', ',', '+' or '='; got '{value}'.");
    }

    private static bool MatchesNamespace(Type type, string @namespace) =>
        @namespace.Length == 0
            ? string.IsNullOrEmpty(type.Namespace)
            : string.Equals(type.Namespace, @namespace, StringComparison.Ordinal);

    private IEnumerable<Assembly> EnumerateCandidateAssemblies()
    {
        foreach (var asm in _assemblies)
        {
            if (asm.IsDynamic) continue;
            yield return asm;
        }
    }

    private static IEnumerable<Type> GetTypesSafe(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null).Select(t => t!);
        }
    }

    public static bool IsUnmanaged(Type type)
    {
        if (!type.IsValueType || type.IsGenericTypeDefinition)
            return false;
        if (type.IsPrimitive || type.IsPointer || type.IsEnum)
            return true;

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!IsUnmanaged(field.FieldType))
                return false;
        }
        return true;
    }

    public static void ValidateUnmanaged(Type trackType, Type clipType)
    {
        if (!IsUnmanaged(trackType))
            throw new BakeDiagnosticException($"track or clip not unmanaged: track type '{trackType.FullName}' is not an unmanaged type.");
        if (!IsUnmanaged(clipType))
            throw new BakeDiagnosticException($"track or clip not unmanaged: clip type '{clipType.FullName}' is not an unmanaged type.");
    }

    public static object PopulateStruct(Type structType, JsonElement element, string contextName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new BakeDiagnosticException($"wrong-typed value for {contextName}: expected object, got {element.ValueKind}.");

        var allFields = structType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var fieldMap = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        foreach (var f in allFields)
        {
            var name = f.Name;
            if (name.StartsWith("<") && name.Contains(">k__BackingField"))
            {
                name = name.Substring(1, name.IndexOf('>') - 1);
            }
            fieldMap[name] = f;
        }

        object boxed = Activator.CreateInstance(structType)!;

        foreach (var prop in element.EnumerateObject())
        {
            if (!fieldMap.TryGetValue(prop.Name, out var field))
            {
                throw new BakeDiagnosticException($"unknown field name in track/payload: field '{prop.Name}' not found on type '{structType.Name}' in {contextName}.");
            }

            var val = ReadPrimitive(prop.Value, field.FieldType, prop.Name, contextName);
            field.SetValue(boxed, val);
        }

        return boxed;
    }

        private static readonly Dictionary<Type, (Func<JsonElement, object?> Read, string Name)> NumberReaders = new()
        {
            [typeof(byte)] = (e => e.ValueKind == JsonValueKind.Number && e.TryGetByte(out var v) ? v : null, "byte"),
            [typeof(sbyte)] = (e => e.ValueKind == JsonValueKind.Number && e.TryGetSByte(out var v) ? v : null, "sbyte"),
            [typeof(short)] = (e => e.ValueKind == JsonValueKind.Number && e.TryGetInt16(out var v) ? v : null, "short"),
            [typeof(ushort)] = (e => e.ValueKind == JsonValueKind.Number && e.TryGetUInt16(out var v) ? v : null, "ushort"),
            [typeof(int)] = (e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null, "int"),
            [typeof(uint)] = (e => e.ValueKind == JsonValueKind.Number && e.TryGetUInt32(out var v) ? v : null, "uint"),
            [typeof(long)] = (e => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : null, "long"),
            [typeof(ulong)] = (e => e.ValueKind == JsonValueKind.Number && e.TryGetUInt64(out var v) ? v : null, "ulong"),
            [typeof(float)] = (e => e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out var v) ? v : null, "float"),
            [typeof(double)] = (e => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : null, "double"),
        };

        private static object ReadPrimitive(JsonElement elem, Type targetType, string fieldName, string contextName)
        {
            if (targetType == typeof(bool))
            {
                if (elem.ValueKind == JsonValueKind.True) return true;
                if (elem.ValueKind == JsonValueKind.False) return false;
                throw new BakeDiagnosticException($"wrong-typed value: field '{fieldName}' in {contextName} expected bool, got {elem.ValueKind}.");
            }
            if (NumberReaders.TryGetValue(targetType, out var entry))
            {
                var value = entry.Read(elem);
                if (value != null) return value;
                throw new BakeDiagnosticException($"wrong-typed value: field '{fieldName}' in {contextName} expected {entry.Name}, got {elem.GetRawText()}.");
            }
            throw new BakeDiagnosticException($"wrong-typed value: field '{fieldName}' in {contextName} has unsupported primitive type '{targetType.Name}'.");
        }
}
