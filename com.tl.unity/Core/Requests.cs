namespace Tl;

public enum RequestOp : byte
{
    Start = 0,
    Stop = 1,
    StopChannel = 2,
    StopAll = 3,
    Pause = 4,
    Resume = 5,
    Seek = 6,
    Reverse = 7,
    PauseChannel = 8,
    ResumeChannel = 9,
    SeekChannel = 10,
    ReverseChannel = 11,
    Speed = 12,
    SpeedChannel = 13,
    Reset = 14,
}

public readonly struct RequestRecord(int entity, int slot, ushort timeline, ushort at, RequestOp op, int sequence)
{
    public readonly int Entity = entity;
    public readonly int Slot = slot;
    public readonly ushort Timeline = timeline;
    public readonly ushort At = at;
    public readonly RequestOp Op = op;
    public readonly int Sequence = sequence;
}

public unsafe struct RequestQueue
{
    RequestRecord* _items;
    int _count;
    int _capacity;
    int _sequence;
    RequestRecord* _scratch;

    public int Count => _count;
    public ref readonly RequestRecord Item(int index) => ref _items[index];

    public static void Add(RequestQueue* queue, int entity, int slot, ushort timeline, ushort at, RequestOp op)
    {
        if (queue->_count == queue->_capacity) Grow(queue);
        queue->_items[queue->_count++] = new RequestRecord(entity, slot, timeline, at, op, queue->_sequence++);
    }

    public static void SortStable(RequestQueue* queue) => SortWith(queue->_items, queue->_count, queue->_scratch);

    public static void Sort(RequestRecord* items, int count, RequestRecord* scratch) => SortWith(items, count, scratch);

    static void SortWith(RequestRecord* items, int count, RequestRecord* scratch)
    {
        if (count <= 1) return;
        var sorted = true;
        var prevEntity = items[0].Entity;
        var prevSlot = items[0].Slot;
        for (var i = 1; i < count; i++)
        {
            var entity = items[i].Entity;
            if (entity < prevEntity || (entity == prevEntity && items[i].Slot < prevSlot))
            {
                sorted = false;
                break;
            }
            prevEntity = entity;
            prevSlot = items[i].Slot;
        }
        if (sorted) return;
        var counts = stackalloc int[256];
        var source = items;
        var destination = scratch;
        for (var pass = 0; pass < 8; pass++)
        {
            for (var d = 0; d < 256; d++) counts[d] = 0;
            for (var i = 0; i < count; i++) counts[Digit(source[i], pass)]++;
            if (counts[Digit(source[0], pass)] == count) continue;
            var offset = 0;
            for (var bucket = 0; bucket < 256; bucket++)
            {
                var bucketCount = counts[bucket];
                counts[bucket] = offset;
                offset += bucketCount;
            }
            for (var i = 0; i < count; i++)
            {
                var digit = Digit(source[i], pass);
                destination[counts[digit]++] = source[i];
            }
            var tmp = source;
            source = destination;
            destination = tmp;
        }
        if (source != items)
            for (var i = 0; i < count; i++) items[i] = source[i];
    }

    static int Digit(RequestRecord item, int pass)
    {
        var key = pass < 4 ? (uint)item.Slot ^ 0x80000000u : (uint)item.Entity ^ 0x80000000u;
        return (int)((key >> ((pass & 3) * 8)) & 0xFFu);
    }

    public static void Clear(RequestQueue* queue) => queue->_count = 0;

    internal static int CapacityOf(RequestQueue* queue) => queue->_capacity;

    internal static void Trim(RequestQueue* queue, int drained = 0)
    {
        if (queue->_count != 0 || queue->_capacity <= Math.Max(4096, drained * 8)) return;
        NativeMemory.AlignedFree(queue->_items);
        NativeMemory.AlignedFree(queue->_scratch);
        queue->_items = queue->_scratch = null;
        queue->_capacity = 0;
    }

    public static RequestQueue* Create()
    {
        var queue = (RequestQueue*)NativeMemory.AlignedAlloc((nuint)sizeof(RequestQueue), 8u);
        NativeMemory.Clear(queue, (nuint)sizeof(RequestQueue));
        return queue;
    }

    public static void Free(RequestQueue* queue)
    {
        NativeMemory.AlignedFree(queue->_items);
        NativeMemory.AlignedFree(queue->_scratch);
        NativeMemory.AlignedFree(queue);
    }

    static void Grow(RequestQueue* queue)
    {
        var next = queue->_capacity == 0 ? 16 : queue->_capacity * 2;
        var block = (RequestRecord*)NativeMemory.AlignedAlloc((nuint)next * (nuint)sizeof(RequestRecord), 8u);
        if (queue->_items != null)
        {
            new ReadOnlySpan<RequestRecord>(queue->_items, queue->_count).CopyTo(new Span<RequestRecord>(block, next));
            NativeMemory.AlignedFree(queue->_items);
        }
        NativeMemory.AlignedFree(queue->_scratch);
        queue->_scratch = (RequestRecord*)NativeMemory.AlignedAlloc((nuint)next * (nuint)sizeof(RequestRecord), 8u);
        queue->_items = block;
        queue->_capacity = next;
    }
}

public unsafe struct ClosingList
{
    int* _entities;
    int* _slots;
    Playback* _playbacks;
    int _count;
    int _capacity;

    public int Count => _count;

    public void Get(int index, out int entity, out int slot, out Playback playback)
    {
        entity = _entities[index];
        slot = _slots[index];
        playback = _playbacks[index];
    }

    public static void Add(ClosingList* list, int entity, int slot, in Playback playback)
    {
        if (list->_count == list->_capacity) Grow(list);
        list->_entities[list->_count] = entity;
        list->_slots[list->_count] = slot;
        list->_playbacks[list->_count] = playback;
        list->_count++;
    }

    public static void Clear(ClosingList* list) => list->_count = 0;

    public static ClosingList* Create()
    {
        var list = (ClosingList*)NativeMemory.AlignedAlloc((nuint)sizeof(ClosingList), 8u);
        NativeMemory.Clear(list, (nuint)sizeof(ClosingList));
        return list;
    }

    public static void Free(ClosingList* list)
    {
        KindStore.Free4(list->_entities, list->_slots, list->_playbacks, list);
    }

    static void Grow(ClosingList* list)
    {
        var next = list->_capacity == 0 ? 8 : list->_capacity * 2;
        var entities = (int*)NativeMemory.AlignedAlloc((nuint)next * 4u, 8u);
        var slots = (int*)NativeMemory.AlignedAlloc((nuint)next * 4u, 8u);
        var playbacks = (Playback*)NativeMemory.AlignedAlloc((nuint)next * 8u, 8u);
        if (list->_count > 0)
        {
            new ReadOnlySpan<int>(list->_entities, list->_count).CopyTo(new Span<int>(entities, next));
            new ReadOnlySpan<int>(list->_slots, list->_count).CopyTo(new Span<int>(slots, next));
            new ReadOnlySpan<Playback>(list->_playbacks, list->_count).CopyTo(new Span<Playback>(playbacks, next));
            NativeMemory.AlignedFree(list->_entities);
            NativeMemory.AlignedFree(list->_slots);
            NativeMemory.AlignedFree(list->_playbacks);
        }
        list->_entities = entities;
        list->_slots = slots;
        list->_playbacks = playbacks;
        list->_capacity = next;
    }
}

public readonly unsafe struct Requests(RequestQueue* queue, int entity, int slot)
{
    readonly RequestQueue* _queue = queue;
    readonly int _entity = entity;
    readonly int _slot = slot;

    public Requests For(int entity, int slot) => new(_queue, entity, slot);
    void Add(ushort timeline, ushort at, RequestOp op) => RequestQueue.Add(_queue, _entity, _slot, timeline, at, op);
    public void Start(ushort timeline, ushort at = 0) => Add(timeline, at, RequestOp.Start);
    public void Stop(ushort timeline) => Add(timeline, 0, RequestOp.Stop);
    public void StopChannel(byte channel) => Add(channel, 0, RequestOp.StopChannel);
    public void StopAll() => Add(0, 0, RequestOp.StopAll);
    public void Pause(ushort timeline) => Add(timeline, 0, RequestOp.Pause);
    public void Resume(ushort timeline) => Add(timeline, 0, RequestOp.Resume);
    public void Seek(ushort timeline, ushort tick) => Add(timeline, tick, RequestOp.Seek);
    public void Reverse(ushort timeline, bool reverse) => Add(timeline, reverse ? (ushort)1 : (ushort)0, RequestOp.Reverse);
    public void PauseChannel(byte channel) => Add(channel, 0, RequestOp.PauseChannel);
    public void ResumeChannel(byte channel) => Add(channel, 0, RequestOp.ResumeChannel);
    public void SeekChannel(byte channel, ushort tick) => Add(channel, tick, RequestOp.SeekChannel);
    public void ReverseChannel(byte channel, bool reverse) => Add(channel, reverse ? (ushort)1 : (ushort)0, RequestOp.ReverseChannel);
    public void SetSpeed(ushort timeline, ushort sixteenths) => Add(timeline, sixteenths, RequestOp.Speed);
    public void SetChannelSpeed(byte channel, ushort sixteenths) => Add(channel, sixteenths, RequestOp.SpeedChannel);
    public void Reset() => Add(0, 0, RequestOp.Reset);
    public void ConcatSegments(RequestQueue** segs, int chunks)
    {
        if (segs == null || chunks <= 0) return;
        for (var c = 0; c < chunks; c++)
        {
            var seg = segs[c];
            if (seg == null) continue;
            for (var i = 0; i < seg->Count; i++) { ref readonly var r = ref seg->Item(i); RequestQueue.Add(_queue, r.Entity, r.Slot, r.Timeline, r.At, r.Op); }
        }
    }
}
