using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length < 6)
{
    Console.Error.WriteLine("usage: GenPlain <Tl.Gen.CSharp.dll> <Tl.Core folder> <game folder> <assembly name> <id> <output folder>");
    return 2;
}

var generator = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var emit = generator.GetType("Tl.Gen.CSharp.SystemGeneration", true)!.GetMethod("Emit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
var assembly = args[3];
var id = args[4];
var output = args[5];
var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
var trees = new List<SyntaxTree>();
foreach (var folder in new[] { args[1], args[2] })
    foreach (var file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
        if (!file.Contains(Path.DirectorySeparatorChar + "Generated" + Path.DirectorySeparatorChar))
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), parse, file));
trees.Add(CSharpSyntaxTree.ParseText("global using global::System; global using global::System.Collections.Generic; global using global::System.IO; global using global::System.Linq; global using global::System.Threading; global using global::System.Threading.Tasks;", parse, "GlobalUsings.cs"));
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Where(static p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
    .Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p))
    .ToList();
var compilation = CSharpCompilation.Create(assembly, trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
var result = emit.Invoke(null, new object?[] { compilation, id })!;
var type = result.GetType();
var artifacts = (System.Collections.IEnumerable)type.GetField("Item1")!.GetValue(result)!;
var diagnostics = (System.Collections.IEnumerable)type.GetField("Item2")!.GetValue(result)!;
var failed = false;
foreach (var diagnostic in diagnostics)
{
    Console.Error.WriteLine(diagnostic);
    failed |= !diagnostic.ToString()!.Contains("TLGEN98");
}
Directory.CreateDirectory(output);
var written = 0;
foreach (var artifact in artifacts)
{
    var name = (string)artifact.GetType().GetProperty("RelativePath")!.GetValue(artifact)!;
    var content = ((string)artifact.GetType().GetProperty("Content")!.GetValue(artifact)!).Replace("\r\n", "\n");
    File.WriteAllText(Path.Combine(output, name), content);
    written++;
}
Console.WriteLine("written=" + written);
return failed || written == 0 ? 1 : 0;
