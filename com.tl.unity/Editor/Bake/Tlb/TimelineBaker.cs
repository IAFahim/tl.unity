using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace Tl.Gen.Tlb;

internal readonly record struct BatchOutcome(byte[]? Bytes, Exception? Error, double Ms);

public static class TimelineBaker
{
    public static byte[] BakeJson(string json, BakerAssemblyResolver? resolver = null, bool autoNamespace = false, LibraryManifest? manifest = null)
    {
        resolver ??= new BakerAssemblyResolver();
        return TimelineBakerFast.BakeJsonUtf8(Encoding.UTF8.GetBytes(json), resolver, null, autoNamespace, manifest);
    }

    public static byte[] BakeJson(byte[] utf8Json, BakerAssemblyResolver? resolver = null, bool autoNamespace = false, LibraryManifest? manifest = null)
    {
        if (NetCompat.FirstInvalidUtf8(utf8Json) >= 0)
            throw new BakeDiagnosticException($"invalid UTF-8 in authoring JSON at byte {NetCompat.FirstInvalidUtf8(utf8Json)}: the bake input must be valid UTF-8; fix the input file encoding.");
        resolver ??= new BakerAssemblyResolver();
        return TimelineBakerFast.BakeJsonUtf8(utf8Json, resolver, null, autoNamespace, manifest);
    }

    private static byte[] BakeValidated(byte[] utf8Json, BakerAssemblyResolver resolver, bool autoNamespace, LibraryManifest? manifest = null)
    {
        if (NetCompat.FirstInvalidUtf8(utf8Json) >= 0)
            throw new BakeDiagnosticException($"invalid UTF-8 in authoring JSON at byte {NetCompat.FirstInvalidUtf8(utf8Json)}: the bake input must be valid UTF-8; fix the input file encoding.");
        return TimelineBakerFast.BakeJsonUtf8(utf8Json, resolver, BakeWorkspace.Shared, autoNamespace, manifest);
    }

    public static byte[] BakeTyped(TypedTimeline timeline, BakerAssemblyResolver? resolver = null, LibraryManifest? manifest = null)
    {
        if (timeline == null)
            throw new ArgumentNullException(nameof(timeline));
        TypeNames.Unique(timeline.Tracks.Select(t => t.Type).Concat(timeline.Tracks.SelectMany(t => t.Clips).Select(c => c.Type)).Concat(timeline.Markers.Select(m => m.Type)).Where(t => t != null));
        return TypedBake.Bake(timeline, resolver ?? BakerAssemblyResolver.FromAssemblies([]), manifest);
    }

    public static byte[][] BakeJsonBatch(IReadOnlyList<byte[]> utf8Jsons, BakerAssemblyResolver? resolver = null, bool autoNamespace = false, LibraryManifest? manifest = null)
    {
        resolver ??= new BakerAssemblyResolver();
        var outcomes = BakeBatch(utf8Jsons, resolver, autoNamespace, manifest);
        foreach (var outcome in outcomes)
            if (outcome.Error != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outcome.Error!).Throw();
        var outputs = new byte[outcomes.Length][];
        for (var i = 0; i < outcomes.Length; i++)
            outputs[i] = outcomes[i].Bytes!;
        return outputs;
    }

    private static BatchOutcome BakeOne(byte[] utf8Json, BakerAssemblyResolver resolver, bool autoNamespace, LibraryManifest? manifest = null)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var bytes = BakeValidated(utf8Json, resolver, autoNamespace, manifest);
            return new BatchOutcome(bytes, null, NetCompat.ElapsedMs(start));
        }
        catch (Exception error)
        {
            return new BatchOutcome(null, error, NetCompat.ElapsedMs(start));
        }
    }

    [SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "closures run before the dispose later in the same method")]
    internal static BatchOutcome[] BakeBatch(IReadOnlyList<byte[]> utf8Jsons, BakerAssemblyResolver resolver, bool autoNamespace = false, LibraryManifest? manifest = null)
    {
        var outcomes = new BatchOutcome[utf8Jsons.Count];
        if (utf8Jsons.Count == 0)
            return outcomes;
        var workers = Math.Min(utf8Jsons.Count, Environment.ProcessorCount);
        if (workers <= 1)
        {
            for (var i = 0; i < utf8Jsons.Count; i++)
                outcomes[i] = BakeOne(utf8Jsons[i], resolver, autoNamespace, manifest);
            return outcomes;
        }
        using var done = new CountdownEvent(workers);
        for (var w = 0; w < workers; w++)
        {
            var worker = w;
            new Thread(() =>
            {
                for (var i = worker; i < utf8Jsons.Count; i += workers)
                    outcomes[i] = BakeOne(utf8Jsons[i], resolver, autoNamespace, manifest);
                done.Signal();
            }).Start();
        }
        done.Wait();
        return outcomes;
    }
}
