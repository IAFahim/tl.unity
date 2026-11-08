using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Tl.Unity
{
    public unsafe struct PlaybackSlots : global::Tl.IPlaybackSlots
    {
        DynamicBuffer<Playback> _buffer;
        global::Tl.Playback* _items;
        int _length;
        readonly int _entity;
        NativeList<ClosedPlayback> _closed;

        public PlaybackSlots(DynamicBuffer<Playback> buffer, int entity, NativeList<ClosedPlayback> closed)
        {
            _buffer = buffer;
            _items = (global::Tl.Playback*)buffer.GetUnsafePtr();
            _length = buffer.Length;
            _entity = entity;
            _closed = closed;
        }

        public PlaybackSlots(DynamicBuffer<Playback> buffer) : this(buffer, -1, default)
        {
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _length;
        }

        public ref global::Tl.Playback this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _items[index];
        }

        public bool StartsPending => false;

        public bool TryAdd(in global::Tl.Playback playback)
        {
            if (_length >= TimelineKernel.MaxSlotsPerEntity) return false;
            var copy = playback;
            _buffer.Add(*(Playback*)&copy);
            _items = (global::Tl.Playback*)_buffer.GetUnsafePtr();
            _length++;
            return true;
        }

        public bool Admit(ushort timeline) => true;

        public void Close(int index)
        {
            if (_closed.IsCreated) _closed.Add(new ClosedPlayback { Entity = _entity, Slot = index, Value = *(Playback*)(_items + index) });
            _items[index] = default;
            while (_length > 0 && _items[_length - 1].Timeline == 0) _length--;
            _buffer.ResizeUninitialized(_length);
        }

        public void Purge(int index)
        {
        }
    }

    public static class TimelineSlots
    {
        public static bool TryGetTimeline(ref DynamicBuffer<TimelineSlot> slots, ushort key, out global::Tl.TimelineId timeline)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i].Key == key)
                {
                    timeline = slots[i].Timeline;
                    return true;
                }
            }
            timeline = default;
            return false;
        }

        public static void Set(ref DynamicBuffer<TimelineSlot> slots, ushort key, global::Tl.TimelineId timeline)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i].Key == key)
                {
                    slots[i] = new TimelineSlot { Key = key, Timeline = timeline };
                    return;
                }
            }
            slots.Add(new TimelineSlot { Key = key, Timeline = timeline });
        }

        public static unsafe bool StartFromSlot(ref PlaybackSlots playbacks, ref DynamicBuffer<TimelineSlot> slots, ushort key, ushort at, in global::Tl.LibraryView view)
        {
            if (!TryGetTimeline(ref slots, key, out var timeline) || timeline.IsNone)
                return false;
            return global::Tl.PlaybackRules.Start(ref playbacks, view.Timelines, view.TimelineCount, timeline.Value, at, out _);
        }
    }
}
