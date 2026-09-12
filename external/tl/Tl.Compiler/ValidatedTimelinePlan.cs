using System.Collections.Immutable;

namespace Tl.Compiler;

public readonly record struct RegionWorkPlan(
    TrackPlan Track,
    ClipPlan First,
    ClipPlan? Second,
    uint FactorStart,
    uint FactorLength);

public readonly record struct RegionPlan(
    uint Start,
    uint End,
    ImmutableArray<RegionWorkPlan> Works);

public sealed record ValidatedTimelinePlan
{
    private ValidatedTimelinePlan(TimelinePlan plan, ImmutableArray<RegionPlan> regions)
    {
        Identity = plan.Identity;
        RuntimeId = plan.RuntimeId;
        Loops = plan.Loops;
        Tracks = plan.Tracks;
        Clips = plan.Clips;
        Duration = plan.Duration;
        Regions = regions;
        FormatVersion = plan.FormatVersion;
    }

    public string Identity { get; }
    public ushort RuntimeId { get; }
    public bool Loops { get; }
    public ImmutableArray<TrackPlan> Tracks { get; }
    public ImmutableArray<ClipPlan> Clips { get; }
    public uint Duration { get; }
    public ImmutableArray<RegionPlan> Regions { get; }
    public ushort FormatVersion { get; }

    internal static ValidatedTimelinePlan Create(TimelinePlan plan)
    {
        if (plan.FormatVersion != TimelinePlan.CurrentFormatVersion)
            throw new NotSupportedException($"Timeline plan format {plan.FormatVersion} is not supported. Expected {TimelinePlan.CurrentFormatVersion}.");
        if (string.IsNullOrWhiteSpace(plan.Identity))
            throw new ArgumentException("Timeline identity cannot be empty.", nameof(plan));
        if (plan.Tracks.Length > ushort.MaxValue + 1)
            throw new ArgumentException("A timeline may contain at most 65,536 tracks.", nameof(plan));

        var tracks = new HashSet<ushort>();
        foreach (var track in plan.Tracks)
        {
            if (!tracks.Add(track.Index))
                throw new ArgumentException($"Track index {track.Index} is duplicated.", nameof(plan));
            if (string.IsNullOrWhiteSpace(track.Operation.Value))
                throw new ArgumentException($"Track index {track.Index} has no operation.", nameof(plan));
        }

        foreach (var clip in plan.Clips)
        {
            if (!tracks.Contains(clip.TrackIndex))
                throw new ArgumentException($"Clip track index {clip.TrackIndex} does not exist.", nameof(plan));
            if (clip.Start >= clip.End)
                throw new ArgumentException($"Clip [{clip.Start}, {clip.End}) is empty or reversed.", nameof(plan));
        }

        foreach (var track in plan.Tracks)
        {
            var events = plan.Clips
                .Where(clip => clip.TrackIndex == track.Index)
                .SelectMany(static clip => new[] { (Tick: clip.Start, Delta: 1), (Tick: clip.End, Delta: -1) })
                .GroupBy(static item => item.Tick)
                .OrderBy(static group => group.Key);
            var active = 0;
            foreach (var group in events)
            {
                active += group.Where(static item => item.Delta < 0).Sum(static item => item.Delta);
                active += group.Where(static item => item.Delta > 0).Sum(static item => item.Delta);
                if (active > 2)
                    throw new ArgumentException($"Track index {track.Index} has more than two overlapping clips.", nameof(plan));
            }
        }

        return new(plan, LowerRegions(plan));
    }

    private static ImmutableArray<RegionPlan> LowerRegions(TimelinePlan plan)
    {
        if (plan.Duration == 0)
            return [];
        var cuts = plan.Clips
            .SelectMany(static clip => new[] { clip.Start, clip.End })
            .Append(0u)
            .Append(plan.Duration)
            .Distinct()
            .OrderBy(static tick => tick)
            .ToArray();
        var clips = plan.Clips
            .Select(static (clip, authored) => (Clip: clip, Authored: authored))
            .ToArray();
        var regions = ImmutableArray.CreateBuilder<RegionPlan>(cuts.Length - 1);
        for (var index = 0; index + 1 < cuts.Length; index++)
        {
            var start = cuts[index];
            var end = cuts[index + 1];
            var works = ImmutableArray.CreateBuilder<RegionWorkPlan>();
            foreach (var track in plan.Tracks)
            {
                var active = clips
                    .Where(item => item.Clip.TrackIndex == track.Index && item.Clip.Start <= start && start < item.Clip.End)
                    .OrderBy(static item => item.Clip.Start)
                    .ThenBy(static item => item.Authored)
                    .Select(static item => item.Clip)
                    .ToArray();
                if (active.Length == 1)
                {
                    var clip = active[0];
                    works.Add(new(track, clip, null, 0u, 0u));
                }
                else if (active.Length == 2)
                {
                    var first = active[0];
                    var second = active[1];
                    var factorStart = Math.Max(first.Start, second.Start);
                    var factorEnd = Math.Min(first.End, second.End);
                    works.Add(new(
                        track,
                        first,
                        second,
                        factorStart,
                        factorEnd - factorStart));
                }
            }
            regions.Add(new(start, end, works.ToImmutable()));
        }
        return regions.MoveToImmutable();
    }
}
