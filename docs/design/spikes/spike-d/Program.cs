using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tl.Gen.Unity;

var tlCore = args[0];
var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
var bcl = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Where(static p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
    .Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
var copts = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable);
const string usings = "global using global::System; global using global::System.Collections.Generic; global using global::System.IO; global using global::System.Linq; global using global::System.Threading; global using global::System.Threading.Tasks;";

MetadataReference Emit(string name, IEnumerable<SyntaxTree> trees, IEnumerable<MetadataReference> refs, bool generate)
{
    var c = CSharpCompilation.Create(name, trees, bcl.Concat(refs), copts);
    if (generate)
    {
        var driver = CSharpGeneratorDriver.Create(new TlGenUnityGenerator()).RunGenerators(c);
        var run = driver.GetRunResult();
        Console.WriteLine("== generator on " + name + ": diagnostics=" + run.Diagnostics.Length);
        foreach (var d in run.Diagnostics) Console.WriteLine("  " + d);
        foreach (var g in run.Results.SelectMany(r => r.GeneratedSources)) Console.WriteLine("  generated " + g.HintName + " " + g.SourceText.Length + " chars" + (g.SourceText.ToString().Contains("HealKind") ? " mentions HealKind" : ""));
        if (Environment.GetEnvironmentVariable("DUMP") == name)
            foreach (var g in run.Results.SelectMany(r => r.GeneratedSources)) Console.WriteLine("---- " + g.HintName + "\n" + g.SourceText);
    }
    using var pe = new MemoryStream();
    var r = c.Emit(pe);
    if (!r.Success) { foreach (var d in r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5)) Console.WriteLine("  compile " + name + ": " + d); }
    return MetadataReference.CreateFromImage(pe.ToArray());
}

var core = MetadataReference.CreateFromFile(args[0]);
var stubs = Emit("Unity.Entities", new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UnityStubs.cs")), options, "UnityStubs.cs") }, Array.Empty<MetadataReference>(), false);
const string kindsSrc = @"using Tl; using Unity.Entities;
namespace SpikeD {
public struct SpikeDHealth : IComponentData { public float Value; }
[TlKind] public struct SpikeDHealKind { public static void Combine(ref float total, in float next) => total += next; public static void Apply(in float total, ref SpikeDHealth h) => h.Value += total; } }";
const string gameSrc = @"using Tl;
namespace SpikeDGame {
[TlClip(""Spike Heal"")] public struct SpikeDHealClip { public float Amount; }
public struct SpikeDHealTrack : ITlTrack<SpikeDHealClip> { [Feeds(typeof(SpikeD.SpikeDHealKind))] public static float Evaluate(in TlFrame<SpikeDHealClip> f) => f.Clip.Amount; } }";
var kinds = Emit("Spike.D.Kinds", new[] { CSharpSyntaxTree.ParseText(kindsSrc, options), CSharpSyntaxTree.ParseText(usings, options) }, new[] { core, stubs }, true);
Emit("Spike.D.Game", new[] { CSharpSyntaxTree.ParseText(gameSrc, options), CSharpSyntaxTree.ParseText(usings, options) }, new[] { core, stubs, kinds }, true);
Emit("Spike.D.SameAssembly", new[] { CSharpSyntaxTree.ParseText(kindsSrc, options), CSharpSyntaxTree.ParseText(gameSrc, options), CSharpSyntaxTree.ParseText(usings, options) }, new[] { core, stubs }, true);
