using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tl.Gen.Unity;

var tlCore = args[0];
var game = args[1];
var output = args[2];
var assembly = args.Length > 3 ? args[3] : "Tl.Unity.Kinds.Tests";
var check = args.Length > 4 && args[4] == "--check";
var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
var trees = new List<SyntaxTree>();
foreach (var file in Directory.GetFiles(tlCore, "*.cs"))
    trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, file));
foreach (var file in Directory.GetFiles(game, "*.cs", SearchOption.AllDirectories))
{
    if (file.Contains(Path.DirectorySeparatorChar + "Generated" + Path.DirectorySeparatorChar))
        continue;
    trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, file));
}
trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UnityStubs.cs")), options, "UnityStubs.cs"));
trees.Add(CSharpSyntaxTree.ParseText("global using global::System; global using global::System.Collections.Generic; global using global::System.IO; global using global::System.Linq; global using global::System.Threading; global using global::System.Threading.Tasks;", options, "GlobalUsings.cs"));
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Where(static p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
    .Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p))
    .ToList();
var compilation = CSharpCompilation.Create(assembly, trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable));
var driver = CSharpGeneratorDriver.Create(new TlGenUnityGenerator()).RunGenerators(compilation);
var result = driver.GetRunResult();
foreach (var diagnostic in result.Diagnostics)
    Console.Error.WriteLine(diagnostic.ToString());
Directory.CreateDirectory(output);
var failed = result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error);
var changed = 0;
foreach (var generated in result.Results.SelectMany(static r => r.GeneratedSources))
{
    var path = Path.Combine(output, generated.HintName);
    var text = generated.SourceText.ToString();
    if (File.Exists(path) && File.ReadAllText(path) == text)
        continue;
    changed++;
    if (check)
        Console.Error.WriteLine("differs: " + generated.HintName);
    else
        File.WriteAllText(path, text);
}
Console.Error.WriteLine("generated=" + result.GeneratedTrees.Length + " changed=" + changed);
return failed || check && changed != 0 ? 1 : 0;
