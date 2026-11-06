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
        var span = new Span<EventRecord<T>>(header->Items, header->Count);
        for (var i = 1; i < span.Length; i++)
        {
            var item = span[i];
            var j = i - 1;
            while (j >= 0 && Before(in item, in span[j])) { span[j + 1] = span[j]; j--; }
            span[j + 1] = item;
        }
        header->Count = 0;
        return span;

        static bool Before(in EventRecord<T> left, in EventRecord<T> right)
        {
            if (left.Entity.Index != right.Entity.Index) return left.Entity.Index < right.Entity.Index;
            if (left.Slot != right.Slot) return left.Slot < right.Slot;
            return left.Sequence < right.Sequence;
        }
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
