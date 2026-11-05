using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    public struct TimelineEventSink<T> : System.IDisposable
        where T : unmanaged
    {
        NativeList<global::Tl.EventRecord<T>> _items;
        int _sequence;

        public TimelineEventSink(int capacity, Allocator allocator)
        {
            _items = new NativeList<global::Tl.EventRecord<T>>(capacity, allocator);
            _sequence = 0;
            BoundEntity = -1;
            BoundSlot = -1;
        }

        public void Bind(int entity, int slot)
         {
            BoundEntity = entity;
            BoundSlot = slot;
        }

        public int BoundEntity;
        public int BoundSlot;

        public void Raise(in T value)
        {
            _items.Add(new global::Tl.EventRecord<T>(new global::Tl.EntityRef(BoundEntity, 0), BoundSlot, _sequence++, in value));
        }

        public NativeArray<global::Tl.EventRecord<T>> Drain(Allocator allocator)
        {
            var sorted = new NativeArray<global::Tl.EventRecord<T>>(_items.Length, allocator, NativeArrayOptions.UninitializedMemory);
            sorted.CopyFrom(_items.AsArray());
            TimelineEvents.Sort(sorted);
            _items.Clear();
            return sorted;
        }

        public void Dispose()
        {
            if (_items.IsCreated)
                _items.Dispose();
        }
    }

    public static class TimelineEvents
    {
        public static void Sort<T>(NativeArray<global::Tl.EventRecord<T>> items)
            where T : unmanaged
        {
            for (var i = 1; i < items.Length; i++)
            {
                var item = items[i];
                var j = i - 1;
                while (j >= 0)
                {
                    var current = items[j];
                    if (!Before(in item, in current))
                        break;
                    items[j + 1] = current;
                    j--;
                }
                items[j + 1] = item;
            }
        }

        static bool Before<T>(in global::Tl.EventRecord<T> left, in global::Tl.EventRecord<T> right)
            where T : unmanaged
        {
            if (left.Entity.Index != right.Entity.Index)
                return left.Entity.Index < right.Entity.Index;
            if (left.Slot != right.Slot)
                return left.Slot < right.Slot;
            return left.Sequence < right.Sequence;
        }

        public static NativeArray<global::Tl.EventRecord<T>> Drain<T>(ref TimelineEventSink<T> sink, Allocator allocator)
            where T : unmanaged
        {
            return sink.Drain(allocator);
        }
    }
}
