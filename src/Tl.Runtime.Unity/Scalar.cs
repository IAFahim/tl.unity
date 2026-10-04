using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tl;

public static unsafe partial class Timeline
{
    public static void Advance(ushort index, Span<ushort> positions, bool forward)
        => Advance(index, positions, positions, forward);

    public static void Advance(ushort index, ReadOnlySpan<ushort> positions, Span<ushort> next, bool forward)
    {
        if (positions.Length != next.Length) throw new ArgumentException("Clock columns must have the same row count.");
        var reference = TimelineTable.Reference(index);
        for (var row = 0; row < positions.Length; row++)
        {
            var clock = positions[row];
            if (reference.Advance(!forward, clock, out _, out _))
            {
                var header = (NativeHeader*)reference._p;
                TimelineMovement.Advance((ushort)header->Duration, header->Loops != 0, !forward, clock, out clock, out _, out _);
            }
            next[row] = clock;
        }
    }

    public static void Advance(ReadOnlySpan<ushort> indices, Span<ushort> positions, bool forward)
    {
        if (indices.Length != positions.Length) throw new ArgumentException("Index and clock columns must have the same row count.");
        for (var row = 0; row < positions.Length; row++)
            Advance(indices[row], positions.Slice(row, 1), forward);
    }
}

public static unsafe partial class Timeline<TTrack, TClip>
    where TTrack : unmanaged, IBlend<TClip>
    where TClip : unmanaged
{
    private static SlotView** views;
    private static int capacity;
    private static int gate;
    private static ulong generation;
    private static bool consumersReady;
    private const string Head = "Timeline column binding:";

    internal readonly struct BankHandle
    {
        internal SlotView* FoldedView(ushort index) => index < capacity ? views[index] : null;
    }

    private static BankHandle Bank() => default;

    private static void EnsureConsumers()
    {
        if (Volatile.Read(ref consumersReady)) return;
        RuntimeHelpers.RunModuleConstructor(typeof(TTrack).Module.ModuleHandle);
        if (typeof(TClip).Module != typeof(TTrack).Module)
            RuntimeHelpers.RunModuleConstructor(typeof(TClip).Module.ModuleHandle);
        Volatile.Write(ref consumersReady, true);
    }

    private static void Resolve(ushort index)
    {
        while (Interlocked.CompareExchange(ref gate, 1, 0) != 0) Thread.SpinWait(1);
        try
        {
            var reference = TimelineTable.Reference(index);
            if (index < capacity && views[index] != null) return;
            var key = PairRuntime<TTrack, TClip>.Key;
            EnsureConsumers();
            if (!reference.Uses(key)) throw new ArgumentException("The asset does not contain this timeline pair.");
            var expected = reference.LayoutOf(key);
            var actual = PairTable.LayoutOf(key);
            if (expected != 0 && actual != 0 && expected != actual)
                throw new ArgumentException("The asset field layout differs from the current domain. Rebake the timeline.");
            if (PairTable.HeadOf(key) < 0)
                throw new ArgumentException("No generated consumer is installed for this timeline pair. Install the Unity generator and recompile the domain.");
            if (index >= capacity)
            {
                var next = Math.Min(65536, Math.Max(index + 1, Math.Max(64, capacity * 2)));
                var allocated = (SlotView**)NativeMemory.AlignedAlloc((nuint)(next * sizeof(void*)), 64);
                new Span<IntPtr>(allocated, next).Clear();
                if (views != null)
                {
                    Buffer.MemoryCopy(views, allocated, next * sizeof(void*), capacity * sizeof(void*));
                    NativeMemory.AlignedFree(views);
                }
                views = allocated;
                capacity = next;
            }
            using var measured = MeasuredLanes.Measure(reference, key);
            var ticks = (nuint)(measured.Duration + 1);
            var plan = LaneBlockPlan.Plan(measured, ticks);
            views[index] = BankBuilder.BakeBlock(measured, plan, measured.Duration, measured.Looping, ticks, ++generation);
        }
        finally { Volatile.Write(ref gate, 0); }
    }

    public static SlotView View(ushort index)
    {
        Resolve(index);
        return *views[index];
    }

    public static SlotView View(TimelineAsset asset)
    {
        if (asset is null) throw new ArgumentNullException(nameof(asset));
        return View(asset.Index);
    }

    public static PreparedTimeline Prepare(ushort index) => new(View(index));
    public static PreparedTimeline Prepare(TimelineAsset asset) => new(View(asset));

    public static void Apply(ReadOnlySpan<ushort> indices, ReadOnlySpan<ushort> positions, bool forward, Span<float> effects)
    {
        if (indices.Length != positions.Length || indices.Length != effects.Length)
            throw new ArgumentException("Index, clock and effect columns must have the same row count.");
        for (var row = 0; row < positions.Length; row++)
            if (Prepare(indices[row]).TryRead(0, positions[row], forward, out float value)) effects[row] += value;
    }

    public static void Apply(ushort index, ReadOnlySpan<ushort> positions, bool forward, Span<float> effects)
    {
        if (positions.Length != effects.Length) throw new ArgumentException("Clock and effect columns must have the same row count.");
        var prepared = Prepare(index);
        for (var row = 0; row < positions.Length; row++)
            if (prepared.TryRead(0, positions[row], forward, out float value)) effects[row] += value;
    }

    public static void Advance(ushort index, ref ushort position, bool forward)
        => Prepare(index).Advance(ref position, forward);

    public static void Advance(ReadOnlySpan<ushort> indices, Span<ushort> positions, bool forward)
        => Timeline.Advance(indices, positions, forward);

    public static void ClearPrepared()
    {
        while (Interlocked.CompareExchange(ref gate, 1, 0) != 0) Thread.SpinWait(1);
        try
        {
            for (var index = 0; index < capacity; index++) NativeMemory.AlignedFree(views[index]);
            NativeMemory.AlignedFree(views);
            views = null;
            capacity = 0;
        }
        finally { Volatile.Write(ref gate, 0); }
    }
}

[StructLayout(LayoutKind.Sequential)]
public readonly unsafe struct PreparedTimeline
{
    public readonly SlotView View;

    public PreparedTimeline(SlotView view)
    {
        if (view.AbiVersion != SlotView.AbiVersionV3) throw new ArgumentException("Prepared timeline requires SlotView ABI 3.");
        View = view;
    }

    public bool TryRead<T>(int lane, ushort position, bool forward, out T value) where T : unmanaged
    {
        value = default;
        if (View.Absent != 0 || View.Duration == 0 || position > View.Duration || View.Looping != 0 && position == View.Duration || forward && position == View.Duration || !forward && View.Looping == 0 && position == 0)
            return false;
        var width = sizeof(T);
        if (width is not (1 or 2 or 4 or 8) || lane < 0 || lane >= View.ResultCount || width == 8 && lane + 1 >= View.ResultCount)
            throw new ArgumentException("Typed lane must fit its declared 1/2/4/8-byte result.");
        var slot = View;
        var tick = forward ? position : LaneEncoding.BackwardTick(position, View.Duration);
        var first = LaneEncoding.Bits(LaneEncoding.Value(&slot, (nuint)lane, forward, tick));
        ulong bits = first;
        if (width == 8)
        {
            if (slot.LaneKeys[lane + 1] != (slot.LaneKeys[lane] ^ 0x8000000000000000UL))
                throw new ArgumentException("Eight-byte values require the adjacent joined lane pair.");
            bits |= (ulong)LaneEncoding.Bits(LaneEncoding.Value(&slot, (nuint)(lane + 1), forward, tick)) << 32;
        }
        value = Unsafe.ReadUnaligned<T>(&bits);
        return true;
    }

    public void Advance(ref ushort position, bool forward)
    {
        if (View.Absent == 0 && TimelineMovement.Advance(View.Duration, View.Looping != 0, !forward, position, out var next, out _, out _))
            position = next;
    }
}

internal static class Checked
{
    internal static void Domain(ReadOnlySpan<ushort> indices, ReadOnlySpan<ushort> positions)
    {
        if (indices.Length != positions.Length) throw new ArgumentException("Index and clock columns must have the same row count.");
        for (var row = 0; row < indices.Length; row++) Domain(indices[row], positions[row]);
    }

    internal static unsafe void Domain(ushort index, ushort position)
    {
        var reference = TimelineTable.Reference(index);
        if (position > ((NativeHeader*)reference._p)->Duration) throw new ArgumentException("Clock position is outside the asset duration.");
    }
}
