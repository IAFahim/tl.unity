using System.Runtime.InteropServices;

namespace Tl;

public readonly struct EventRecord<T>
    where T : unmanaged
{
    public EventRecord(EntityRef entity, int slot, int sequence, in T value)
    {
        Entity = entity;
        Slot = slot;
        Sequence = sequence;
        Value = value;
    }

    public readonly EntityRef Entity;
    public readonly int Slot;
    public readonly int Sequence;
    public readonly T Value;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct EventsHeader<T>
    where T : unmanaged
{
    public EventRecord<T>* Items;
    public int Count;
    public int Capacity;
    public int Sequence;
    public int Dropped;
    public int Fixed;
}

public unsafe struct Events<T> : IDisposable
    where T : unmanaged
{
    EventsHeader<T>* _header;
    int _entity;
    int _slot;

    public Events(int capacity)
    {
        var header = (EventsHeader<T>*)NativeMemory.AlignedAlloc((nuint)sizeof(EventsHeader<T>), 8u);
        NativeMemory.Clear(header, (nuint)sizeof(EventsHeader<T>));
        if (capacity > 0)
        {
            header->Items = (EventRecord<T>*)NativeMemory.AlignedAlloc((nuint)capacity * (nuint)sizeof(EventRecord<T>), 8u);
            header->Capacity = capacity;
        }
        _header = header;
        _entity = -1;
        _slot = -1;
    }

    public Events(void* header, EventRecord<T>* items, int capacity)
    {
        var fixedHeader = (EventsHeader<T>*)header;
        *fixedHeader = default;
        fixedHeader->Items = items;
        fixedHeader->Capacity = capacity;
        fixedHeader->Fixed = 1;
        _header = fixedHeader;
        _entity = -1;
        _slot = -1;
    }

    public static int HeaderBytes => sizeof(EventsHeader<T>);

    public Events<T> Writer => this;
    public int Count => _header->Count;
    public int Dropped => _header->Dropped;
    public ref readonly EventRecord<T> Item(int index) => ref _header->Items[index];

    public void Bind(int entity, int slot)
    {
        _entity = entity;
        _slot = slot;
    }

    public void Raise(in T value)
    {
        var header = _header;
        if (header->Count == header->Capacity)
        {
            if (header->Fixed == 0) Grow(header, header->Capacity == 0 ? 16 : header->Capacity * 2);
            if (header->Count == header->Capacity)
            {
                header->Dropped++;
                return;
            }
        }
        header->Items[header->Count++] = new EventRecord<T>(new EntityRef(_entity, 0), _slot, header->Sequence++, value);
    }

    public Span<EventRecord<T>> Drain()
    {
        var header = _header;
        var items = header->Items;
        var count = header->Count;
        SortStable(items, count);
        header->Count = 0;
        return new Span<EventRecord<T>>(items, count);
    }

    static void SortStable(EventRecord<T>* items, int count)
    {
        if (count <= 1) return;
        var sorted = true;
        for (var i = 1; i < count; i++)
        {
            if (Before(items + i, items + i - 1)) { sorted = false; break; }
        }
        if (sorted) return;
        const int block = 20;
        for (var low = 0; low < count; low += block)
        {
            var high = low + block < count ? low + block : count;
            for (var i = low + 1; i < high; i++)
            {
                if (!Before(items + i, items + i - 1)) continue;
                var held = items[i];
                var j = i;
                do
                {
                    items[j] = items[j - 1];
                    j--;
                }
                while (j > low && Before(&held, items + j - 1));
                items[j] = held;
            }
        }
        var stack = stackalloc int[192];
        var buffer = stackalloc long[1024];
        var capacity = 8192 / sizeof(EventRecord<T>);
        for (var width = block; width < count; width <<= 1)
        {
            for (var low = 0; low + width < count; low += width * 2)
            {
                var high = low + width * 2 < count ? low + width * 2 : count;
                if (Before(items + low + width, items + low + width - 1)) Merge(items, low, low + width, high, stack, (EventRecord<T>*)buffer, capacity);
            }
        }
    }

    static void Merge(EventRecord<T>* items, int first, int middle, int last, int* stack, EventRecord<T>* buffer, int capacity)
    {
        stack[0] = first;
        stack[1] = middle;
        stack[2] = last;
        var top = 3;
        while (top > 0)
        {
            top -= 3;
            var a = stack[top];
            var m = stack[top + 1];
            var b = stack[top + 2];
            if (m - a <= capacity && m - a <= b - m)
            {
                MergeLeft(items, a, m, b, buffer);
                continue;
            }
            if (b - m <= capacity)
            {
                MergeRight(items, a, m, b, buffer);
                continue;
            }
            if (m - a == 1)
            {
                var i = m;
                var j = b;
                while (i < j)
                {
                    var h = (int)(((uint)i + (uint)j) >> 1);
                    if (Before(items + h, items + a)) i = h + 1;
                    else j = h;
                }
                for (var k = a; k < i - 1; k++) Swap(items + k, items + k + 1);
                continue;
            }
            if (b - m == 1)
            {
                var i = a;
                var j = m;
                while (i < j)
                {
                    var h = (int)(((uint)i + (uint)j) >> 1);
                    if (!Before(items + m, items + h)) i = h + 1;
                    else j = h;
                }
                for (var k = m; k > i; k--) Swap(items + k, items + k - 1);
                continue;
            }
            var mid = (int)(((uint)a + (uint)b) >> 1);
            var n = mid + m;
            int start;
            int r;
            if (m > mid)
            {
                start = n - b;
                r = mid;
            }
            else
            {
                start = a;
                r = m;
            }
            var p = n - 1;
            while (start < r)
            {
                var c = (int)(((uint)start + (uint)r) >> 1);
                if (!Before(items + p - c, items + c)) start = c + 1;
                else r = c;
            }
            var end = n - start;
            if (start < m && m < end) Rotate(items, start, m, end);
            if (mid < end && end < b)
            {
                stack[top] = mid;
                stack[top + 1] = end;
                stack[top + 2] = b;
                top += 3;
            }
            if (a < start && start < mid)
            {
                stack[top] = a;
                stack[top + 1] = start;
                stack[top + 2] = mid;
                top += 3;
            }
        }
    }

    static void MergeLeft(EventRecord<T>* items, int a, int m, int b, EventRecord<T>* buffer)
    {
        var leftCount = m - a;
        for (var i = 0; i < leftCount; i++) buffer[i] = items[a + i];
        var l = 0;
        var r = m;
        var k = a;
        while (l < leftCount && r < b)
        {
            if (Before(items + r, buffer + l)) items[k++] = items[r++];
            else items[k++] = buffer[l++];
        }
        while (l < leftCount) items[k++] = buffer[l++];
    }

    static void MergeRight(EventRecord<T>* items, int a, int m, int b, EventRecord<T>* buffer)
    {
        var rightCount = b - m;
        for (var i = 0; i < rightCount; i++) buffer[i] = items[m + i];
        var l = m;
        var r = rightCount;
        var k = b;
        while (l > a && r > 0)
        {
            if (Before(buffer + r - 1, items + l - 1)) items[--k] = items[--l];
            else items[--k] = buffer[--r];
        }
        while (r > 0) items[--k] = buffer[--r];
    }

    static void Rotate(EventRecord<T>* items, int a, int m, int b)
    {
        var i = m - a;
        var j = b - m;
        while (i != j)
        {
            if (i > j)
            {
                SwapRange(items + m - i, items + m, j);
                i -= j;
            }
            else
            {
                SwapRange(items + m - i, items + m + j - i, i);
                j -= i;
            }
        }
        SwapRange(items + m - i, items + m, i);
    }

    static void SwapRange(EventRecord<T>* left, EventRecord<T>* right, int count)
    {
        for (var i = 0; i < count; i++) Swap(left + i, right + i);
    }

    static void Swap(EventRecord<T>* left, EventRecord<T>* right)
    {
        var temp = *left;
        *left = *right;
        *right = temp;
    }

    static bool Before(EventRecord<T>* left, EventRecord<T>* right)
    {
        if (left->Entity.Index != right->Entity.Index) return left->Entity.Index < right->Entity.Index;
        if (left->Slot != right->Slot) return left->Slot < right->Slot;
        return left->Sequence < right->Sequence;
    }

    public void ConcatSegments(Events<T>* segs, int chunks)
    {
        if (segs == null || chunks <= 0) return;
        var self = this;
        for (var c = 0; c < chunks; c++) { var seg = &segs[c]; for (var i = 0; i < seg->Count; i++) { ref readonly var r = ref seg->Item(i); self.Bind(r.Entity.Index, r.Slot); self.Raise(in r.Value); } }
    }

    public void Dispose()
    {
        if (_header == null) return;
        if (_header->Fixed != 0)
        {
            _header = null;
            return;
        }
        if (_header->Items != null) NativeMemory.AlignedFree(_header->Items);
        NativeMemory.AlignedFree(_header);
        _header = null;
    }

    [Unity.Burst.BurstDiscard]
    static void Grow(EventsHeader<T>* header, int next)
    {
        var block = (EventRecord<T>*)NativeMemory.AlignedAlloc((nuint)next * (nuint)sizeof(EventRecord<T>), 8u);
        if (header->Items != null)
        {
            new ReadOnlySpan<EventRecord<T>>(header->Items, header->Count).CopyTo(new Span<EventRecord<T>>(block, next));
            NativeMemory.AlignedFree(header->Items);
        }
        header->Items = block;
        header->Capacity = next;
    }
}
