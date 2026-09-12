using System.Collections.Immutable;

namespace Tl.Compiler;

public readonly record struct TypeId(string Value);

public readonly record struct PayloadId(uint Value);

[Flags]
public enum SlotAccess : byte
{
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
}

public readonly record struct OperationSlotPlan(string Role, TypeId Type, SlotAccess Access);

public sealed record OrderedOperationPlan
{
    public OrderedOperationPlan(OperationId id, IEnumerable<OperationSlotPlan> slots)
    {
        if (slots is null)
            throw new ArgumentNullException(nameof(slots));
        Id = id;
        Slots = [.. slots];
    }

    public OperationId Id { get; }
    public ImmutableArray<OperationSlotPlan> Slots { get; }
}

public sealed record PayloadPlan
{
    public PayloadPlan(PayloadId id, TypeId type, IEnumerable<byte> bytes)
    {
        if (bytes is null)
            throw new ArgumentNullException(nameof(bytes));
        Id = id;
        Type = type;
        Bytes = [.. bytes];
    }

    public PayloadId Id { get; }
    public TypeId Type { get; }
    public ImmutableArray<byte> Bytes { get; }
}

public readonly record struct OrderedTrackPlan(byte Index, PayloadId Payload, OperationId Operation);

public readonly record struct OrderedClipPlan(byte TrackIndex, PayloadId Payload, uint Start, uint End);

public enum HookPhase : byte
{
    Before = 1,
    After = 2,
}

public readonly record struct OrderedHookPlan(OperationId Operation, HookPhase Phase);

public sealed record OrderedTimelinePlan
{
    public const ushort CurrentFormatVersion = 1;

    public OrderedTimelinePlan(
        string identity,
        bool loops,
        IEnumerable<OrderedOperationPlan> operations,
        IEnumerable<PayloadPlan> payloads,
        IEnumerable<OrderedTrackPlan> tracks,
        IEnumerable<OrderedClipPlan> clips,
        IEnumerable<OrderedHookPlan> hooks,
        ushort formatVersion = CurrentFormatVersion)
    {
        if (identity is null)
            throw new ArgumentNullException(nameof(identity));
        if (operations is null)
            throw new ArgumentNullException(nameof(operations));
        if (payloads is null)
            throw new ArgumentNullException(nameof(payloads));
        if (tracks is null)
            throw new ArgumentNullException(nameof(tracks));
        if (clips is null)
            throw new ArgumentNullException(nameof(clips));
        if (hooks is null)
            throw new ArgumentNullException(nameof(hooks));
        Identity = identity;
        Loops = loops;
        Operations = [.. operations];
        Payloads = [.. payloads];
        Tracks = [.. tracks];
        Clips = [.. clips];
        Hooks = [.. hooks];
        FormatVersion = formatVersion;
    }

    public string Identity { get; }
    public bool Loops { get; }
    public ImmutableArray<OrderedOperationPlan> Operations { get; }
    public ImmutableArray<PayloadPlan> Payloads { get; }
    public ImmutableArray<OrderedTrackPlan> Tracks { get; }
    public ImmutableArray<OrderedClipPlan> Clips { get; }
    public ImmutableArray<OrderedHookPlan> Hooks { get; }
    public ushort FormatVersion { get; }
    public ValidatedOrderedTimelinePlan Validate() => ValidatedOrderedTimelinePlan.Create(this);
}

[Flags]
public enum OrderedOccurrenceFlags : byte
{
    None = 0,
    BeforeHook = 1,
    AfterHook = 2,
    Blend = 4,
}

public readonly record struct OrderedOccurrencePlan(
    ushort OperationIndex,
    byte TrackIndex,
    OrderedOccurrenceFlags Flags,
    uint TrackPayloadIndex,
    uint FirstPayloadIndex,
    uint SecondPayloadIndex,
    uint FactorStart,
    uint FactorLength);

public readonly record struct OrderedRegionPlan(
    uint Start,
    uint End,
    uint OccurrenceOffset,
    ushort OccurrenceCount);
