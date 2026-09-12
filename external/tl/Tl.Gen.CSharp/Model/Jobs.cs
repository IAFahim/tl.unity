using Tl.Gen.CSharp.Analysis;

namespace Tl.Gen.CSharp.Model;

public enum SlotMode : byte
{
    Input,
    Reference,
}

public sealed record TimelineSlot(string Name, string TypeName, SlotMode Mode);

public sealed record HeterogeneousClip(
    int TrackIndex,
    string TypeName,
    string Expression,
    uint Start,
    uint End);

public sealed record JobDefinition(string TypeName, IReadOnlyList<TimelineSlot> Slots);

public sealed record JobConsumer(string TrackTypeName, string ClipTypeName, JobDefinition Job);
public sealed record JobTrack(
    int Index,
    string TypeName,
    string ClipTypeName,
    string Expression,
    JobDefinition Job);

public sealed record JobTimeline(
    string Name,
    string Namespace,
    bool Loops,
    IReadOnlyList<string> Usings,
    IReadOnlyList<JobTrack> Tracks,
    IReadOnlyList<HeterogeneousClip> Clips,
    IReadOnlyList<JobDefinition> Before,
    IReadOnlyList<JobDefinition> After)
{
    public uint Duration => Clips.Count == 0 ? 0u : Clips.Max(static clip => clip.End);
}

public sealed record JobSchema(string Name, IReadOnlyList<string> Assets);

public sealed record JobCatalog(
    string Name,
    string Namespace,
    IReadOnlyList<JobSchema> Schemas);

public sealed record JobReadResult(IReadOnlyList<JobTimeline> Timelines, IReadOnlyList<JobCatalog> Catalogs, IReadOnlyList<DeclarationDiagnostic> Diagnostics) { public IReadOnlyList<JobConsumer> Consumers { get; init; } = []; }
