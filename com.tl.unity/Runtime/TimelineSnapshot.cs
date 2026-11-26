using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Tl.Unity
{
    public unsafe struct TimelineSnapshot : IDisposable
    {
        public NativeList<byte> Data;

        public bool IsCreated => Data.IsCreated;

        public int Length => Data.IsCreated ? Data.Length : 0;

        public static TimelineSnapshot Create(Allocator allocator = Allocator.Persistent, int initialCapacity = 4096)
        {
            return new TimelineSnapshot
            {
                Data = new NativeList<byte>(initialCapacity, allocator),
            };
        }

        public void Dispose()
        {
            if (Data.IsCreated)
                Data.Dispose();
            Data = default;
        }
    }

    public unsafe struct TimelineSnapshotter : IDisposable
    {
        private const uint Magic = 0x544C534Eu;
        private const ushort FormatVersion = 1;
        private const byte FlagZeroSized = 1;
        private const byte FlagEnableable = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct Header
        {
            public uint Magic;
            public ushort Version;
            public ushort KindCount;
            public ushort ColumnCount;
            public ushort EventCount;
            public uint Generation;
            public int EntityCount;
            public byte HasStep;
            public byte Forward;
            public byte HasClock;
            public byte HasKind;
            public ushort Now;
            public int GlideStepDir;
            public int GlideLimit;
            public byte HasDense;
            public sbyte DenseDirection;
            public ushort DenseClock;
            public uint DenseSequence;
            public int IndexKindCount;
        }

        private struct SnapshotType
        {
            public TypeIndex TypeIndex;
            public int ElementSize;
            public byte Flags;

            public bool IsZeroSized => (Flags & FlagZeroSized) != 0;
            public bool IsEnableable => (Flags & FlagEnableable) != 0;
            public bool Valid => TypeIndex != TypeIndex.Null;
        }

        private byte _queriesCreated;
        private byte _schemaBuilt;
        private uint _builtGeneration;
        private EntityQuery _playbacks;
        private EntityQuery _library;
        private EntityQuery _step;
        private EntityQuery _dense;
        private EntityQuery _clock;
        private EntityQuery _closed;
        private NativeList<SnapshotType> _entries;
        private NativeList<SnapshotType> _actives;
        private NativeList<SnapshotType> _enableds;
        private NativeList<SnapshotType> _columns;
        private NativeList<SnapshotType> _events;

        public bool Capture(EntityManager manager, ref TimelineSnapshot into)
        {
            manager.CompleteAllTrackedJobs();
            EnsureSchema(manager);

            if (!into.IsCreated)
                into = TimelineSnapshot.Create(Allocator.Persistent);

            var data = into.Data;
            data.Clear();

            var header = new Header
            {
                Magic = Magic,
                Version = FormatVersion,
                KindCount = (ushort)_entries.Length,
                ColumnCount = (ushort)_columns.Length,
                EventCount = (ushort)_events.Length,
                Generation = _builtGeneration,
            };

            if (!_step.IsEmptyIgnoreFilter)
            {
                header.HasStep = 1;
                header.Forward = _step.GetSingleton<TimelineStep>().Forward ? (byte)1 : (byte)0;
            }

            if (!_clock.IsEmptyIgnoreFilter)
            {
                var clockValue = _clock.GetSingleton<TimelineKindClock>();
                header.HasClock = 1;
                header.Now = clockValue.Now;
                header.GlideStepDir = clockValue.GlideStepDir;
                header.GlideLimit = clockValue.GlideLimit;
            }

            var hasDense = !_dense.IsEmptyIgnoreFilter;
            var dense = default(TimelineDense);
            if (hasDense)
            {
                dense = _dense.GetSingleton<TimelineDense>();
                header.HasDense = 1;
                header.DenseClock = dense.Clock;
                header.DenseDirection = dense.Direction;
                header.DenseSequence = dense.Sequence;
            }

            var kindIndex = default(KindIndex);
            var hasKind = false;
            if (!_library.IsEmptyIgnoreFilter)
            {
                kindIndex = _library.GetSingleton<TimelineLibrary>().Kinds;
                hasKind = kindIndex.KindCount > 0 && kindIndex.HeldBufferKinds != null && kindIndex.SlowCount != null;
                if (hasKind)
                {
                    header.HasKind = 1;
                    header.IndexKindCount = kindIndex.KindCount;
                }
            }

            WriteStruct(ref data, in header);
            if (hasKind)
                WriteKindState(ref data, in kindIndex);

            var entityHandle = manager.GetEntityTypeHandle();
            var playbackHandle = manager.GetBufferTypeHandle<Playback>(true);
            var requestHandle = manager.GetBufferTypeHandle<TimelineRequest>(true);
            var playingHandle = manager.GetComponentTypeHandle<TimelinePlaying>(true);
            var horizonHandle = manager.GetComponentTypeHandle<TimelineHorizon>(true);
            var clockHandle = manager.GetComponentTypeHandle<TimelineClock>(true);
            var dueHandle = manager.GetBufferTypeHandle<PlaybackDue>(true);
            var denseChunkHandle = manager.GetComponentTypeHandle<TimelineDenseChunk>(true);

            var entityCount = 0;
            using var chunks = _playbacks.ToArchetypeChunkArray(Allocator.Temp);

            for (var c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                var entities = chunk.GetNativeArray(entityHandle);
                var playbacks = chunk.GetBufferAccessor(ref playbackHandle);
                var hasDenseChunk = chunk.HasChunkComponent(ref denseChunkHandle);
                var denseChunk = hasDenseChunk ? chunk.GetChunkComponentData(ref denseChunkHandle) : default;
                var hasRequests = chunk.Has(ref requestHandle);
                var requests = hasRequests ? chunk.GetBufferAccessor(ref requestHandle) : default;
                var hasPlaying = chunk.Has(ref playingHandle);
                var playings = hasPlaying ? chunk.GetNativeArray(ref playingHandle) : default;
                var playingBits = hasPlaying ? chunk.GetEnableableBits(ref playingHandle) : default;
                var hasHorizon = chunk.Has(ref horizonHandle);
                var horizons = hasHorizon ? chunk.GetNativeArray(ref horizonHandle) : default;
                var hasClock = chunk.Has(ref clockHandle);
                var clocks = hasClock ? chunk.GetNativeArray(ref clockHandle) : default;
                var hasDue = chunk.Has(ref dueHandle);
                var dues = hasDue ? chunk.GetBufferAccessor(ref dueHandle) : default;

                for (var row = 0; row < chunk.Count; row++)
                {
                    entityCount++;
                    var entity = entities[row];
                    WriteStruct(ref data, in entity);

                    WriteByte(ref data, hasDenseChunk ? (byte)1 : (byte)0);
                    if (hasDenseChunk)
                        WriteStruct(ref data, in denseChunk);

                    var pb = playbacks[row];
                    WriteValue(ref data, pb.Length);
                    if (pb.Length > 0)
                        WriteBytes(ref data, pb.GetUnsafeReadOnlyPtr(), pb.Length * sizeof(Playback));

                    if (hasRequests)
                    {
                        WriteByte(ref data, 1);
                        var req = requests[row];
                        WriteValue(ref data, req.Length);
                        if (req.Length > 0)
                            WriteBytes(ref data, req.GetUnsafeReadOnlyPtr(), req.Length * sizeof(TimelineRequest));
                    }
                    else
                    {
                        WriteByte(ref data, 0);
                    }

                    if (hasPlaying)
                    {
                        WriteByte(ref data, 1);
                        WriteByte(ref data, BitAt(playingBits, row) ? (byte)1 : (byte)0);
                        var val = playings[row];
                        WriteStruct(ref data, in val);
                    }
                    else
                    {
                        WriteByte(ref data, 0);
                    }

                    if (hasHorizon)
                    {
                        WriteByte(ref data, 1);
                        var val = horizons[row];
                        WriteStruct(ref data, in val);
                    }
                    else
                    {
                        WriteByte(ref data, 0);
                    }

                    if (hasClock)
                    {
                        WriteByte(ref data, 1);
                        var val = clocks[row];
                        WriteStruct(ref data, in val);
                    }
                    else
                    {
                        WriteByte(ref data, 0);
                    }

                    if (hasDue)
                    {
                        WriteByte(ref data, 1);
                        var due = dues[row];
                        WriteValue(ref data, due.Length);
                        if (due.Length > 0)
                            WriteBytes(ref data, due.GetUnsafeReadOnlyPtr(), due.Length * sizeof(PlaybackDue));
                    }
                    else
                    {
                        WriteByte(ref data, 0);
                    }

                    for (var k = 0; k < _entries.Length; k++)
                    {
                        var st = _entries[k];
                        var handle = manager.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(st.TypeIndex));
                        if (!chunk.Has(ref handle))
                        {
                            WriteByte(ref data, 0);
                        }
                        else
                        {
                            WriteByte(ref data, 1);
                            var acc = chunk.GetUntypedBufferAccessor(ref handle);
                            var len = acc.GetBufferLength(row);
                            WriteValue(ref data, len);
                            if (len > 0)
                                WriteBytes(ref data, acc.GetUnsafeReadOnlyPtr(row), len * acc.ElementSize);
                        }
                    }

                    for (var k = 0; k < _actives.Length; k++)
                        CaptureDynamicComponent(manager, in chunk, row, _actives[k], ref data);

                    for (var k = 0; k < _enableds.Length; k++)
                        CaptureDynamicComponent(manager, in chunk, row, _enableds[k], ref data);

                    for (var i = 0; i < _columns.Length; i++)
                        CaptureDynamicComponent(manager, in chunk, row, _columns[i], ref data);

                    for (var i = 0; i < _events.Length; i++)
                    {
                        var st = _events[i];
                        var handle = manager.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(st.TypeIndex));
                        if (!chunk.Has(ref handle))
                        {
                            WriteByte(ref data, 0);
                        }
                        else
                        {
                            WriteByte(ref data, 1);
                            var acc = chunk.GetUntypedBufferAccessor(ref handle);
                            var len = acc.GetBufferLength(row);
                            WriteValue(ref data, len);
                            if (len > 0)
                                WriteBytes(ref data, acc.GetUnsafeReadOnlyPtr(row), len * acc.ElementSize);
                        }
                    }
                }
            }

            ((Header*)data.GetUnsafePtr())->EntityCount = entityCount;
            into.Data = data;
            return true;
        }

        public bool Restore(EntityManager manager, in TimelineSnapshot from)
        {
            if (!from.IsCreated || from.Length < sizeof(Header))
                return false;

            manager.CompleteAllTrackedJobs();
            EnsureSchema(manager);

            var ptr = (byte*)from.Data.GetUnsafeReadOnlyPtr();
            var end = ptr + from.Length;
            var header = *(Header*)ptr;
            ptr += sizeof(Header);

            if (header.Magic != Magic || header.Version != FormatVersion)
                return false;
            if (header.Generation != _builtGeneration)
                return false;
            if (header.KindCount != _entries.Length || header.ColumnCount != _columns.Length || header.EventCount != _events.Length)
                return false;

            if (header.HasStep != 0 && !_step.IsEmptyIgnoreFilter)
                _step.SetSingleton(new TimelineStep { Forward = header.Forward != 0 });

            if (header.HasClock != 0 && !_clock.IsEmptyIgnoreFilter)
                _clock.SetSingleton(new TimelineKindClock { Now = header.Now, GlideStepDir = header.GlideStepDir, GlideLimit = header.GlideLimit });

            if (!_dense.IsEmptyIgnoreFilter && header.HasDense != 0)
            {
                var currentDense = _dense.GetSingleton<TimelineDense>();
                _dense.SetSingleton(new TimelineDense
                {
                    Clock = header.DenseClock,
                    Direction = header.DenseDirection,
                    Sequence = header.DenseSequence,
                    Space = currentDense.Space,
                });
            }

            if (!_library.IsEmptyIgnoreFilter)
            {
                var index = _library.GetSingleton<TimelineLibrary>().Kinds;
                if (header.HasKind != 0)
                {
                    if (index.KindCount != header.IndexKindCount || index.HeldBufferKinds == null || index.SlowCount == null)
                        return false;
                    if (!ReadKindState(ref ptr, end, ref index))
                        return false;
                }
                ClearKindTransients(ref index);
            }

            if (!_closed.IsEmptyIgnoreFilter)
            {
                using var closedEntities = _closed.ToEntityArray(Allocator.Temp);
                for (var i = 0; i < closedEntities.Length; i++)
                    manager.GetBuffer<ClosedPlayback>(closedEntities[i]).Clear();
            }

            var denseChunkHandle = manager.GetComponentTypeHandle<TimelineDenseChunk>(false);
            var entityHandle = manager.GetEntityTypeHandle();
            var playbackHandle = manager.GetBufferTypeHandle<Playback>(false);
            var requestHandle = manager.GetBufferTypeHandle<TimelineRequest>(false);
            var playingHandle = manager.GetComponentTypeHandle<TimelinePlaying>(false);
            var horizonHandle = manager.GetComponentTypeHandle<TimelineHorizon>(false);
            var clockHandle = manager.GetComponentTypeHandle<TimelineClock>(false);
            var dueHandle = manager.GetBufferTypeHandle<PlaybackDue>(false);

            var cachedChunk = default(ArchetypeChunk);
            var cachedEntities = default(NativeArray<Entity>);

            for (var e = 0; e < header.EntityCount; e++)
            {
                if (ptr + sizeof(Entity) > end)
                    return false;
                var entity = *(Entity*)ptr;
                ptr += sizeof(Entity);
                if (!manager.Exists(entity))
                    return false;

                var chunk = manager.GetChunk(entity);
                if (!chunk.Equals(cachedChunk))
                {
                    cachedChunk = chunk;
                    cachedEntities = chunk.GetNativeArray(entityHandle);
                }

                var row = FindRow(cachedEntities, entity);
                if (row < 0)
                    return false;

                if (ReadByte(ref ptr, end) != 0)
                {
                    var denseChunk = ReadStruct<TimelineDenseChunk>(ref ptr, end);
                    if (chunk.HasChunkComponent(ref denseChunkHandle))
                        chunk.GetChunkComponentDataRW(ref denseChunkHandle) = denseChunk;
                }

                var pbCount = ReadInt(ref ptr, end);
                if (pbCount < 0)
                    return false;
                var pbBuffer = chunk.GetBufferAccessor(ref playbackHandle)[row];
                pbBuffer.ResizeUninitialized(pbCount);
                if (pbCount > 0)
                {
                    var bytes = pbCount * sizeof(Playback);
                    if (ptr + bytes > end)
                        return false;
                    UnsafeUtility.MemCpy(pbBuffer.GetUnsafePtr(), ptr, bytes);
                    ptr += bytes;
                }

                if (ReadByte(ref ptr, end) != 0)
                {
                    var reqCount = ReadInt(ref ptr, end);
                    if (reqCount < 0)
                        return false;
                    if (chunk.Has(ref requestHandle))
                    {
                        var reqBuffer = chunk.GetBufferAccessor(ref requestHandle)[row];
                        reqBuffer.ResizeUninitialized(reqCount);
                        if (reqCount > 0)
                        {
                            var bytes = reqCount * sizeof(TimelineRequest);
                            if (ptr + bytes > end)
                                return false;
                            UnsafeUtility.MemCpy(reqBuffer.GetUnsafePtr(), ptr, bytes);
                            ptr += bytes;
                        }
                    }
                    else
                    {
                        ptr += reqCount * sizeof(TimelineRequest);
                    }
                }

                if (ReadByte(ref ptr, end) != 0)
                {
                    var enabled = ReadByte(ref ptr, end) != 0;
                    var val = ReadStruct<TimelinePlaying>(ref ptr, end);
                    if (chunk.Has(ref playingHandle))
                    {
                        var column = chunk.GetNativeArray(ref playingHandle);
                        column[row] = val;
                        chunk.SetComponentEnabled(ref playingHandle, row, enabled);
                    }
                }

                if (ReadByte(ref ptr, end) != 0)
                {
                    var val = ReadStruct<TimelineHorizon>(ref ptr, end);
                    if (chunk.Has(ref horizonHandle))
                    {
                        var column = chunk.GetNativeArray(ref horizonHandle);
                        column[row] = val;
                    }
                }

                if (ReadByte(ref ptr, end) != 0)
                {
                    var val = ReadStruct<TimelineClock>(ref ptr, end);
                    if (chunk.Has(ref clockHandle))
                    {
                        var column = chunk.GetNativeArray(ref clockHandle);
                        column[row] = val;
                    }
                }

                if (ReadByte(ref ptr, end) != 0)
                {
                    var dueCount = ReadInt(ref ptr, end);
                    if (dueCount < 0)
                        return false;
                    if (chunk.Has(ref dueHandle))
                    {
                        var dueBuffer = chunk.GetBufferAccessor(ref dueHandle)[row];
                        dueBuffer.ResizeUninitialized(dueCount);
                        if (dueCount > 0)
                        {
                            var bytes = dueCount * sizeof(PlaybackDue);
                            if (ptr + bytes > end)
                                return false;
                            UnsafeUtility.MemCpy(dueBuffer.GetUnsafePtr(), ptr, bytes);
                            ptr += bytes;
                        }
                    }
                    else
                    {
                        ptr += dueCount * sizeof(PlaybackDue);
                    }
                }

                for (var k = 0; k < _entries.Length; k++)
                {
                    if (!RestoreDynamicBuffer(manager, in chunk, row, _entries[k], ref ptr, end))
                        return false;
                }

                for (var k = 0; k < _actives.Length; k++)
                {
                    if (!RestoreDynamicComponent(manager, in chunk, row, _actives[k], ref ptr, end))
                        return false;
                }

                for (var k = 0; k < _enableds.Length; k++)
                {
                    if (!RestoreDynamicComponent(manager, in chunk, row, _enableds[k], ref ptr, end))
                        return false;
                }

                for (var i = 0; i < _columns.Length; i++)
                {
                    if (!RestoreDynamicComponent(manager, in chunk, row, _columns[i], ref ptr, end))
                        return false;
                }

                for (var i = 0; i < _events.Length; i++)
                {
                    if (!RestoreDynamicBuffer(manager, in chunk, row, _events[i], ref ptr, end))
                        return false;
                }
            }

            return true;
        }

        public void Dispose()
        {
            if (_queriesCreated == 0)
                return;

            _playbacks.Dispose();
            _library.Dispose();
            _step.Dispose();
            _dense.Dispose();
            _clock.Dispose();
            _closed.Dispose();

            if (_entries.IsCreated) _entries.Dispose();
            if (_actives.IsCreated) _actives.Dispose();
            if (_enableds.IsCreated) _enableds.Dispose();
            if (_columns.IsCreated) _columns.Dispose();
            if (_events.IsCreated) _events.Dispose();

            this = default;
        }

        private void EnsureSchema(EntityManager manager)
        {
            if (_queriesCreated == 0)
            {
                _queriesCreated = 1;
                const EntityQueryOptions every = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab;
                _playbacks = new EntityQueryBuilder(Allocator.Temp).WithAll<Playback>().WithOptions(every).Build(manager);
                _library = new EntityQueryBuilder(Allocator.Temp).WithAll<TimelineLibrary>().Build(manager);
                _step = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TimelineStep>().Build(manager);
                _dense = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TimelineDense>().WithOptions(EntityQueryOptions.IncludeSystems).Build(manager);
                _clock = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TimelineKindClock>().WithOptions(EntityQueryOptions.IncludeSystems).Build(manager);
                _closed = new EntityQueryBuilder(Allocator.Temp).WithAll<TimelineLibrary, ClosedPlayback>().Build(manager);
                _entries = new NativeList<SnapshotType>(8, Allocator.Persistent);
                _actives = new NativeList<SnapshotType>(8, Allocator.Persistent);
                _enableds = new NativeList<SnapshotType>(8, Allocator.Persistent);
                _columns = new NativeList<SnapshotType>(16, Allocator.Persistent);
                _events = new NativeList<SnapshotType>(8, Allocator.Persistent);
            }

            var currentGen = _library.IsEmptyIgnoreFilter ? 0u : _library.GetSingleton<TimelineLibrary>().Generation;
            if (_schemaBuilt != 0 && currentGen == _builtGeneration)
                return;

            _schemaBuilt = 1;
            _builtGeneration = currentGen;

            _entries.Clear();
            _actives.Clear();
            _enableds.Clear();
            _columns.Clear();
            _events.Clear();

            var registry = KindRegistry.Discover();
            for (var k = 0; k < registry.Count; k++)
            {
                var d = registry[k];
                _entries.Add(DescribeBuffer(d.Entry));
                _actives.Add(DescribeComponent(d.Active, true));
                _enableds.Add(d.Enabled != null ? DescribeComponent(d.Enabled, true) : default);

                for (var c = 0; c < d.Columns.Length; c++)
                {
                    var col = d.Columns[c];
                    var st = DescribeComponent(col, typeof(IEnableableComponent).IsAssignableFrom(col));
                    if (!ContainsType(_columns, st.TypeIndex))
                        _columns.Add(st);
                }
            }

            for (var e = 0; e < registry.EventBuffers.Count; e++)
            {
                var st = DescribeBuffer(registry.EventBuffers[e]);
                if (!ContainsType(_events, st.TypeIndex))
                    _events.Add(st);
            }
        }

        private static SnapshotType DescribeBuffer(Type type)
        {
            var ct = ComponentType.ReadWrite(type);
            var info = TypeManager.GetTypeInfo(ct.TypeIndex);
            return new SnapshotType
            {
                TypeIndex = ct.TypeIndex,
                ElementSize = info.ElementSize,
                Flags = 0,
            };
        }

        private static SnapshotType DescribeComponent(Type type, bool enableable)
        {
            var ct = ComponentType.ReadWrite(type);
            var info = TypeManager.GetTypeInfo(ct.TypeIndex);
            byte flags = 0;
            if (info.IsZeroSized)
                flags |= FlagZeroSized;
            if (enableable)
                flags |= FlagEnableable;
            return new SnapshotType
            {
                TypeIndex = ct.TypeIndex,
                ElementSize = info.IsZeroSized ? 0 : info.TypeSize,
                Flags = flags,
            };
        }

        private static bool ContainsType(NativeList<SnapshotType> list, TypeIndex typeIndex)
        {
            for (var i = 0; i < list.Length; i++)
                if (list[i].TypeIndex == typeIndex)
                    return true;
            return false;
        }

        private static void WriteKindState(ref NativeList<byte> data, in KindIndex index)
        {
            var kinds = index.KindCount;
            WriteByte(ref data, index.Dense != null ? (byte)1 : (byte)0);
            WriteValue(ref data, index.Dense != null ? (int)index.Dense->WalkSequence : 0);
            WriteValue(ref data, index.TickFastPulses != null ? (int)*index.TickFastPulses : 0);
            WriteValue(ref data, index.TickFastPulses != null ? (int)(*index.TickFastPulses >> 32) : 0);
            WriteValue(ref data, index.SampledCount != null ? *index.SampledCount : 0);
            WriteValue(ref data, index.PrevSampledCount != null ? *index.PrevSampledCount : 0);
            WriteBytes(ref data, index.HeldBufferKinds, kinds);
            WriteBytes(ref data, index.SlowCount, kinds * sizeof(int));
            WriteBytes(ref data, index.SpillCount, kinds * sizeof(int));
            WriteBytes(ref data, index.HeldSeen, kinds * sizeof(int));
        }

        private static bool ReadKindState(ref byte* ptr, byte* end, ref KindIndex index)
        {
            var kinds = index.KindCount;
            var need = 1 + 5 * sizeof(int) + kinds + 3 * kinds * sizeof(int);
            if (ptr + need > end)
                return false;

            var hasDense = ReadByte(ref ptr, end) != 0;
            var walk = ReadInt(ref ptr, end);
            var fastLo = (uint)ReadInt(ref ptr, end);
            var fastHi = (uint)ReadInt(ref ptr, end);
            var sampled = ReadInt(ref ptr, end);
            var prevSampled = ReadInt(ref ptr, end);

            if (hasDense && index.Dense != null)
                index.Dense->WalkSequence = (uint)walk;
            if (index.TickFastPulses != null)
                *index.TickFastPulses = ((ulong)fastHi << 32) | fastLo;
            if (index.SampledCount != null)
                *index.SampledCount = sampled;
            if (index.PrevSampledCount != null)
                *index.PrevSampledCount = prevSampled;

            UnsafeUtility.MemCpy(index.HeldBufferKinds, ptr, kinds);
            ptr += kinds;
            UnsafeUtility.MemCpy(index.SlowCount, ptr, kinds * sizeof(int));
            ptr += kinds * sizeof(int);
            UnsafeUtility.MemCpy(index.SpillCount, ptr, kinds * sizeof(int));
            ptr += kinds * sizeof(int);
            UnsafeUtility.MemCpy(index.HeldSeen, ptr, kinds * sizeof(int));
            ptr += kinds * sizeof(int);
            return true;
        }

        private static void ClearKindTransients(ref KindIndex index)
        {
            if (index.KindCount <= 0)
                return;

            if (index.PulseLanes != null)
            {
                var total = index.KindCount * index.ThreadCount;
                for (var i = 0; i < total; i++)
                    index.PulseLanes[i].Count = 0;
            }

            if (index.WorkerCache != null)
                for (var t = 0; t < index.ThreadCount; t++)
                    index.WorkerCache[t].Flags = 0;
        }

        private static void CaptureDynamicComponent(EntityManager manager, in ArchetypeChunk chunk, int row, in SnapshotType st, ref NativeList<byte> data)
        {
            if (!st.Valid)
            {
                WriteByte(ref data, 0);
                return;
            }

            var handle = manager.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(st.TypeIndex));
            if (!chunk.Has(ref handle))
            {
                WriteByte(ref data, 0);
                return;
            }

            WriteByte(ref data, 1);
            if (st.IsEnableable)
            {
                var bits = chunk.GetEnableableBits(ref handle);
                WriteByte(ref data, BitAt(bits, row) ? (byte)1 : (byte)0);
            }

            if (!st.IsZeroSized && st.ElementSize > 0)
            {
                var col = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref handle, st.ElementSize);
                var origin = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(col);
                WriteBytes(ref data, origin + (long)row * st.ElementSize, st.ElementSize);
            }
        }

        private static bool RestoreDynamicComponent(EntityManager manager, in ArchetypeChunk chunk, int row, in SnapshotType st, ref byte* ptr, byte* end)
        {
            var present = ReadByte(ref ptr, end);
            if (present == 0)
                return true;
            if (!st.Valid)
                return false;

            var handle = manager.GetDynamicComponentTypeHandle(ComponentType.ReadWrite(st.TypeIndex));
            var has = chunk.Has(ref handle);

            if (st.IsEnableable)
            {
                var enabled = ReadByte(ref ptr, end) != 0;
                if (has)
                    chunk.SetComponentEnabled(ref handle, row, enabled);
            }

            if (!st.IsZeroSized && st.ElementSize > 0)
            {
                if (ptr + st.ElementSize > end)
                    return false;
                if (has)
                {
                    var col = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref handle, st.ElementSize);
                    var origin = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(col);
                    UnsafeUtility.MemCpy(origin + (long)row * st.ElementSize, ptr, st.ElementSize);
                }
                ptr += st.ElementSize;
            }

            return true;
        }

        private static bool RestoreDynamicBuffer(EntityManager manager, in ArchetypeChunk chunk, int row, in SnapshotType st, ref byte* ptr, byte* end)
        {
            var present = ReadByte(ref ptr, end);
            if (present == 0)
                return true;
            if (!st.Valid)
                return false;

            var len = ReadInt(ref ptr, end);
            if (len < 0)
                return false;

            var handle = manager.GetDynamicComponentTypeHandle(ComponentType.ReadWrite(st.TypeIndex));
            if (chunk.Has(ref handle))
            {
                var acc = chunk.GetUntypedBufferAccessor(ref handle);
                acc.ResizeUninitialized(row, len);
                var elemSize = acc.ElementSize;
                if (len > 0)
                {
                    var bytes = len * elemSize;
                    if (ptr + bytes > end)
                        return false;
                    UnsafeUtility.MemCpy(acc.GetUnsafePtr(row), ptr, bytes);
                    ptr += bytes;
                }
            }
            else if (len > 0)
            {
                var bytes = len * st.ElementSize;
                if (ptr + bytes > end)
                    return false;
                ptr += bytes;
            }

            return true;
        }

        private static int FindRow(NativeArray<Entity> entities, Entity target)
        {
            for (var i = 0; i < entities.Length; i++)
                if (entities[i] == target)
                    return i;
            return -1;
        }

        private static bool BitAt(global::Unity.Burst.Intrinsics.v128 bits, int row)
        {
            return row < 64
                ? ((bits.ULong0 >> row) & 1ul) != 0ul
                : ((bits.ULong1 >> (row - 64)) & 1ul) != 0ul;
        }

        private static void WriteByte(ref NativeList<byte> dst, byte value)
        {
            dst.Add(value);
        }

        private static void WriteValue(ref NativeList<byte> dst, int value)
        {
            dst.AddRange(&value, sizeof(int));
        }

        private static void WriteStruct<T>(ref NativeList<byte> dst, in T value) where T : unmanaged
        {
            fixed (T* p = &value)
                dst.AddRange(p, sizeof(T));
        }

        private static void WriteBytes(ref NativeList<byte> dst, void* src, int bytes)
        {
            dst.AddRange(src, bytes);
        }

        private static byte ReadByte(ref byte* ptr, byte* end)
        {
            if (ptr >= end)
                return 0;
            return *ptr++;
        }

        private static int ReadInt(ref byte* ptr, byte* end)
        {
            if (ptr + sizeof(int) > end)
                return -1;
            var v = *(int*)ptr;
            ptr += sizeof(int);
            return v;
        }

        private static T ReadStruct<T>(ref byte* ptr, byte* end) where T : unmanaged
        {
            if (ptr + sizeof(T) > end)
                return default;
            var v = *(T*)ptr;
            ptr += sizeof(T);
            return v;
        }
    }
}
