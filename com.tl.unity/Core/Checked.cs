using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Runtime.CompilerServices.MethodImplOptions;

namespace Tl;

internal static class Checked
{
    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static void Live(bool disposed)
    {
        if (disposed) Fail.Disposed();
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static void Columns(ReadOnlySpan<ushort> positions, Span<float> effects)
    {
        if (effects.Length != positions.Length) Fail.ColumnLength(positions.Length, effects.Length);
        if (MemoryMarshal.AsBytes(positions).Overlaps(MemoryMarshal.AsBytes(effects))) Fail.ColumnOverlap();
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static unsafe void Columns(ReadOnlySpan<ushort> positions, Span<ushort> next, Span<float> effects)
    {
        Columns(positions, effects);
        if (next.IsEmpty) return;
        if (next.Length != positions.Length) Fail.ColumnLength(positions.Length, next.Length);
        fixed (ushort* pa = positions, pb = next) if (pa == pb) return;
        if (MemoryMarshal.AsBytes(effects).Overlaps(MemoryMarshal.AsBytes(next))
            || MemoryMarshal.AsBytes(positions).Overlaps(MemoryMarshal.AsBytes(next)))
            Fail.ColumnOverlap();
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static void Columns(ReadOnlySpan<ushort> indices, ReadOnlySpan<ushort> positions)
    {
        if (positions.Length != indices.Length) Fail.ColumnLength(positions.Length, indices.Length);
        if (MemoryMarshal.AsBytes(indices).Overlaps(MemoryMarshal.AsBytes(positions))) Fail.ColumnOverlap();
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static void Columns(ReadOnlySpan<ushort> ids, ReadOnlySpan<ushort> positions, Span<ushort> next, Span<float> effects)
    {
        Columns(positions, next, effects);
        if (ids.Length != positions.Length) Fail.ColumnLength(positions.Length, ids.Length);
        if (MemoryMarshal.AsBytes(ids).Overlaps(MemoryMarshal.AsBytes(positions))
            || MemoryMarshal.AsBytes(ids).Overlaps(MemoryMarshal.AsBytes(effects))
            || MemoryMarshal.AsBytes(ids).Overlaps(MemoryMarshal.AsBytes(next)))
            Fail.ColumnOverlap();
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static unsafe void Columns(ReadOnlySpan<ushort> ids, ReadOnlySpan<ushort> positions, Span<ushort> next)
    {
        if (ids.Length != positions.Length) Fail.ColumnLength(positions.Length, ids.Length);
        if (positions.Length != next.Length) Fail.ColumnLength(positions.Length, next.Length);
        if (next.IsEmpty) return;
        fixed (ushort* pa = positions, pb = next) if (pa == pb) return;
        if (MemoryMarshal.AsBytes(positions).Overlaps(MemoryMarshal.AsBytes(next))
            || MemoryMarshal.AsBytes(ids).Overlaps(MemoryMarshal.AsBytes(next))) Fail.ColumnOverlap();
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static void Length(ReadOnlySpan<ushort> positions, ReadOnlySpan<ushort> next)
    {
        if (next.Length != positions.Length) Fail.ColumnLength(positions.Length, next.Length);
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static void Rows(ReadOnlySpan<int> rows, ReadOnlySpan<ushort> indices, ReadOnlySpan<ushort> positions, Span<float> effects)
    {
        if (effects.Length != positions.Length) Fail.ColumnLength(positions.Length, effects.Length);
        if (indices.Length != positions.Length) Fail.ColumnLength(positions.Length, indices.Length);
        if (MemoryMarshal.AsBytes(positions).Overlaps(MemoryMarshal.AsBytes(effects))) Fail.ColumnOverlap();
        if (MemoryMarshal.AsBytes(indices).Overlaps(MemoryMarshal.AsBytes(positions))
            || MemoryMarshal.AsBytes(indices).Overlaps(MemoryMarshal.AsBytes(effects))) Fail.ColumnOverlap();
        RowBounds(rows, indices, positions, effects);
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static void Rows(ReadOnlySpan<int> rows, ReadOnlySpan<ushort> indices, Span<ushort> positions)
    {
        if (indices.Length != positions.Length) Fail.ColumnLength(positions.Length, indices.Length);
        if (MemoryMarshal.AsBytes(indices).Overlaps(MemoryMarshal.AsBytes(positions))) Fail.ColumnOverlap();
        RowBounds(rows, indices, positions, default);
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static unsafe void Domain(ushort index, ushort position)
    {
        var duration = TimelineTable.DomainOf(TimelineTable.Motion[index]);
        if (position > duration) Fail.PositionOutsideDomain(0, index, position, duration);
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static unsafe void Domain(ushort index, ReadOnlySpan<ushort> positions)
    {
        var duration = TimelineTable.DomainOf(TimelineTable.Motion[index]);
        for (var i = 0; i < positions.Length; i++)
            if (positions[i] > duration) Fail.PositionOutsideDomain(i, index, positions[i], duration);
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    internal static unsafe void Domain(ReadOnlySpan<ushort> indices, ReadOnlySpan<ushort> positions)
    {
        if (indices.Length != positions.Length) return;
        var motion = TimelineTable.Motion;
        for (var i = 0; i < positions.Length; i++)
        {
            var index = indices[i];
            var duration = TimelineTable.DomainOf(motion[index]);
            if (positions[i] > duration) Fail.PositionOutsideDomain(i, index, positions[i], duration);
        }
    }

    [Conditional("TL_CHECKED")]
    [MethodImpl(AggressiveInlining)]
    static void RowBounds(ReadOnlySpan<int> rows, ReadOnlySpan<ushort> indices, ReadOnlySpan<ushort> positions, Span<float> effects)
    {
        if (MemoryMarshal.AsBytes(rows).Overlaps(MemoryMarshal.AsBytes(indices))
            || MemoryMarshal.AsBytes(rows).Overlaps(MemoryMarshal.AsBytes(positions))
            || MemoryMarshal.AsBytes(rows).Overlaps(MemoryMarshal.AsBytes(effects))) Fail.ColumnOverlap();
        var count = indices.Length;
        for (var i = 0; i < rows.Length; i++)
            if ((uint)rows[i] >= (uint)count)
                Fail.RowOutsideColumns(i, rows[i], count);
    }
}


internal static class Fail
{
    [DoesNotReturn]
    internal static void ColumnLength(int positions, int column)
        => throw new ArgumentException($"Column length {column} must equal position count {positions}.");

    [DoesNotReturn]
    internal static void ColumnOverlap()
        => throw new ArgumentException("Lane columns must not overlap.");

    [DoesNotReturn]
    internal static void RowOutsideColumns(int batch, int row, int count)
        => throw new ArgumentException($"Row {batch} selects entity {row}, outside columns of length {count}.");

    [DoesNotReturn]
    internal static void PositionOutsideDomain(int row, int index, int position, int duration)
        => throw new ArgumentException($"Row {row} position {position} is outside timeline {index} duration {duration}.");

    [DoesNotReturn]
    internal static void Disposed()
        => throw new ObjectDisposedException("TimelineSet");

    [DoesNotReturn]
    internal static void PairSizes()
        => throw new ArgumentException("Pair value sizes do not match the baked pools; rebake the .tlb.");

    [DoesNotReturn]
    internal static void Sealed()
        => throw new InvalidOperationException("Consumer registry is sealed; install consumers and bakes before Timeline.SealConsumers().");
}
