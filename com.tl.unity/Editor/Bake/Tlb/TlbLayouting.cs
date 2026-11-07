using System.Reflection;

namespace Tl.Gen.Tlb;

internal static class TlbLayouting
{
    internal static ulong Of(BakerAssemblyResolver resolver, Type track, Type clip)
    {
        var found = 0ul;
        foreach (var assembly in Candidates(resolver, track, clip))
            foreach (var data in assembly.GetCustomAttributesData())
            {
                var name = data.AttributeType.Name;
                if (!(name.StartsWith("TlConsumerLayout", StringComparison.Ordinal) && name.EndsWith("Attribute", StringComparison.Ordinal))) continue;
                var args = data.ConstructorArguments;
                if (args.Count != 3 || args[0].Value is not Type a || a != track || args[1].Value is not Type b || b != clip || args[2].Value is not ulong layout) continue;
                if (found != 0 && found != layout)
                    throw new BakeDiagnosticException($"consumer layout conflict: referenced consumer assemblies publish layout fingerprints {found} and {layout} for pair ({track.FullName}, {clip.FullName}); rebuild every consumer assembly from one struct definition and rebake.");
                found = layout;
            }
        return found != 0 ? found ^ PairLayoutDigest.Of(track, clip) : 0;
    }

    static IEnumerable<Assembly> Candidates(BakerAssemblyResolver resolver, Type track, Type clip)
    {
        var seen = new HashSet<Assembly>();
        foreach (var assembly in resolver.ReferencedAssemblies.Append(track.Assembly).Append(clip.Assembly))
            if (seen.Add(assembly))
                yield return assembly;
    }
}
