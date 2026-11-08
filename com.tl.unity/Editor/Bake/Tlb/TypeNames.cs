namespace Tl.Gen.Tlb;

internal static class TypeNames
{
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
