using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Tl;

if (typeof(CSharpCompilation).Assembly.GetName().Version != new Version(4, 3, 0, 0))
    return 1;
var analyzer = Path.Combine(AppContext.BaseDirectory, "Tl.Gen.CSharp.dll");
AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Tl.Compiler.dll"));
var reference = new AnalyzerFileReference(analyzer, new Loader());
var generator = reference.GetGenerators(LanguageNames.CSharp).Single();
var source = """
    using Tl;
    public readonly record struct Clip(int Value);
    public readonly struct Track : IBlend<Clip>
    {
        public void Blend(in Clip first, in Clip second, float factor, out Clip result) => result = first;
    }
    public readonly struct Job : ITimelineJob<Track, Clip>
    {
        public static void Execute(in Frame<Track, Clip> frame, ref int value) => value += frame.Clip.Value;
    }
    public readonly partial struct UnityTimeline : ITimeline
    {
        public static void Define(scoped Builder builder)
        {
            var track = builder.Track(new Track()).Use<Job>();
            builder.Clip(track, new Clip(3), 0u, 1u);
        }
    }
    public readonly struct UnityRows;
    public readonly partial struct UnityCatalog : ITimelineCatalog
    {
        public static void Define(scoped CatalogBuilder builder)
        {
            builder.Schema<UnityRows>().Asset<UnityTimeline>();
        }
    }
    """;
var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
var tree = CSharpSyntaxTree.ParseText(source, parseOptions, "UnityTimeline.cs");
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Append(typeof(ITimeline).Assembly.Location)
    .Distinct(StringComparer.Ordinal)
    .Select(static path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create(
    "Unity6000Receipt",
    [tree],
    references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
GeneratorDriver driver = CSharpGeneratorDriver.Create([generator], parseOptions: parseOptions);
driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
var generated = driver.GetRunResult().Results.Single().GeneratedSources;
if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
    || generated.Length != 2)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, diagnostics));
    Console.Error.WriteLine($"generated sources: {generated.Length}");
    return 2;
}
Console.WriteLine($"Unity 6000 Roslyn {typeof(CSharpCompilation).Assembly.GetName().Version}: analyzer loaded; job and catalog sources generated");
return 0;

sealed class Loader : IAnalyzerAssemblyLoader
{
    public void AddDependencyLocation(string fullPath) { }
    public Assembly LoadFromPath(string fullPath) => AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
}
