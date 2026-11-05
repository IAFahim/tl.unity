using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.LowLevel.Unsafe;
using Unity.Jobs;

namespace Tl.Unity
{
    public unsafe struct KindPulseWriter
    {
        [NativeDisableUnsafePtrRestriction] internal NativeStream.Writer* Pointer;

        ref NativeStream.Writer Writer => ref *Pointer;

        public void Begin(int index) => Writer.BeginForEachIndex(index);

        public void End() => Writer.EndForEachIndex();

        internal void Write<T>(T value) where T : unmanaged => Writer.Write(value);
    }

    public struct KindPulses<T> where T : unmanaged
    {
        KindPulseWriter _writer;
        readonly int _kind;

        public KindPulses(KindPulseWriter writer, int kind)
        {
            _writer = writer;
            _kind = kind;
        }

        public void To(Entity target, T value)
        {
            _writer.Write(_kind);
            _writer.Write(UnsafeUtility.SizeOf<T>());
            _writer.Write(target);
            _writer.Write(value);
        }
    }

    public unsafe struct KindPulseSender
    {
        NativeStream _stream;
        NativeStream.Writer* _writer;

        public KindPulseSender(int foreachCount, Allocator allocator)
        {
            _stream = new NativeStream(foreachCount, allocator);
            _writer = (NativeStream.Writer*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<NativeStream.Writer>(), UnsafeUtility.AlignOf<NativeStream.Writer>(), Allocator.Persistent);
            *_writer = _stream.AsWriter();
        }

        public KindPulseWriter Writer => new KindPulseWriter { Pointer = _writer };

        internal NativeStream Stream => _stream;

        internal JobHandle Dispose(JobHandle dependency)
        {
            return new KindFreeJob { Pointer = _writer }.Schedule(_stream.Dispose(dependency));
        }

        internal void Dispose()
        {
            _stream.Dispose();
            UnsafeUtility.Free(_writer, Allocator.Persistent);
        }
    }

    [BurstCompile]
    public unsafe struct KindFreeJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public void* Pointer;

        public void Execute()
        {
            UnsafeUtility.Free(Pointer, Allocator.Persistent);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe struct KindMergeJob : IJob
    {
        public NativeStream.Reader Reader;
        public int ForEachCount;
        [ReadOnly] public NativeArray<int> RegistryToLibrary;
        [ReadOnly] public EntityStorageInfoLookup Storage;
        public KindHandleSet Handles;
        [NativeDisableUnsafePtrRestriction] public int* Counters;
        [NativeDisableUnsafePtrRestriction] public KindIndex Index;

        public void Execute()
        {
            var buffers = stackalloc UnsafeUntypedBufferAccessor[64];
            var cursor = new KindCursor { Index = Index, Handles = (KindHandleSet*)UnsafeUtility.AddressOf(ref Handles), Buffers = buffers, Counters = Counters };
            var last = default(ArchetypeChunk);
            var lastValid = false;
            for (var f = 0; f < ForEachCount; f++)
            {
                Reader.BeginForEachIndex(f);
                while (Reader.RemainingItemCount > 0)
                {
                    var registry = Reader.Read<int>();
                    var bytes = Reader.Read<int>();
                    var target = Reader.Read<Entity>();
                    var value = Reader.ReadUnsafePtr(bytes);
                    var kind = (uint)registry < (uint)RegistryToLibrary.Length ? RegistryToLibrary[registry] : -1;
                    if (kind < 0 || bytes != (int)Index.Sec.Kinds[kind].ValueBytes || !Storage.Exists(target))
                    {
                        Counters[KindCounter.DroppedPulses]++;
                        continue;
                    }
                    var info = Storage[target];
                    if (!lastValid || !info.Chunk.Equals(last))
                    {
                        last = info.Chunk;
                        lastValid = true;
                        cursor.Chunk = last;
                        cursor.Reach = 0;
                        for (var k = 0; k < Index.KindCount; k++)
                        {
                            if (!KindHandleSet.IsMapped(cursor.Handles, k) || !last.Has(ref *KindHandleSet.Entry(cursor.Handles, k))) continue;
                            buffers[k] = last.GetUntypedBufferAccessor(ref *KindHandleSet.Entry(cursor.Handles, k));
                            cursor.Reach |= 1ul << k;
                        }
                    }
                    var entry = cursor.AppendEntry(info.IndexInChunk, kind);
                    if (entry == null)
                    {
                        Counters[KindCounter.DroppedPulses]++;
                        continue;
                    }
                    entry->Flags = global::Tl.KindEntryFlags.Pulse;
                    UnsafeUtility.MemCpy((byte*)entry + 8, value, bytes);
                }
                Reader.EndForEachIndex();
            }
        }
    }
}
