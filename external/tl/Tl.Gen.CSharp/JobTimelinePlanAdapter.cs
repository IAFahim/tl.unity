using System.Collections.Immutable;
using Tl.Compiler;
using Tl.Gen.CSharp.Model;

namespace Tl.Gen.CSharp;

internal sealed record CSharpPayloadBinding(string TypeName, string Expression);

internal sealed record BoundOrderedTimelinePlan(
    ValidatedOrderedTimelinePlan Plan,
    ImmutableArray<JobDefinition> OperationBindings,
    ImmutableArray<CSharpPayloadBinding> PayloadBindings);

internal static class JobTimelinePlanAdapter
{
    internal static BoundOrderedTimelinePlan Create(JobTimeline timeline)
    {
        if (timeline is null)
            throw new ArgumentNullException(nameof(timeline));

        var typeIds = new Dictionary<string, TypeId>(StringComparer.Ordinal);
        var operationIds = new Dictionary<string, OperationId>(StringComparer.Ordinal);
        var operations = ImmutableArray.CreateBuilder<OrderedOperationPlan>();
        var operationBindings = ImmutableArray.CreateBuilder<JobDefinition>();
        foreach (var operation in timeline.Before.Concat(timeline.Tracks.Select(static track => track.Job)).Concat(timeline.After))
        {
            if (operationIds.TryGetValue(operation.TypeName, out _))
            {
                var existing = operationBindings.First(candidate => StringComparer.Ordinal.Equals(candidate.TypeName, operation.TypeName));
                if (!existing.Slots.SequenceEqual(operation.Slots))
                    throw new ArgumentException($"Operation '{operation.TypeName}' has inconsistent slot bindings.", nameof(timeline));
                continue;
            }

            var id = new OperationId("operation:" + operations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            operationIds.Add(operation.TypeName, id);
            operations.Add(new OrderedOperationPlan(id, operation.Slots.Select(slot => new OperationSlotPlan(
                slot.Name,
                Type(slot.TypeName, typeIds),
                slot.Mode == SlotMode.Input ? SlotAccess.Read : SlotAccess.ReadWrite))));
            operationBindings.Add(operation);
        }

        var payloads = ImmutableArray.CreateBuilder<PayloadPlan>(timeline.Tracks.Count + timeline.Clips.Count);
        var payloadBindings = ImmutableArray.CreateBuilder<CSharpPayloadBinding>(payloads.Capacity);
        foreach (var track in timeline.Tracks)
            AddPayload(track.TypeName, track.Expression, typeIds, payloads, payloadBindings);
        foreach (var clip in timeline.Clips)
            AddPayload(clip.TypeName, clip.Expression, typeIds, payloads, payloadBindings);

        var tracks = timeline.Tracks.Select((track, index) => new OrderedTrackPlan(
            TrackIndex(track.Index, nameof(timeline)),
            new PayloadId((uint)index),
            operationIds[track.Job.TypeName]));
        var clips = timeline.Clips.Select((clip, index) => new OrderedClipPlan(
            TrackIndex(clip.TrackIndex, nameof(timeline)),
            new PayloadId((uint)(timeline.Tracks.Count + index)),
            clip.Start,
            clip.End));
        var hooks = timeline.Before.Select(hook => new OrderedHookPlan(operationIds[hook.TypeName], HookPhase.Before))
            .Concat(timeline.After.Select(hook => new OrderedHookPlan(operationIds[hook.TypeName], HookPhase.After)));
        var plan = new OrderedTimelinePlan(
            "timeline:0",
            timeline.Loops,
            operations,
            payloads,
            tracks,
            clips,
            hooks).Validate();
        return new(plan, operationBindings.ToImmutable(), payloadBindings.MoveToImmutable());
    }

    private static TypeId Type(string typeName, IDictionary<string, TypeId> ids)
    {
        if (ids.TryGetValue(typeName, out var id))
            return id;
        id = new TypeId("type:" + ids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ids.Add(typeName, id);
        return id;
    }

    private static void AddPayload(
        string typeName,
        string expression,
        IDictionary<string, TypeId> typeIds,
        ImmutableArray<PayloadPlan>.Builder payloads,
        ImmutableArray<CSharpPayloadBinding>.Builder bindings)
    {
        var equivalence = -1;
        for (var index = 0; index < bindings.Count; index++)
            if (StringComparer.Ordinal.Equals(bindings[index].TypeName, typeName) &&
                StringComparer.Ordinal.Equals(bindings[index].Expression, expression))
            {
                equivalence = index;
                break;
            }
        if (equivalence < 0)
            equivalence = bindings.Count;
        var value = (uint)equivalence;
        var id = new PayloadId((uint)payloads.Count);
        payloads.Add(new PayloadPlan(id, Type(typeName, typeIds),
        [
            (byte)value,
            (byte)(value >> 8),
            (byte)(value >> 16),
            (byte)(value >> 24),
        ]));
        bindings.Add(new(typeName, expression));
    }

    private static byte TrackIndex(int index, string parameterName)
    {
        if ((uint)index > byte.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, $"Authored track index {index} is outside the supported range 0..255.");
        return (byte)index;
    }
}
