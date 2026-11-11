using System.Runtime.InteropServices;

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
}

public readonly struct RequestRecord
{
    public RequestRecord(int entity, int slot, ushort timeline, ushort at, RequestOp op, int sequence)
    {
        Entity = entity;
        Slot = slot;
        Timeline = timeline;
        At = at;
        Op = op;
        Sequence = sequence;
    }

    public readonly int Entity;
    public readonly int Slot;
    public readonly ushort Timeline;
    public readonly ushort At;
    public readonly RequestOp Op;
    public readonly int Sequence;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RequestQueue
{
    RequestRecord* _items;
    int _count;
    int _capacity;
    int _sequence;

    public int Count => _count;
    public ref readonly RequestRecord Item(int index) => ref _items[index];

    public static void Add(RequestQueue* queue, int entity, int slot, ushort timeline, ushort at, RequestOp op)
    {
        if (queue->_count == queue->_capacity) Grow(queue);
        queue->_items[queue->_count++] = new RequestRecord(entity, slot, timeline, at, op, queue->_sequence++);
    }

    public static void SortStable(RequestQueue* queue) => Sort(queue->_items, queue->_count);

    public static void Sort(RequestRecord* items, int count)
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
        var scratch = (RequestRecord*)NativeMemory.AlignedAlloc((nuint)count * (nuint)sizeof(RequestRecord), 8u);
        var counts = (int*)NativeMemory.AlignedAlloc(65536u * (uint)sizeof(int), 8u);
        try
        {
            var source = new Span<RequestRecord>(items, count);
            var destination = new Span<RequestRecord>(scratch, count);
            for (var pass = 0; pass < 4; pass++)
            {
                RadixPass(source, destination, counts, pass);
                var tmp = source;
                source = destination;
                destination = tmp;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(scratch);
            NativeMemory.AlignedFree(counts);
        }

        static void RadixPass(Span<RequestRecord> source, Span<RequestRecord> destination, int* counts, int pass)
        {
            new Span<int>(counts, 65536).Clear();
            for (var i = 0; i < source.Length; i++) counts[Digit(source[i], pass)]++;
            var offset = 0;
            for (var bucket = 0; bucket < 65536; bucket++)
            {
                var bucketCount = counts[bucket];
                counts[bucket] = offset;
                offset += bucketCount;
            }
            for (var i = 0; i < source.Length; i++)
            {
                var digit = Digit(source[i], pass);
                destination[counts[digit]++] = source[i];
            }
        }

        static int Digit(RequestRecord item, int pass)
        {
            var key = pass < 2 ? (uint)item.Slot ^ 0x80000000u : (uint)item.Entity ^ 0x80000000u;
            return (int)((key >> ((pass % 2) * 16)) & 0xFFFFu);
        }
    }

    public static void Clear(RequestQueue* queue) => queue->_count = 0;

    public static RequestQueue* Create()
    {
        var queue = (RequestQueue*)NativeMemory.AlignedAlloc((nuint)sizeof(RequestQueue), 8u);
        NativeMemory.Clear(queue, (nuint)sizeof(RequestQueue));
        return queue;
    }

    public static void Free(RequestQueue* queue)
    {
        if (queue->_items != null) NativeMemory.AlignedFree(queue->_items);
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
        queue->_items = block;
        queue->_capacity = next;
    }
}

[StructLayout(LayoutKind.Sequential)]
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
        if (list->_entities != null) NativeMemory.AlignedFree(list->_entities);
        if (list->_slots != null) NativeMemory.AlignedFree(list->_slots);
        if (list->_playbacks != null) NativeMemory.AlignedFree(list->_playbacks);
        NativeMemory.AlignedFree(list);
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

public readonly unsafe struct Requests
{
    readonly RequestQueue* _queue;
    readonly int _entity;
    readonly int _slot;

    public Requests(RequestQueue* queue, int entity, int slot)
    {
        _queue = queue;
        _entity = entity;
        _slot = slot;
    }
    public Requests For(int entity, int slot) => new(_queue, entity, slot);
    public void Start(ushort timeline, ushort at = 0) => RequestQueue.Add(_queue, _entity, _slot, timeline, at, RequestOp.Start);
    public void Stop(ushort timeline) => RequestQueue.Add(_queue, _entity, _slot, timeline, 0, RequestOp.Stop);
    public void StopChannel(byte channel) => RequestQueue.Add(_queue, _entity, _slot, channel, 0, RequestOp.StopChannel);
    public void StopAll() => RequestQueue.Add(_queue, _entity, _slot, 0, 0, RequestOp.StopAll);
    public void Pause(ushort timeline) => RequestQueue.Add(_queue, _entity, _slot, timeline, 0, RequestOp.Pause);
    public void Resume(ushort timeline) => RequestQueue.Add(_queue, _entity, _slot, timeline, 0, RequestOp.Resume);
    public void Seek(ushort timeline, ushort tick) => RequestQueue.Add(_queue, _entity, _slot, timeline, tick, RequestOp.Seek);
    public void Reverse(ushort timeline, bool reverse) => RequestQueue.Add(_queue, _entity, _slot, timeline, reverse ? (ushort)1 : (ushort)0, RequestOp.Reverse);
    public void PauseChannel(byte channel) => RequestQueue.Add(_queue, _entity, _slot, channel, 0, RequestOp.PauseChannel);
    public void ResumeChannel(byte channel) => RequestQueue.Add(_queue, _entity, _slot, channel, 0, RequestOp.ResumeChannel);
    public void SeekChannel(byte channel, ushort tick) => RequestQueue.Add(_queue, _entity, _slot, channel, tick, RequestOp.SeekChannel);
    public void ReverseChannel(byte channel, bool reverse) => RequestQueue.Add(_queue, _entity, _slot, channel, reverse ? (ushort)1 : (ushort)0, RequestOp.ReverseChannel);
    public void SetSpeed(ushort timeline, ushort sixteenths) => RequestQueue.Add(_queue, _entity, _slot, timeline, sixteenths, RequestOp.Speed);
    public void SetChannelSpeed(byte channel, ushort sixteenths) => RequestQueue.Add(_queue, _entity, _slot, channel, sixteenths, RequestOp.SpeedChannel);
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
