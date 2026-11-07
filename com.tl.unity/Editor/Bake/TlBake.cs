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
            => Catalog(kindAssemblies, null);

        public static TlKindCatalog Catalog(IEnumerable<Assembly> kindAssemblies, IReadOnlyDictionary<string, TlKindKnobs>? overrides)
        {
            var assemblies = kindAssemblies.ToList();
            var catalog = KindCatalog.Discover(assemblies);
            var knobs = new TlKindKnobs[catalog.Kinds.Count];
            for (var k = 0; k < knobs.Length; k++)
            {
                var kind = catalog.Kinds[k];
                knobs[k] = overrides != null && overrides.TryGetValue(kind.Name, out var given) ? given : new TlKindKnobs(kind.Inline ?? -1, GamePulses(assemblies, kind.FullName));
            }
            return new TlKindCatalog(catalog, knobs);
        }

        public static byte[] KindBytes(string name, byte[] timelineBytes, TlKindCatalog catalog)
            => TlKindBytes.Write(KindTimeline.Analyze(name, timelineBytes, catalog.Value), catalog);

        static int GamePulses(List<Assembly> assemblies, string fullName)
        {
            foreach (var assembly in assemblies)
            {
                var type = assembly.GetType(fullName, false);
                if (type == null) continue;
                foreach (var attribute in type.CustomAttributes)
                {
                    if (attribute.AttributeType.FullName != KindCatalog.TlKindAttribute) continue;
                    foreach (var argument in attribute.NamedArguments)
                        if (argument.MemberName == "GamePulses" && argument.TypedValue.Value != null) return Convert.ToInt32(argument.TypedValue.Value);
                }
            }
            return 0;
        }

        public static TlKindAnalysis Analyze(string name, byte[] timelineBytes, TlKindCatalog catalog)
            => new TlKindAnalysis(KindTimeline.Analyze(name, timelineBytes, catalog.Value));
    }

    public sealed class TlKindCatalog
    {
        internal readonly KindCatalog Value;
        internal readonly TlKindKnobs[] Knobs;

        internal TlKindCatalog(KindCatalog value, TlKindKnobs[] knobs)
        {
            Value = value;
            Knobs = knobs;
        }

        public IReadOnlyList<string> Kinds => Value.Kinds.Select(k => k.Name).ToList();
    }

    public readonly struct TlKindKnobs
    {
        public readonly int Inline;
        public readonly int GamePulses;

        public TlKindKnobs(int inline, int gamePulses)
        {
            Inline = inline;
            GamePulses = gamePulses;
        }
    }

    public sealed class TlKindAnalysis
    {
        internal readonly KindTimeline Value;

        internal TlKindAnalysis(KindTimeline value) => Value = value;

        public string Name => Value.Name;
        public ulong KindMask => Value.KindMask;
    }
}
