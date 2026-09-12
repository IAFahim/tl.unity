using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.CSharp;

namespace Tl.Gen.CSharp;

internal sealed record CompileSource(string Path, string Content);

internal sealed class CompileGenerationManifest
{
    public int FormatVersion { get; set; }
    public string CacheKey { get; set; } = "";
    public string SourceListHash { get; set; } = "";
    public string ReportHash { get; set; } = "";
    public List<CompileGenerationOutput?>? Outputs { get; set; }
}

internal sealed class CompileGenerationOutput
{
    public string RelativePath { get; set; } = "";
    public string ContentHash { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CompileGenerationManifest))]
internal partial class CompileManifestJsonContext : JsonSerializerContext;

internal static class CompileGenerationCache
{
    internal const string ManifestFileName = "TlGenCompile.manifest.json";
    internal const string SourceListFileName = "TlGenCompile.sources";
    internal const string ReportFileName = "TlGenCompile.report.txt";
    private const int FormatVersion = 5;

    internal static string GetKey(
        IReadOnlyList<CompileSource> sources,
        IReadOnlyList<string> symbols,
        IReadOnlyList<string>? semanticInputs = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "Tl.Gen.CSharp compile");
        Append(hash, FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, "newline=lf");

        Append(hash, typeof(CompileGenerationCache).Assembly);
        Append(hash, typeof(Microsoft.CodeAnalysis.SyntaxTree).Assembly);
        Append(hash, typeof(CSharpSyntaxTree).Assembly);

        foreach (var symbol in symbols)
            Append(hash, symbol);

        foreach (var input in semanticInputs ?? [])
            Append(hash, input);

        foreach (var source in sources)
        {
            Append(hash, source.Path);
            Append(hash, source.Content);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static CompileGenerationManifest? Load(string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, ManifestFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), CompileManifestJsonContext.Default.CompileGenerationManifest);
            if (manifest?.FormatVersion != FormatVersion
                || manifest.Outputs == null
                || manifest.SourceListHash is not { Length: 64 }
                || manifest.ReportHash is not { Length: 64 })
                return null;

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in manifest.Outputs)
            {
                if (output == null
                    || !IsOwnedPath(output.RelativePath)
                    || !paths.Add(output.RelativePath)
                    || output.ContentHash is not { Length: 64 })
                    return null;
            }

            return manifest;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static bool IsHit(string outputDirectory, string cacheKey, CompileGenerationManifest? manifest)
        => MissReason(outputDirectory, cacheKey, manifest) is null;

    internal static string? MissReason(string outputDirectory, string cacheKey, CompileGenerationManifest? manifest)
    {
        if (manifest is null)
            return File.Exists(Path.Combine(outputDirectory, ManifestFileName)) ? "manifest invalid" : "manifest missing";
        if (manifest.CacheKey != cacheKey)
            return "inputs changed";
        if (manifest.Outputs == null || manifest.Outputs.Any(static output => output == null))
            return "manifest outputs invalid";

        var expectedSourceList = GetSourceList(manifest.Outputs.Select(static output => output!.RelativePath));
        var expectedSourceListHash = HashContent(expectedSourceList);
        var sourceListPath = Path.Combine(outputDirectory, SourceListFileName);
        var reportPath = Path.Combine(outputDirectory, ReportFileName);
        if (manifest.SourceListHash != expectedSourceListHash)
            return "source list manifest changed";
        if (!File.Exists(sourceListPath))
            return "source list missing";
        if (HashFile(sourceListPath) != expectedSourceListHash)
            return "source list changed";
        if (!File.Exists(reportPath))
            return "report missing";
        if (HashFile(reportPath) != manifest.ReportHash)
            return "report changed";

        foreach (var nullableOutput in manifest.Outputs.OrderBy(static output => output!.RelativePath, StringComparer.Ordinal))
        {
            var output = nullableOutput!;
            var path = Path.Combine(outputDirectory, output.RelativePath);
            if (!File.Exists(path))
                return $"artifact missing: {output.RelativePath}";
            if (HashFile(path) != output.ContentHash)
                return $"artifact changed: {output.RelativePath}";
        }

        return null;
    }

    internal static void Synchronize(
        string outputDirectory,
        string cacheKey,
        IReadOnlyList<CompileArtifact> artifacts,
        string report,
        CompileGenerationManifest? previous)
    {
        var expected = new Dictionary<string, CompileArtifact>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            if (!IsOwnedPath(artifact.RelativePath) || !expected.TryAdd(artifact.RelativePath, artifact))
                throw new InvalidOperationException($"Invalid generated artifact path '{artifact.RelativePath}'.");
        }

        Directory.CreateDirectory(outputDirectory);

        foreach (var artifact in expected.Values.OrderBy(static artifact => artifact.RelativePath, StringComparer.Ordinal))
            WriteIfChanged(Path.Combine(outputDirectory, artifact.RelativePath), artifact.Content);

        if (previous?.Outputs != null)
        {
            foreach (var output in previous.Outputs)
            {
                if (output == null)
                    continue;
                if (!expected.ContainsKey(output.RelativePath))
                {
                    var path = Path.Combine(outputDirectory, output.RelativePath);
                    if (File.Exists(path) && HashFile(path) == output.ContentHash)
                        File.Delete(path);
                }
            }
        }

        var sourceList = GetSourceList(expected.Keys);
        WriteIfChanged(Path.Combine(outputDirectory, SourceListFileName), sourceList);
        var normalizedReport = JobEmitter.Normalize(report);
        WriteIfChanged(Path.Combine(outputDirectory, ReportFileName), normalizedReport);

        var manifest = new CompileGenerationManifest
        {
            FormatVersion = FormatVersion,
            CacheKey = cacheKey,
            SourceListHash = HashContent(sourceList),
            ReportHash = HashContent(normalizedReport),
            Outputs = expected.Values
                .OrderBy(static artifact => artifact.RelativePath, StringComparer.Ordinal)
                .Select(static artifact => (CompileGenerationOutput?)new CompileGenerationOutput
                {
                    RelativePath = artifact.RelativePath,
                    ContentHash = HashContent(artifact.Content),
                })
                .ToList(),
        };

        WriteManifest(outputDirectory, manifest);
    }

    private static bool IsOwnedPath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)
            || Path.IsPathRooted(relativePath)
            || Path.GetFileName(relativePath) != relativePath
            || relativePath.Contains('/')
            || relativePath.Contains('\\')
            || relativePath.Contains('\r')
            || relativePath.Contains('\n'))
            return false;

        if (!relativePath.EndsWith(".g.cs", StringComparison.Ordinal))
            return false;

        return SyntaxFacts.IsValidIdentifier(relativePath[..^5]);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, Assembly assembly)
    {
        var identity = AssemblyIdentity(assembly.GetName());
        Append(hash, identity.Name);
        Append(hash, identity.Version);
        Append(hash, assembly.ManifestModule.ModuleVersionId.ToString("N"));
    }

    internal static (string Name, string Version) AssemblyIdentity(AssemblyName assembly)
        => (assembly.Name ?? "", assembly.Version?.ToString() ?? "");

    private static string HashContent(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string GetSourceList(IEnumerable<string> paths)
    {
        var content = string.Join('\n', paths.OrderBy(static path => path, StringComparer.Ordinal));
        return content.Length == 0 ? content : content + "\n";
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return;

        WriteAtomic(path, content);
    }

    private static void WriteManifest(string outputDirectory, CompileGenerationManifest manifest)
    {
        var path = Path.Combine(outputDirectory, ManifestFileName);
        var content = JobEmitter.Normalize(JsonSerializer.Serialize(manifest, CompileManifestJsonContext.Default.CompileGenerationManifest)) + "\n";
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return;

        WriteAtomic(path, content);
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
