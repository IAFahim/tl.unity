using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tl.Gen.CSharp.Analysis;
using Tl.Gen.CSharp.Model;

namespace Tl.Gen.CSharp;

internal sealed record CompilationReference(string Path, IReadOnlyList<string> Aliases, bool EmbedInteropTypes);

public static class GeneratorCli
{
    public static int Main(string[] args)
    {
        if (args is not ["--compile", ..])
            return Error("TLGEN00: --compile is required.");

        string? output = null;
        var backend = "csharp";
        var paths = new List<string>();
        var references = new List<CompilationReference>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbols = new SortedSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--output" && index + 1 < args.Length)
                output = args[++index];
            else if (args[index] == "--backend" && index + 1 < args.Length)
                backend = args[++index];
            else if (args[index] == "--source" && index + 1 < args.Length)
                paths.Add(args[++index]);
            else if (args[index] == "--source-list" && index + 1 < args.Length)
                paths.AddRange(File.ReadAllLines(args[++index]));
            else if (args[index] == "--reference-list" && index + 1 < args.Length)
                ReadReferences(args[++index], references);
            else if (args[index] == "--option-list" && index + 1 < args.Length)
                ReadOptions(args[++index], options);
            else if (args[index] == "--define" && index + 1 < args.Length)
                AddSymbols(args[++index], symbols);
            else
                return Error($"TLGEN00: invalid argument '{args[index]}'.");
        }

        if (string.IsNullOrWhiteSpace(output))
            return Error("TLGEN00: --output is required.");
        if (backend is not ("csharp" or "unity-entities"))
            return Error($"TLGEN00: unsupported backend '{backend}'.");

        var sources = paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal)
            .Select(static path => new CompileSource(path, File.ReadAllText(path))).ToArray();
        var semanticInputs = new[] { "backend=" + backend }.Concat(references.Select(ReferenceIdentity))
            .Concat(options.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => pair.Key + "=" + pair.Value)).ToArray();
        var key = CompileGenerationCache.GetKey(sources, symbols.ToArray(), semanticInputs);
        var previous = CompileGenerationCache.Load(output);
        var missReason = CompileGenerationCache.MissReason(output, key, previous);
        if (missReason is null)
        {
            Console.WriteLine($"TlGenCompile: cache hit; report {Path.Combine(output, CompileGenerationCache.ReportFileName)}");
            return 0;
        }
        Console.WriteLine($"TlGenCompile: cache miss ({missReason})");

        CSharpCompilation compilation;
        try
        {
            compilation = Compilation(sources, references, options, symbols);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return Error($"TLGEN00: {exception.Message}", 2);
        }

        var model = JobReader.Read(compilation);
        foreach (var diagnostic in model.Diagnostics)
            Console.Error.WriteLine(diagnostic);
        if (model.Diagnostics.Count != 0)
            return 2;

        CompileArtifact[] artifacts;
        try
        {
            artifacts = (backend == "unity-entities" ? UnityJobEmitter.Emit(model) : JobEmitter.Emit(model))
                .Select(static artifact => artifact with { Content = JobEmitter.Normalize(artifact.Content) }).ToArray();
        }
        catch (InvalidOperationException exception)
        {
            return Error($"TLUNITY01: {exception.Message}", 2);
        }
        var report = Report(model, artifacts, backend);
        CompileGenerationCache.Synchronize(output, key, artifacts, report, previous);
        var bytes = artifacts.Sum(static artifact => System.Text.Encoding.UTF8.GetByteCount(artifact.Content));
        Console.WriteLine($"TlGenCompile: {model.Timelines.Count} timeline(s), {model.Catalogs.Count} catalog(s), {artifacts.Length} source file(s), {bytes:N0} UTF-8 B; report {Path.Combine(output, CompileGenerationCache.ReportFileName)}");
        return 0;
    }

    private static CSharpCompilation Compilation(
        IReadOnlyList<CompileSource> sources,
        IReadOnlyList<CompilationReference> references,
        IReadOnlyDictionary<string, string> options,
        IEnumerable<string> symbols)
    {
        var language = LanguageVersion.Preview;
        if (options.TryGetValue("language-version", out var configured) && !LanguageVersionFacts.TryParse(configured, out language))
            throw new ArgumentException($"Invalid C# language version '{configured}'.");
        var parse = new CSharpParseOptions(language, preprocessorSymbols: symbols);
        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source.Content, parse, source.Path));
        foreach (var reference in references)
        {
            using var stream = File.OpenRead(reference.Path);
            using var image = new PEReader(stream);
            _ = image.GetMetadataReader();
        }
        var metadata = references.Select(static reference => MetadataReference.CreateFromFile(
            reference.Path,
            new MetadataReferenceProperties(
                MetadataImageKind.Assembly,
                reference.Aliases.ToImmutableArray(),
                reference.EmbedInteropTypes)));
        var nullable = options.TryGetValue("nullable", out var nullableValue) ? Nullable(nullableValue) : NullableContextOptions.Enable;
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: options.TryGetValue("allow-unsafe", out var unsafeValue) && Boolean(unsafeValue),
            checkOverflow: options.TryGetValue("check-overflow", out var overflowValue) && Boolean(overflowValue),
            nullableContextOptions: nullable);
        return CSharpCompilation.Create("Tl.Generated.Authoring", trees, metadata, compilationOptions);
    }

    private static NullableContextOptions Nullable(string value)
        => value.ToLowerInvariant() switch
        {
            "disable" => NullableContextOptions.Disable,
            "annotations" => NullableContextOptions.Annotations,
            "warnings" => NullableContextOptions.Warnings,
            "enable" => NullableContextOptions.Enable,
            _ => throw new ArgumentException($"Invalid nullable mode '{value}'."),
        };

    private static string Report(JobReadResult model, IReadOnlyList<CompileArtifact> artifacts, string backend)
    {
        var writer = new System.Text.StringBuilder();
        var sourceBytes = artifacts.Sum(static artifact => System.Text.Encoding.UTF8.GetByteCount(artifact.Content));
        writer.AppendLine("format\t2");
        writer.Append("backend\t").AppendLine(backend);
        writer.AppendLine($"timelines\t{model.Timelines.Count}");
        writer.AppendLine($"catalogs\t{model.Catalogs.Count}");
        writer.AppendLine($"generated-source-files\t{artifacts.Count}");
        writer.AppendLine($"generated-source-utf8-bytes\t{sourceBytes}");
        foreach (var timeline in model.Timelines.OrderBy(Qualified, StringComparer.Ordinal))
        {
            var qualified = Qualified(timeline);
            var plan = JobTimelinePlanAdapter.Create(timeline).Plan;
            writer.Append("timeline\t").Append(qualified)
                .Append("\ttracks=").Append(timeline.Tracks.Count)
                .Append("\tclips=").Append(timeline.Clips.Count)
                .Append("\tduration=").Append(timeline.Duration)
                .Append("\tloops=").Append(timeline.Loops ? "true" : "false")
                .Append("\toperations=").Append(plan.Operations.Length)
                .Append("\tslots=").Append(plan.Slots.Length)
                .Append("\tregions=").Append(plan.Regions.Length)
                .Append("\toccurrences=").Append(plan.Occurrences.Length)
                .Append("\tunique-schedules=").Append(plan.UniqueScheduleCount)
                .Append("\tunique-payloads=").Append(plan.UniquePayloads.Length)
                .Append("\tneutral-payload-bytes=").Append(plan.PayloadBytes)
                .Append("\tneutral-schedule-bytes=").Append(plan.ScheduleBytes)
                .Append("\tstatic-data-bytes=").Append(qualified).AppendLine(".StaticDataBytes");
        }
        foreach (var catalog in model.Catalogs.OrderBy(Qualified, StringComparer.Ordinal))
        {
            var qualified = Qualified(catalog);
            writer.Append("catalog\t").Append(qualified)
                .Append("\tschemas=").Append(catalog.Schemas.Count)
                .Append("\tassets=").Append(catalog.Schemas.Sum(static schema => schema.Assets.Count).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (backend == "unity-entities")
            {
                var timelines = new Dictionary<string, JobTimeline>(model.Timelines.Count, StringComparer.Ordinal);
                foreach (var timeline in model.Timelines)
                    timelines.Add("global::" + Qualified(timeline), timeline);
                var catalogAssets = catalog.Schemas.SelectMany(static schema => schema.Assets)
                    .Distinct(StringComparer.Ordinal)
                    .Select(asset => timelines[asset])
                    .ToArray();
                var operationKinds = catalogAssets.SelectMany(asset => JobTimelinePlanAdapter.Create(asset).OperationBindings)
                    .Select(static operation => operation.TypeName).Distinct(StringComparer.Ordinal).Count();
                var maxStages = catalogAssets.Select(asset => JobTimelinePlanAdapter.Create(asset).Plan.Regions
                        .Select(static region => (int)region.OccurrenceCount).DefaultIfEmpty().Max())
                    .DefaultIfEmpty().Max();
                var scheduledJobs = 2 + catalog.Schemas.Count + maxStages * operationKinds;
                writer.Append("\toperation-kinds=").Append(operationKinds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     .Append("\tmax-stages=").Append(maxStages.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     .Append("\tscheduled-jobs-per-step=").Append(scheduledJobs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            writer.Append("\tstate-bytes=").Append(qualified).AppendLine(".StateBytes");
        }
        foreach (var artifact in artifacts.OrderBy(static artifact => artifact.RelativePath, StringComparer.Ordinal))
            writer.Append("artifact\t").Append(artifact.RelativePath).Append("\tutf8-bytes=")
                .AppendLine(System.Text.Encoding.UTF8.GetByteCount(artifact.Content).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return writer.ToString();
    }

    private static string Qualified(JobTimeline timeline)
        => (timeline.Namespace.Length == 0 ? "" : timeline.Namespace + ".") + timeline.Name;

    private static string Qualified(JobCatalog catalog)
        => (catalog.Namespace.Length == 0 ? "" : catalog.Namespace + ".") + catalog.Name;

    private static void AddSymbols(string value, ISet<string> symbols)
    {
        foreach (var symbol in value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            symbols.Add(symbol);
    }

    private static void ReadOptions(string path, IDictionary<string, string> options)
    {
        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
                options[line[..separator]] = line[(separator + 1)..];
        }
    }

    private static void ReadReferences(string path, ICollection<CompilationReference> references)
    {
        foreach (var line in File.ReadAllLines(path))
        {
            var fields = line.Split('\t');
            var aliases = fields.Length > 1
                ? fields[1].Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            references.Add(new(Path.GetFullPath(fields[0]), aliases, fields.Length > 2 && Boolean(fields[2])));
        }
    }

    private static bool Boolean(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string ReferenceIdentity(CompilationReference reference)
    {
        try
        {
            using var stream = File.OpenRead(reference.Path);
            return reference.Path + "\t" + string.Join(",", reference.Aliases) + "\t" + reference.EmbedInteropTypes + "=" + Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return reference.Path + "=" + exception.GetType().Name;
        }
    }

    private static int Error(string message, int code = 1)
    {
        Console.Error.WriteLine(message);
        return code;
    }
}
