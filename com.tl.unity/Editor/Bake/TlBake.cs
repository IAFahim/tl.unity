using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Tl.Gen.Tlb;

[assembly: InternalsVisibleTo("Tl.Unity.Bake.Tests")]

namespace Tl.Unity.Bake
{
    public static class TlBake
    {
        static readonly BakerAssemblyResolver Resolver = BakerAssemblyResolver.FromAssemblies(Array.Empty<Assembly>());

        public static byte[] BakeTyped(TypedTimeline timeline, LibraryManifest? manifest = null)
            => TimelineBaker.BakeTyped(timeline, Resolver, manifest);

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

        public static TlKindCatalog Catalog(IEnumerable<Assembly> kindAssemblies)
            => new TlKindCatalog(KindCatalog.Discover(kindAssemblies.ToList()));

        public static TlKindAnalysis Analyze(string name, byte[] tlb, TlKindCatalog catalog)
            => new TlKindAnalysis(KindTimeline.Analyze(name, tlb, catalog.Value));
    }

    public sealed class TlKindCatalog
    {
        internal readonly KindCatalog Value;

        internal TlKindCatalog(KindCatalog value) => Value = value;

        public IReadOnlyList<string> Kinds => Value.Kinds.Select(k => k.Name).ToList();
    }

    public sealed class TlKindAnalysis
    {
        internal readonly KindTimeline Value;

        internal TlKindAnalysis(KindTimeline value) => Value = value;

        public string Name => Value.Name;
        public ulong KindMask => Value.KindMask;
    }
}
