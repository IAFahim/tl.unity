using System.Collections.Immutable;

namespace Tl.Compiler;

public sealed record ValidatedOrderedTimelinePlan
{
    public const uint NoPayload = uint.MaxValue;
    private const int RegionBytes = 16;
    private const int OccurrenceBytes = 24;

    private ValidatedOrderedTimelinePlan(
        OrderedTimelinePlan plan,
        uint duration,
        ImmutableArray<OperationSlotPlan> slots,
        ImmutableArray<PayloadPlan> uniquePayloads,
        ImmutableArray<uint> payloadStorageIndices,
        ImmutableArray<OrderedRegionPlan> regions,
        ImmutableArray<OrderedOccurrencePlan> occurrences,
        int uniqueScheduleCount)
    {
        Identity = plan.Identity;
        Loops = plan.Loops;
        Duration = duration;
        Operations = plan.Operations;
        Payloads = plan.Payloads;
        UniquePayloads = uniquePayloads;
        PayloadStorageIndices = payloadStorageIndices;
        Slots = slots;
        Tracks = plan.Tracks;
        Clips = plan.Clips;
        Hooks = plan.Hooks;
        Regions = regions;
        Occurrences = occurrences;
        UniqueScheduleCount = uniqueScheduleCount;
        PayloadBytes = uniquePayloads.Aggregate(0UL, static (total, payload) => total + (ulong)payload.Bytes.Length);
        ScheduleBytes = (ulong)regions.Length * RegionBytes + (ulong)occurrences.Length * OccurrenceBytes;
        WorstCaseTraversal = "O(entities * operation kinds * stages)";
        FormatVersion = plan.FormatVersion;
    }

    public string Identity { get; }
    public bool Loops { get; }
    public uint Duration { get; }
    public ImmutableArray<OrderedOperationPlan> Operations { get; }
    public ImmutableArray<PayloadPlan> Payloads { get; }
    public ImmutableArray<PayloadPlan> UniquePayloads { get; }
    public ImmutableArray<uint> PayloadStorageIndices { get; }
    public ImmutableArray<OperationSlotPlan> Slots { get; }
    public ImmutableArray<OrderedTrackPlan> Tracks { get; }
    public ImmutableArray<OrderedClipPlan> Clips { get; }
    public ImmutableArray<OrderedHookPlan> Hooks { get; }
    public ImmutableArray<OrderedRegionPlan> Regions { get; }
    public ImmutableArray<OrderedOccurrencePlan> Occurrences { get; }
    public int UniqueScheduleCount { get; }
    public ulong PayloadBytes { get; }
    public ulong ScheduleBytes { get; }
    public string WorstCaseTraversal { get; }
    public ushort FormatVersion { get; }

    internal static ValidatedOrderedTimelinePlan Create(OrderedTimelinePlan plan)
    {
        if (plan.FormatVersion != OrderedTimelinePlan.CurrentFormatVersion)
            throw Error($"Ordered timeline plan format {plan.FormatVersion} is not supported. Expected {OrderedTimelinePlan.CurrentFormatVersion}.");
        if (string.IsNullOrWhiteSpace(plan.Identity))
            throw Error("Ordered timeline identity cannot be empty.");
        if (plan.Tracks.Length > 256)
            throw Error("An ordered timeline may contain at most 256 authored tracks.");
        if (plan.Operations.Length > ushort.MaxValue + 1)
            throw Error("An ordered timeline may contain at most 65,536 operations.");

        var operationIndexes = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var roleTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var slots = new List<OperationSlotPlan>();
        for (var index = 0; index < plan.Operations.Length; index++)
        {
            var operation = plan.Operations[index] ?? throw Error($"Operation at index {index} is null.");
            if (string.IsNullOrWhiteSpace(operation.Id.Value))
                throw Error($"Operation at index {index} has no identity.");
            if (operationIndexes.ContainsKey(operation.Id.Value))
                throw Error($"Operation '{operation.Id.Value}' is duplicated.");
            operationIndexes.Add(operation.Id.Value, (ushort)index);
            var localRoles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slot in operation.Slots)
            {
                if (string.IsNullOrWhiteSpace(slot.Role))
                    throw Error($"Operation '{operation.Id.Value}' has an empty slot role.");
                if (string.IsNullOrWhiteSpace(slot.Type.Value))
                    throw Error($"Operation '{operation.Id.Value}' slot '{slot.Role}' has no type identity.");
                if (slot.Access is not SlotAccess.Read and not SlotAccess.Write and not SlotAccess.ReadWrite)
                    throw Error($"Operation '{operation.Id.Value}' slot '{slot.Role}' has invalid access {((byte)slot.Access)}.");
                if (!localRoles.Add(slot.Role))
                    throw Error($"Operation '{operation.Id.Value}' maps slot role '{slot.Role}' more than once.");
                if (roleTypes.TryGetValue(slot.Role, out var type) && !StringComparer.Ordinal.Equals(type, slot.Type.Value))
                    throw Error($"Slot role '{slot.Role}' maps to both '{type}' and '{slot.Type.Value}'.");
                roleTypes[slot.Role] = slot.Type.Value;
                var joined = slots.FindIndex(item => item.Role == slot.Role && item.Type == slot.Type);
                if (joined < 0)
                    slots.Add(slot);
                else
                    slots[joined] = slots[joined] with { Access = slots[joined].Access | slot.Access };
            }
        }

        var payloadIndexes = new Dictionary<uint, uint>();
        for (var index = 0; index < plan.Payloads.Length; index++)
        {
            var payload = plan.Payloads[index] ?? throw Error($"Payload at index {index} is null.");
            if (string.IsNullOrWhiteSpace(payload.Type.Value))
                throw Error($"Payload {payload.Id.Value} has no type identity.");
            if (payload.Bytes.IsEmpty)
                throw Error($"Payload {payload.Id.Value} has an empty constant encoding.");
            if (payloadIndexes.ContainsKey(payload.Id.Value))
                throw Error($"Payload identity {payload.Id.Value} is duplicated.");
            payloadIndexes.Add(payload.Id.Value, (uint)index);
        }

        var tracks = new HashSet<byte>();
        foreach (var track in plan.Tracks)
        {
            if (!tracks.Add(track.Index))
                throw Error($"Authored track index {track.Index} is duplicated.");
            RequireOperation(operationIndexes, track.Operation, $"Track index {track.Index}");
            RequirePayload(payloadIndexes, track.Payload, $"Track index {track.Index}");
        }
        foreach (var clip in plan.Clips)
        {
            if (!tracks.Contains(clip.TrackIndex))
                throw Error($"Clip track index {clip.TrackIndex} does not exist.");
            RequirePayload(payloadIndexes, clip.Payload, $"Clip on track index {clip.TrackIndex}");
            if (clip.Start >= clip.End)
                throw Error($"Clip [{clip.Start}, {clip.End}) is empty or reversed.");
        }
        foreach (var hook in plan.Hooks)
        {
            RequireOperation(operationIndexes, hook.Operation, $"{hook.Phase} hook");
            if (hook.Phase is not HookPhase.Before and not HookPhase.After)
                throw Error($"Hook operation '{hook.Operation.Value}' has invalid phase {((byte)hook.Phase)}.");
        }
        ValidateOverlaps(plan);
        var duration = plan.Clips.IsEmpty ? 0u : plan.Clips.Max(static clip => clip.End);
        var (uniquePayloads, payloadStorageIndices) = DeduplicatePayloads(plan.Payloads);
        var (regions, occurrences, uniqueScheduleCount) = Lower(plan, duration, operationIndexes, payloadIndexes);
        return new(plan, duration, [.. slots], uniquePayloads, payloadStorageIndices, regions, occurrences, uniqueScheduleCount);
    }

    private static void ValidateOverlaps(OrderedTimelinePlan plan)
    {
        foreach (var track in plan.Tracks)
        {
            var events = plan.Clips.Where(clip => clip.TrackIndex == track.Index)
                .SelectMany(static clip => new[] { (clip.Start, Delta: 1), (clip.End, Delta: -1) })
                .GroupBy(static item => item.Item1).OrderBy(static group => group.Key);
            var active = 0;
            foreach (var group in events)
            {
                active += group.Where(static item => item.Delta < 0).Sum(static item => item.Delta);
                active += group.Where(static item => item.Delta > 0).Sum(static item => item.Delta);
                if (active > 2)
                    throw Error($"Authored track index {track.Index} has more than two overlapping clips.");
            }
        }
    }

    private static (ImmutableArray<PayloadPlan>, ImmutableArray<uint>) DeduplicatePayloads(ImmutableArray<PayloadPlan> payloads)
    {
        var unique = new List<PayloadPlan>();
        var storage = ImmutableArray.CreateBuilder<uint>(payloads.Length);
        foreach (var payload in payloads)
        {
            var index = unique.FindIndex(candidate => candidate.Type == payload.Type && candidate.Bytes.SequenceEqual(payload.Bytes));
            if (index < 0)
            {
                index = unique.Count;
                unique.Add(payload);
            }
            storage.Add((uint)index);
        }
        return ([.. unique], storage.MoveToImmutable());
    }

    private static (ImmutableArray<OrderedRegionPlan>, ImmutableArray<OrderedOccurrencePlan>, int) Lower(
        OrderedTimelinePlan plan,
        uint duration,
        IReadOnlyDictionary<string, ushort> operations,
        IReadOnlyDictionary<uint, uint> payloads)
    {
        if (duration == 0)
            return ([], [], 0);
        var cuts = new SortedSet<uint> { 0u, duration };
        foreach (var clip in plan.Clips)
        {
            cuts.Add(clip.Start);
            cuts.Add(clip.End);
        }
        var boundaries = cuts.ToArray();
        var regions = ImmutableArray.CreateBuilder<OrderedRegionPlan>(boundaries.Length - 1);
        var occurrences = ImmutableArray.CreateBuilder<OrderedOccurrencePlan>();
        var schedules = new List<(uint Offset, ushort Count)>();
        for (var region = 0; region + 1 < boundaries.Length; region++)
        {
            var current = new List<OrderedOccurrencePlan>();
            AddHooks(current, plan.Hooks, HookPhase.Before, operations, OrderedOccurrenceFlags.BeforeHook);
            foreach (var track in plan.Tracks)
            {
                var active = plan.Clips.Select(static (clip, authored) => (clip, authored))
                    .Where(item => item.clip.TrackIndex == track.Index && item.clip.Start <= boundaries[region] && boundaries[region] < item.clip.End)
                    .OrderBy(static item => item.clip.Start).ThenBy(static item => item.authored).Select(static item => item.clip).ToArray();
                if (active.Length == 0)
                    continue;
                var second = active.Length == 2 ? payloads[active[1].Payload.Value] : NoPayload;
                var factorStart = active.Length == 2 ? Math.Max(active[0].Start, active[1].Start) : 0u;
                current.Add(new(
                    operations[track.Operation.Value],
                    track.Index,
                    active.Length == 2 ? OrderedOccurrenceFlags.Blend : OrderedOccurrenceFlags.None,
                    payloads[track.Payload.Value],
                    payloads[active[0].Payload.Value],
                    second,
                    factorStart,
                    active.Length == 2 ? Math.Min(active[0].End, active[1].End) - factorStart : 0u));
            }
            AddHooks(current, plan.Hooks, HookPhase.After, operations, OrderedOccurrenceFlags.AfterHook);
            if (current.Count > ushort.MaxValue)
                throw Error($"Region [{boundaries[region]}, {boundaries[region + 1]}) has more than 65,535 operation occurrences.");
            var offset = FindSchedule(occurrences, schedules, current);
            if (offset == uint.MaxValue)
            {
                offset = (uint)occurrences.Count;
                occurrences.AddRange(current);
                schedules.Add((offset, (ushort)current.Count));
            }
            regions.Add(new(boundaries[region], boundaries[region + 1], offset, (ushort)current.Count));
        }
        return (regions.MoveToImmutable(), occurrences.ToImmutable(), schedules.Count);
    }

    private static uint FindSchedule(
        ImmutableArray<OrderedOccurrencePlan>.Builder occurrences,
        IEnumerable<(uint Offset, ushort Count)> schedules,
        IReadOnlyList<OrderedOccurrencePlan> candidate)
    {
        foreach (var schedule in schedules)
        {
            if (schedule.Count != candidate.Count)
                continue;
            var equal = true;
            for (var index = 0; index < candidate.Count; index++)
                equal &= occurrences[(int)schedule.Offset + index] == candidate[index];
            if (equal)
                return schedule.Offset;
        }
        return uint.MaxValue;
    }

    private static void AddHooks(
        ICollection<OrderedOccurrencePlan> target,
        IEnumerable<OrderedHookPlan> hooks,
        HookPhase phase,
        IReadOnlyDictionary<string, ushort> operations,
        OrderedOccurrenceFlags flags)
    {
        foreach (var hook in hooks.Where(hook => hook.Phase == phase))
            target.Add(new(operations[hook.Operation.Value], 0, flags, NoPayload, NoPayload, NoPayload, 0, 0));
    }

    private static void RequireOperation(IReadOnlyDictionary<string, ushort> operations, OperationId id, string owner)
    {
        if (string.IsNullOrWhiteSpace(id.Value) || !operations.ContainsKey(id.Value))
            throw Error($"{owner} maps unknown operation '{id.Value}'.");
    }

    private static void RequirePayload(IReadOnlyDictionary<uint, uint> payloads, PayloadId id, string owner)
    {
        if (!payloads.ContainsKey(id.Value))
            throw Error($"{owner} maps unknown payload {id.Value}.");
    }

    private static ArgumentException Error(string message) => new(message, "plan");
}
