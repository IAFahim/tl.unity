using System.Collections.Immutable;

namespace Tl.Compiler;

public readonly record struct OperationId(string Value);

public readonly record struct TrackPlan(
    ushort Index,
    uint Payload,
    OperationId Operation);

public readonly record struct ClipPlan(
    ushort TrackIndex,
    uint Payload,
    uint Start,
    uint End);

public sealed record TimelinePlan
{
    public const ushort CurrentFormatVersion = 1;

    public TimelinePlan(
        string identity,
        ushort runtimeId,
        bool loops,
        IEnumerable<TrackPlan> tracks,
        IEnumerable<ClipPlan> clips,
        ushort formatVersion = CurrentFormatVersion)
    {
        if (identity is null)
            throw new ArgumentNullException(nameof(identity));
        if (tracks is null)
            throw new ArgumentNullException(nameof(tracks));
        if (clips is null)
            throw new ArgumentNullException(nameof(clips));
        Identity = identity;
        RuntimeId = runtimeId;
        Loops = loops;
        Tracks = [.. tracks];
        Clips = [.. clips];
        FormatVersion = formatVersion;
    }

    public string Identity { get; }
    public ushort RuntimeId { get; }
    public bool Loops { get; }
    public ImmutableArray<TrackPlan> Tracks { get; }
    public ImmutableArray<ClipPlan> Clips { get; }
    public ushort FormatVersion { get; }
    public uint Duration => Clips.IsEmpty ? 0u : Clips.Max(static clip => clip.End);
    public ValidatedTimelinePlan Validate() => ValidatedTimelinePlan.Create(this);
}
