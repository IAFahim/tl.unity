using System.Reflection;

namespace Tl.Gen.Tlb;

public static class LibraryBake
{
    static readonly BakerAssemblyResolver NoAssemblies = BakerAssemblyResolver.FromAssemblies(Array.Empty<Assembly>());

    public static byte[] BakeTyped(TypedTimeline timeline, LibraryManifest? manifest = null)
        => TimelineBaker.BakeTyped(timeline, NoAssemblies, manifest);

    public static LibraryManifest Manifest(IReadOnlyList<string> timelines, IReadOnlyList<string> channels, uint tickRate)
    {
        var manifest = new LibraryManifest { TickRate = tickRate };
        for (var i = 0; i < timelines.Count; i++)
        {
            if (manifest.Timelines.ContainsKey(timelines[i]))
                throw new BakeDiagnosticException($"duplicate timeline '{timelines[i]}'.");
            manifest.Timelines[timelines[i]] = (ushort)(i + 1);
        }
        for (var i = 0; i < channels.Count; i++)
        {
            if (manifest.Channels.ContainsKey(channels[i]))
                throw new BakeDiagnosticException($"duplicate channel '{channels[i]}'.");
            manifest.Channels[channels[i]] = (ushort)(i + 1);
        }
        return manifest;
    }

    public static BakeKinds Kinds(IEnumerable<Assembly> assemblies, IReadOnlyDictionary<string, KindOverride>? overrides = null)
        => new(KindCatalog.Discover(assemblies), overrides);

    public static byte[] KindPayload(string timeline, byte[] tlb, BakeKinds kinds)
        => KindLayout.Payload(kinds.Catalog, KindTimeline.Analyze(timeline, tlb, kinds.Catalog), kinds.Inline, kinds.GamePulses);

    public static ulong KindMask(string timeline, byte[] tlb, BakeKinds kinds)
        => KindTimeline.Analyze(timeline, tlb, kinds.Catalog).KindMask;
}

public sealed class BakeKinds
{
    internal readonly KindCatalog Catalog;
    internal readonly int[] Inline;
    internal readonly int[] GamePulses;

    internal BakeKinds(KindCatalog catalog, IReadOnlyDictionary<string, KindOverride>? overrides)
    {
        Catalog = catalog;
        Inline = new int[catalog.Kinds.Count];
        GamePulses = new int[catalog.Kinds.Count];
        for (var k = 0; k < Inline.Length; k++)
        {
            var kind = catalog.Kinds[k];
            KindOverride given = default;
            if (overrides != null)
                overrides.TryGetValue(kind.Name, out given);
            Inline[k] = given.Inline ?? kind.Inline ?? -1;
            GamePulses[k] = given.GamePulses ?? kind.GamePulses;
        }
    }

    public IReadOnlyList<string> Names => Catalog.Kinds.Select(k => k.Name).ToList();
}
