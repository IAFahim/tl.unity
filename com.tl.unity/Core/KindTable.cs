using System.Runtime.InteropServices;

namespace Tl;

[Flags]
public enum KindEntryFlags : byte
{
    Span = 1,
    Pulse = 2,
    Synthetic = 4,
    Interrupted = 8,
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct KindEntry
{
    public ushort Timeline;
    public ushort Step;
    public byte Slot;
    public byte Feeder;
    public KindEntryFlags Flags;
    public byte Reserved;
}

public unsafe struct KindTable
{
    public int Kind;
    public int ValueAt;
    public bool Grouped;
    public int Rows;
    public int Inline;
    public int Stride;
    public int Words;
    public int* Entity;
    byte* _inline;
    public ushort* Length;
    ushort* _capacity;
    byte** _heap;
    public ulong* Active;
    public ulong* Enabled;
    public int MaxLength;
    public int Overflows;

    internal static KindTable* Create(int kind, ReadOnlySpan<int> entities, int inlineCapacity, int entryBytes, bool grouped = false)
    {
        var t = (KindTable*)Zeroed((nuint)sizeof(KindTable));
        t->Kind = kind;
        t->Grouped = grouped;
        t->ValueAt = grouped ? 12 : 8;
        t->Rows = entities.Length;
        t->Inline = inlineCapacity;
        t->Stride = entryBytes;
        t->Words = (t->Rows + 63) >> 6;
        t->Entity = (int*)Zeroed((nuint)t->Rows * 4);
        for (var r = 0; r < t->Rows; r++) t->Entity[r] = entities[r];
        t->_inline = (byte*)Zeroed((nuint)t->Rows * (nuint)inlineCapacity * (nuint)entryBytes);
        t->Length = (ushort*)Zeroed((nuint)t->Rows * 2);
        t->_capacity = (ushort*)Zeroed((nuint)t->Rows * 2);
        for (var r = 0; r < t->Rows; r++) t->_capacity[r] = (ushort)inlineCapacity;
        t->_heap = (byte**)Zeroed((nuint)t->Rows * (nuint)sizeof(nint));
        t->Active = (ulong*)Zeroed((nuint)t->Words * 8);
        t->Enabled = (ulong*)Zeroed((nuint)t->Words * 8);
        for (var w = 0; w < t->Words; w++) t->Enabled[w] = ~0ul;
        if ((t->Rows & 63) != 0) t->Enabled[t->Words - 1] = (1ul << (t->Rows & 63)) - 1;
        return t;
    }

    internal static void Free(KindTable* t)
    {
        if (t == null) return;
        for (var r = 0; r < t->Rows; r++)
            if (t->_heap[r] != null) NativeMemory.AlignedFree(t->_heap[r]);
        NativeMemory.AlignedFree(t->Entity);
        NativeMemory.AlignedFree(t->_inline);
        NativeMemory.AlignedFree(t->Length);
        NativeMemory.AlignedFree(t->_capacity);
        NativeMemory.AlignedFree(t->_heap);
        NativeMemory.AlignedFree(t->Active);
        NativeMemory.AlignedFree(t->Enabled);
        NativeMemory.AlignedFree(t);
    }

    static void* Zeroed(nuint bytes)
    {
        var p = NativeMemory.AlignedAlloc(bytes == 0 ? 1 : bytes, 64);
        NativeMemory.Clear(p, bytes);
        return p;
    }

    public long StorageBytesPerRow => 16 + (long)Inline * Stride;

    public bool IsActive(int row) => (Active[row >> 6] & (1ul << (row & 63))) != 0;

    public byte* Data(int row) => _heap[row] != null ? _heap[row] : _inline + (nuint)row * (nuint)Inline * (nuint)Stride;

    public KindEntry* At(int row, int index) => (KindEntry*)(Data(row) + (nuint)index * (nuint)Stride);

    public KindEntry* Append(int row)
    {
        var length = Length[row];
        if (length == _capacity[row]) Grow(row);
        Length[row] = (ushort)(length + 1);
        if (length + 1 > MaxLength) MaxLength = length + 1;
        Active[row >> 6] |= 1ul << (row & 63);
        return At(row, length);
    }

    void Grow(int row)
    {
        var capacity = Math.Max(4, _capacity[row] * 2);
        if (capacity > ushort.MaxValue) throw new InvalidOperationException($"kind {Kind} row {row} holds more than 65,535 entries.");
        var heap = (byte*)NativeMemory.AlignedAlloc((nuint)capacity * (nuint)Stride, 8);
        NativeMemory.Clear(heap, (nuint)capacity * (nuint)Stride);
        new ReadOnlySpan<byte>(Data(row), Length[row] * Stride).CopyTo(new Span<byte>(heap, capacity * Stride));
        if (_heap[row] != null) NativeMemory.AlignedFree(_heap[row]);
        _heap[row] = heap;
        _capacity[row] = (ushort)capacity;
        Overflows++;
    }

    public ushort KindOf(KindEntry* entry) => Grouped ? *(ushort*)((byte*)entry + 8) : (ushort)Kind;

    public bool RemoveSpan(int row, byte slot, ushort timeline, ushort step, int kind = -1)
    {
        var length = Length[row];
        var data = Data(row);
        for (var i = 0; i < length; i++)
        {
            var e = (KindEntry*)(data + (nuint)i * (nuint)Stride);
            if ((e->Flags & KindEntryFlags.Span) == 0 || e->Slot != slot || e->Timeline != timeline || e->Step != step || (Grouped && kind >= 0 && KindOf(e) != kind)) continue;
            var tail = (length - i - 1) * Stride;
            Buffer.MemoryCopy(data + (nuint)(i + 1) * (nuint)Stride, data + (nuint)i * (nuint)Stride, tail, tail);
            Length[row] = (ushort)(length - 1);
            if (length == 1) Active[row >> 6] &= ~(1ul << (row & 63));
            return true;
        }
        return false;
    }

    public void RemovePulses(int row)
    {
        var length = Length[row];
        var data = Data(row);
        var kept = 0;
        for (var i = 0; i < length; i++)
        {
            var e = (KindEntry*)(data + (nuint)i * (nuint)Stride);
            if ((e->Flags & KindEntryFlags.Pulse) != 0) continue;
            if (kept != i) Buffer.MemoryCopy(e, data + (nuint)kept * (nuint)Stride, Stride, Stride);
            kept++;
        }
        Length[row] = (ushort)kept;
        if (kept == 0) Active[row >> 6] &= ~(1ul << (row & 63));
    }

    public void Clear(int row)
    {
        Length[row] = 0;
        Active[row >> 6] &= ~(1ul << (row & 63));
    }

    public void SetEnabled(int row, bool value)
    {
        if (value) Enabled[row >> 6] |= 1ul << (row & 63);
        else Enabled[row >> 6] &= ~(1ul << (row & 63));
    }
}
