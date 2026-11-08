namespace Tl.Gen.Tlb;

internal static class TypeNames
{
    internal static string JsonName(Type type) => type.IsConstructedGenericType
        ? type.Name.Substring(0, type.Name.IndexOf('`')) + "<" + string.Join(",", type.GetGenericArguments().Select(a => (a.Namespace is { } ns ? ns + "." : "") + JsonName(a))) + ">"
        : type.Name;

    internal static Type ResolveGeneric(BakerAssemblyResolver resolver, string space, string name, string? assembly, string context)
    {
        var open = name.IndexOf('<');
        if (open <= 0 || !name.EndsWith(">", StringComparison.Ordinal)) throw new BakeDiagnosticException($"invalid generic type '{name}' in {context}.");
        var arguments = new List<Type>();
        var start = open + 1;
        var depth = 0;
        for (var i = start; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '<') depth++;
            if (c == '>' && i < name.Length - 1) depth--;
            if (depth < 0) throw new BakeDiagnosticException($"invalid generic type '{name}' in {context}.");
            if (depth != 0 || c != ',' && i != name.Length - 1) continue;
            var text = name.Substring(start, i - start).Trim();
            var inner = text.IndexOf('<');
            var dot = text.LastIndexOf('.', inner < 0 ? text.Length - 1 : inner);
            arguments.Add(resolver.ResolveType(dot < 0 ? "" : text.Substring(0, dot), text.Substring(dot + 1), null, context));
            start = i + 1;
        }
        if (depth != 0 || arguments.Count == 0) throw new BakeDiagnosticException($"invalid generic type '{name}' in {context}.");
        var definition = resolver.ResolveType(space, name.Substring(0, open) + "`" + arguments.Count, assembly, context);
        try { return definition.MakeGenericType(arguments.ToArray()); }
        catch (ArgumentException error) { throw new BakeDiagnosticException($"invalid generic type '{name}' in {context}: {error.Message}"); }
    }

    internal static void Unique(IEnumerable<Type> types)
    {
        var seen = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            var name = Keying.StableName(type);
            if (!seen.TryGetValue(name, out var other))
            {
                seen[name] = type;
                continue;
            }
            if (other != type) Collide(name, other, type);
        }
    }

    internal static void Collide(string name, Type a, Type b)
    {
        var first = a.Assembly.GetName().Name ?? "";
        var second = b.Assembly.GetName().Name ?? "";
        if (string.Equals(first, second, StringComparison.Ordinal)) return;
        if (string.CompareOrdinal(first, second) > 0) (first, second) = (second, first);
        throw new BakeDiagnosticException($"type name collision: '{name}' is defined in assembly '{first}' and in assembly '{second}'; Tl keys timeline data by full type name (namespace and name), so rename one of them or move it to another namespace.");
    }
}
