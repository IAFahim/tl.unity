using System.Runtime.InteropServices;

namespace Tl;

[Flags]
public enum KindEntryFlags : byte
{
    Span = 1,
    Pulse = 2,
    Synthetic = 4,
    Interrupted = 8,
    Ready = 16,
}

[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct KindEntry
{
    public ushort Timeline;
    public ushort Step;
    public byte Slot;
    public byte Feeder;
    public KindEntryFlags Flags;
    public byte Owner;
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
    public ulong* Spilled;
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
        t->Spilled = (ulong*)Zeroed((nuint)t->Words * 8);
        if (inlineCapacity == 0) t->_heap = (byte**)Zeroed((nuint)t->Rows * (nuint)sizeof(nint));
        t->Active = (ulong*)Zeroed((nuint)t->Words * 8);
        t->Enabled = (ulong*)Zeroed((nuint)t->Words * 8);
        for (var w = 0; w < t->Words; w++) t->Enabled[w] = ~0ul;
        if ((t->Rows & 63) != 0) t->Enabled[t->Words - 1] = (1ul << (t->Rows & 63)) - 1;
        return t;
    }

    internal static void Free(KindTable* t)
    {
        if (t == null) return;
        if (t->_heap != null)
        {
            for (var r = 0; r < t->Rows; r++)
                if (t->_heap[r] != null) NativeMemory.AlignedFree(t->_heap[r]);
            NativeMemory.AlignedFree(t->_heap);
        }
        if (t->Inline != 0)
            for (var r = 0; r < t->Rows; r++)
                if (t->IsSpilled(r)) NativeMemory.AlignedFree(*(byte**)(t->_inline + (nuint)r * (nuint)t->Inline * (nuint)t->Stride));
        NativeMemory.AlignedFree(t->Entity);
        NativeMemory.AlignedFree(t->_inline);
        NativeMemory.AlignedFree(t->Length);
        NativeMemory.AlignedFree(t->Spilled);
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

    public long StorageBytesPerRow => 6 + (long)Inline * Stride;

    public bool IsActive(int row) => (Active[row >> 6] & (1ul << (row & 63))) != 0;

    bool IsSpilled(int row) => (Spilled[row >> 6] & (1ul << (row & 63))) != 0;

    void SetSpilled(int row, bool value)
    {
        if (value) Spilled[row >> 6] |= 1ul << (row & 63);
        else Spilled[row >> 6] &= ~(1ul << (row & 63));
    }

    byte* HeapOf(int row) => Inline == 0 ? _heap[row] : *(byte**)(_inline + (nuint)row * (nuint)Inline * (nuint)Stride);

    int Capacity(int row) => IsSpilled(row) ? *(int*)HeapOf(row) : Inline;

    public byte* Data(int row)
    {
        if (IsSpilled(row)) return HeapOf(row) + 8;
        if (Inline == 0) return (byte*)0;
        return _inline + (nuint)row * (nuint)Inline * (nuint)Stride;
    }

    public KindEntry* At(int row, int index) => (KindEntry*)(Data(row) + (nuint)index * (nuint)Stride);

    public KindEntry* Append(int row)
    {
        var length = Length[row];
        if (length == Capacity(row)) Grow(row);
        Length[row] = (ushort)(length + 1);
        Active[row >> 6] |= 1ul << (row & 63);
        return At(row, length);
    }

    void Grow(int row)
    {
        var capacity = Math.Min(Math.Max(4, Capacity(row) * 2), ushort.MaxValue);
        if (capacity == Capacity(row)) throw new InvalidOperationException($"kind {Kind} row {row} holds more than 65,535 entries.");
        var raw = (byte*)NativeMemory.AlignedAlloc(8u + (nuint)capacity * (nuint)Stride, 64);
        NativeMemory.Clear(raw, 8u + (nuint)capacity * (nuint)Stride);
        *(int*)raw = capacity;
        var length = Length[row];
        if (length > 0)
            new ReadOnlySpan<byte>(Data(row), length * Stride).CopyTo(new Span<byte>(raw + 8, length * Stride));
        if (IsSpilled(row)) NativeMemory.AlignedFree(HeapOf(row));
        if (Inline == 0) _heap[row] = raw;
        else *(byte**)(_inline + (nuint)row * (nuint)Inline * (nuint)Stride) = raw;
        SetSpilled(row, true);
    }

    internal int ColdMaxLength() { var m = 0; for (var r = 0; r < Rows; r++) if (Length[r] > m) m = Length[r]; return m; }
    internal int ColdOverflows() { var s = 0; for (var r = 0; r < Rows; r++) if (IsSpilled(r)) s++; return s; }

    public ushort KindOf(KindEntry* entry) => Grouped ? *(ushort*)((byte*)entry + 8) : (ushort)Kind;

    public void Purge(int row, byte slot, ushort timeline)
    {
        var length = Length[row];
        var data = Data(row);
        var kept = 0;
        for (var i = 0; i < length; i++)
        {
            var entry = (KindEntry*)(data + (nuint)i * (nuint)Stride);
            if (entry->Slot == slot && entry->Timeline == timeline) continue;
            if (kept != i) Buffer.MemoryCopy(entry, data + (nuint)kept * (nuint)Stride, Stride, Stride);
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
