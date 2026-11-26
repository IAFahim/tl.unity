extern alias dotnetgen;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Tl.Gen.Plain.Tests;

public sealed class PlainGeneratorTests
{
    const string ZeroConfig = """
        using Tl;

        namespace Hello
        {
            [TlClip("Hello/Teleport")]
            public struct TeleportClip
            {
                public float Distance;
                public bool ComeBack;
            }

            public struct TeleportTrack : ITlTrack<TeleportClip>
            {
                [Pulse(typeof(TeleportKind), On = ClipEvent.Start)]
                public static float JumpAtClipStart(in TlFrame<TeleportClip> frame) => frame.Direction * frame.Clip.Distance;

                [Pulse(typeof(TeleportKind), On = ClipEvent.End)]
                public static float ReturnAtClipEnd(in TlFrame<TeleportClip> frame) => frame.Clip.ComeBack ? -frame.Direction * frame.Clip.Distance : 0f;
            }

            [TlKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
            public struct TeleportKind
            {
                public static void Apply(in float jumpDistance, ref Teleported player) => player.Offset += jumpDistance;
            }

            [TlState]
            public partial struct Teleported
            {
                public float Offset;
            }
        }
        """;

    const string Trigger = """
        namespace Hello
        {
            [Tl.TimelineSystem]
            public ref partial struct HelloSystem
            {
            }
        }
        """;

    const string Plain = """
        namespace Hello
        {
            public struct Health
            {
                public float Value;
            }
        }
        """;

    [Fact]
    public void ZeroConfigAssemblyGetsTheSameSourcesAsTheDotNetGenerator()
    {
        var plain = Run(new TlGenPlainGenerator(), ZeroConfig);
        Assert.NotEmpty(plain);
        Assert.Contains(plain, source => source.Text.Contains("TlAuto_Hello_Game", StringComparison.Ordinal));
        Assert.Equal(Run(new dotnetgen::Tl.Gen.CSharp.TimelineIncrementalGenerator(), ZeroConfig), plain);
    }

    [Fact]
    public void AssemblyWithATimelineSystemGetsTheSameSourcesAsTheDotNetGenerator()
    {
        var plain = Run(new TlGenPlainGenerator(), ZeroConfig, Trigger);
        Assert.NotEmpty(plain);
        Assert.Equal(Run(new dotnetgen::Tl.Gen.CSharp.TimelineIncrementalGenerator(), ZeroConfig, Trigger), plain);
    }

    [Fact]
    public void AssemblyWithoutTimelineDeclarationsGetsNothing()
    {
        Assert.Empty(Run(new TlGenPlainGenerator(), Plain));
    }

    static IReadOnlyList<(string Name, string Text)> Run(IIncrementalGenerator generator, params string[] sources)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "Hello.Game",
            sources.Select((text, index) => CSharpSyntaxTree.ParseText(text, parse, $"Source{index}.cs")),
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: parse).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return driver.GetRunResult().Results
            .SelectMany(static result => result.GeneratedSources)
            .Select(static source => (source.HintName, source.SourceText.ToString().Replace("\r\n", "\n")))
            .OrderBy(static source => source.HintName, StringComparer.Ordinal)
            .ToList();
    }

    static ImmutableArray<MetadataReference> References()
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return [.. platform.Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)), MetadataReference.CreateFromFile(typeof(Tl.TlKindAttribute).Assembly.Location)];
    }
}
