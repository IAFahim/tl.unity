using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace Tl.Unity
{
    public unsafe struct TimelineKindTick : IComponentData
    {
        [NativeDisableUnsafePtrRestriction] internal UnsafeList<KindPulseSender>* Senders;
        [NativeDisableUnsafePtrRestriction] internal UnsafeList<JobHandle>* Writers;
        [NativeDisableUnsafePtrRestriction] internal int* Counters;
        [NativeDisableUnsafePtrRestriction] internal JobHandle* Last;

        public bool IsCreated => Counters != null;

        public int DroppedPulses => Counter(KindCounter.DroppedPulses);

        public int DroppedStarts => Counter(KindCounter.DroppedStarts);

        public int ManagedRuns => Counter(KindCounter.ManagedRuns);

        int Counter(int index)
        {
            Last->Complete();
            return Counters[index];
        }

        public KindPulseSender BeginPulses(int foreachCount)
        {
            var sender = new KindPulseSender(foreachCount, Allocator.Persistent);
            Senders->Add(sender);
            return sender;
        }

        public void AddPulseWriter(JobHandle writer)
        {
            Writers->Add(writer);
        }

        internal static TimelineKindTick Create()
        {
            var tick = default(TimelineKindTick);
            tick.Senders = UnsafeList<KindPulseSender>.Create(4, Allocator.Persistent);
            tick.Writers = UnsafeList<JobHandle>.Create(4, Allocator.Persistent);
            var bytes = KindCounter.Count * sizeof(int);
            tick.Counters = (int*)UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent);
            UnsafeUtility.MemClear(tick.Counters, bytes);
            tick.Last = (JobHandle*)UnsafeUtility.Malloc(sizeof(JobHandle), 16, Allocator.Persistent);
            *tick.Last = default;
            return tick;
        }

        internal void Dispose()
        {
            if (!IsCreated)
                return;
            for (var i = 0; i < Senders->Length; i++)
                Senders->ElementAt(i).Dispose();
            UnsafeList<KindPulseSender>.Destroy(Senders);
            UnsafeList<JobHandle>.Destroy(Writers);
            UnsafeUtility.Free(Counters, Allocator.Persistent);
            UnsafeUtility.Free(Last, Allocator.Persistent);
            this = default;
        }
    }

    public struct TimelineKindClock : IComponentData
    {
        public ushort Now;
        public int GlideStepDir;
        public int GlideLimit;
    }

    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineRequestSystem))]
    [UpdateAfter(typeof(TimelineAdvanceSystem))]
    public unsafe partial struct TimelineKindTickSystem : ISystem
    {
        EntityQuery _query;
        BufferTypeHandle<Playback> _playbackHandle;
        BufferTypeHandle<TimelineRequest> _requestHandle;
        ComponentTypeHandle<TimelinePlaying> _playingHandle;
        ComponentTypeHandle<TimelineHorizon> _horizonHandle;
        ComponentTypeHandle<TimelineClock> _clockHandle;
        BufferTypeHandle<PlaybackDue> _dueHandle;
        EntityTypeHandle _entityHandle;
        EntityStorageInfoLookup _storage;
        KindHandleSet _handles;
        TimelineKindTick _tick;
        uint _bound;
        bool _isBound;
        uint _glideGeneration;
        ulong _glideIndex;
        DenseSpace* _space;
        EntityQuery _bare;
        ComponentTypeHandle<TimelineDenseChunk> _denseChunkHandle;
        ulong _spaceIndex;
        uint _spaceGeneration;
        bool _spaceStale;
        NativeQueue<Entity> _resets;
        NativeHashSet<Entity> _resetSet;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _tick = TimelineKindTick.Create();
            _resets = new NativeQueue<Entity>(Allocator.Persistent);
            _resetSet = new NativeHashSet<Entity>(16, Allocator.Persistent);
            state.EntityManager.AddComponentData(state.SystemHandle, _tick);
            state.EntityManager.AddComponentData(state.SystemHandle, new TimelineKindClock());
            state.EntityManager.AddComponentData(state.SystemHandle, TimelineGlide.Default);
            state.EntityManager.AddComponentData(state.SystemHandle, new TimelineDense());
            _query = new EntityQueryBuilder(Allocator.Temp)
                .WithAllRW<Playback>()
                .WithAllRW<TimelineRequest>()
                .WithAllRW<TimelinePlaying>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(ref state);
            _bare = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Playback, TimelineRequest, TimelinePlaying>()
                .WithNoneChunkComponent<TimelineDenseChunk>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(ref state);
            _denseChunkHandle = state.GetComponentTypeHandle<TimelineDenseChunk>(false);
            _playbackHandle = state.GetBufferTypeHandle<Playback>(false);
            _requestHandle = state.GetBufferTypeHandle<TimelineRequest>(false);
            _playingHandle = state.GetComponentTypeHandle<TimelinePlaying>(false);
            _horizonHandle = state.GetComponentTypeHandle<TimelineHorizon>(false);
            _clockHandle = state.GetComponentTypeHandle<TimelineClock>(false);
            _dueHandle = state.GetBufferTypeHandle<PlaybackDue>(false);
            _entityHandle = state.GetEntityTypeHandle();
            _storage = state.GetEntityStorageInfoLookup();
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
        }

        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();
            _tick.Dispose();
            _resets.Dispose();
            _resetSet.Dispose();
            DenseSpace.Release(_space);
            _space = null;
        }

        void Bind(ref SystemState state, in KindIndex index)
        {
            var set = default(KindHandleSet);
            for (var k = 0; k < index.KindCount; k++)
            {
                if ((index.Bound & (1ul << k)) == 0) continue;
                KindHandleSet.Put(ref set, k, state.GetDynamicComponentTypeHandle(index.EntryTypes[k]), state.GetDynamicComponentTypeHandle(index.ActiveTypes[k]));
            }
            _handles = set;
            _isBound = true;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            if (library.Kinds.KindCount == 0)
                return;
            var index = library.Kinds;
            var identity = (ulong)index.WindowStep;
            if (!_isBound || _bound != library.Generation)
            {
                state.CompleteDependency();
                Bind(ref state, in index);
                _bound = library.Generation;
            }
            if (!_bare.IsEmptyIgnoreFilter)
                state.EntityManager.AddComponent(_bare, ComponentType.ChunkComponent<TimelineDenseChunk>());
            KindHandleSet.Update(ref _handles, ref state, index.KindCount);
            _denseChunkHandle.Update(ref state);
            _playbackHandle.Update(ref state);
            _requestHandle.Update(ref state);
            _playingHandle.Update(ref state);
            _horizonHandle.Update(ref state);
            _clockHandle.Update(ref state);
            _dueHandle.Update(ref state);
            _entityHandle.Update(ref state);
            var clock = state.EntityManager.GetComponentData<TimelineKindClock>(state.SystemHandle);
            var now = (ushort)((clock.Now + 1) & TimelineTicks.Mask);
            var dense = state.EntityManager.GetComponentData<TimelineDense>(state.SystemHandle);
            var denseStep = SystemAPI.GetSingleton<TimelineStep>().Forward ? 1 : -1;
            var denseForget = dense.Direction != denseStep;
            var walked = index.Dense != null && index.Dense->WalkSequence == dense.Sequence;
            if (!walked && dense.Space != null && index.Dense != null && index.Dense->Ready &&
                _spaceIndex == identity && _spaceGeneration == library.Generation)
                state.Dependency = new KindRecoverDensePulsesJob
                {
                    Index = index,
                    PlaybackHandle = _playbackHandle,
                    EntityHandle = _entityHandle,
                    Clock = dense.Clock,
                    Step = dense.Direction,
                }.ScheduleParallel(_query, state.Dependency);
            if (_spaceStale)
            {
                state.CompleteDependency();
                DenseSpace.Release(_space);
                _space = null;
                _spaceStale = false;
            }
            if (index.Dense != null && index.Dense->BuiltEpoch != index.Dense->TableEpoch)
            {
                state.CompleteDependency();
                KindDense.Refresh(ref index);
                denseForget = true;
            }
            if (_space != null && (_spaceIndex != identity || _spaceGeneration != library.Generation))
            {
                denseForget = true;
                _spaceStale = true;
            }
            else if (_space == null && index.Dense != null && index.Dense->Ready)
            {
                _space = DenseSpace.Of(index.Dense, index.Timelines, index.TimelineCount);
                _spaceIndex = identity;
                _spaceGeneration = library.Generation;
            }
            var stepDir = SystemAPI.GetSingleton<TimelineStep>().Forward ? 1 : -1;
            var limit = (int)state.EntityManager.GetComponentData<TimelineGlide>(state.SystemHandle).Limit;
            var forget = _glideIndex != identity || _glideGeneration != library.Generation || clock.GlideStepDir != stepDir || clock.GlideLimit != limit;
            _glideIndex = identity;
            _glideGeneration = library.Generation;
            state.EntityManager.SetComponentData(state.SystemHandle, new TimelineKindClock { Now = now, GlideStepDir = stepDir, GlideLimit = limit });
            var handles = _handles;
            var counters = _tick.Counters;
            var dependency = state.Dependency;
            var writers = _tick.Writers;
            for (var i = 0; i < writers->Length; i++)
                dependency = JobHandle.CombineDependencies(dependency, (*writers)[i]);
            writers->Clear();
            var senders = _tick.Senders;
            _storage.Update(ref state);
            var orderVer = state.EntityManager.GetComponentOrderVersion<Playback>();
            dependency = new KindPulseClearJob { Index = index, AllowFastPulses = senders->Length == 0, OrderVersion = orderVer, Storage = _storage, Handles = handles }.Schedule(dependency);
            var tick = new KindTickJob
            {
                Index = index,
                EntityHandle = _entityHandle,
                PlaybackHandle = _playbackHandle,
                RequestHandle = _requestHandle,
                PlayingHandle = _playingHandle,
                HorizonHandle = _horizonHandle,
                ClockHandle = _clockHandle,
                DueHandle = _dueHandle,
                Now = now,
                Sweep = (now & 63) == 0,
                Handles = handles,
                Tables = index.Tables,
                Counters = counters,
                StepDir = stepDir,
                LastSystemVersion = state.LastSystemVersion,
                HorizonLimit = limit,
                ForgetHorizons = forget,
                Dense = index.Dense,
                Space = _space,
                DenseFrom = dense.Clock,
                DenseSequence = dense.Sequence,
                DenseReady = !_spaceStale && _space != null,
                DenseForget = denseForget,
                Walked = walked,
                DenseChunkHandle = _denseChunkHandle,
                Resets = _resets.AsParallelWriter(),
                TrackResets = senders->Length != 0,
            };
            dependency = tick.ScheduleParallel(_query, dependency);
            if (senders->Length != 0)
            {
                dependency = new KindResetsJob { Queue = _resets, Set = _resetSet }.Schedule(dependency);
                var storage = _storage;
                for (var i = 0; i < senders->Length; i++)
                {
                    ref var sender = ref senders->ElementAt(i);
                    var stream = sender.Stream;
                    var merge = new KindMergeJob
                    {
                        Reader = stream.AsReader(),
                        ForEachCount = stream.ForEachCount,
                        Storage = storage,
                        Handles = handles,
                        Counters = counters,
                        Index = index,
                        Resets = _resetSet,
                    };
                    dependency = merge.Schedule(dependency);
                    dependency = sender.Dispose(dependency);
                }
                senders->Clear();
            }
            state.EntityManager.SetComponentData(state.SystemHandle, new TimelineDense { Clock = (ushort)(dense.Clock + denseStep), Direction = (sbyte)denseStep, Sequence = dense.Sequence + 1, Space = _space });
            *_tick.Last = dependency;
            state.Dependency = dependency;
        }
    }
}
