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
        [NativeDisableUnsafePtrRestriction] internal uint* Version;

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
            tick.Version = (uint*)UnsafeUtility.Malloc(sizeof(uint), 16, Allocator.Persistent);
            *tick.Version = 0;
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
            UnsafeUtility.Free(Version, Allocator.Persistent);
            this = default;
        }
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
        ushort _now;
        EntityStorageInfoLookup _storage;
        KindHandleSet _handles;
        TimelineKindTick _tick;
        uint _bound;
        bool _isBound;
        uint _glideGeneration;
        int _glideStepDir;
        int _glideLimit;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _tick = TimelineKindTick.Create();
            state.EntityManager.AddComponentData(state.SystemHandle, _tick);
            state.EntityManager.AddComponentData(state.SystemHandle, TimelineGlide.Default);
            _query = new EntityQueryBuilder(Allocator.Temp)
                .WithAllRW<Playback>()
                .WithAllRW<TimelineRequest>()
                .WithAllRW<TimelinePlaying>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(ref state);
            _playbackHandle = state.GetBufferTypeHandle<Playback>(false);
            _requestHandle = state.GetBufferTypeHandle<TimelineRequest>(false);
            _playingHandle = state.GetComponentTypeHandle<TimelinePlaying>(false);
            _horizonHandle = state.GetComponentTypeHandle<TimelineHorizon>(false);
            _clockHandle = state.GetComponentTypeHandle<TimelineClock>(false);
            _dueHandle = state.GetBufferTypeHandle<PlaybackDue>(false);
            _storage = state.GetEntityStorageInfoLookup();
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
        }

        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();
            if (SystemAPI.TryGetSingleton<TimelineLibrary>(out var library) && library.Kinds.TickVersion == _tick.Version)
            {
                library.Kinds.TickVersion = null;
                SystemAPI.SetSingleton(library);
            }
            _tick.Dispose();
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
            if (library.Kinds.TickVersion != _tick.Version)
            {
                library.Kinds.TickVersion = _tick.Version;
                SystemAPI.SetSingleton(library);
            }
            var index = library.Kinds;
            if (!_isBound || _bound != library.Generation)
            {
                state.CompleteDependency();
                Bind(ref state, in index);
                _bound = library.Generation;
            }
            KindHandleSet.Update(ref _handles, ref state, index.KindCount);
            _playbackHandle.Update(ref state);
            _requestHandle.Update(ref state);
            _playingHandle.Update(ref state);
            _horizonHandle.Update(ref state);
            _clockHandle.Update(ref state);
            _dueHandle.Update(ref state);
            _now = (ushort)((_now + 1) & TimelineTicks.Mask);
            var stepDir = SystemAPI.GetSingleton<TimelineStep>().Forward ? 1 : -1;
            var limit = (int)state.EntityManager.GetComponentData<TimelineGlide>(state.SystemHandle).Limit;
            var forget = _glideGeneration != library.Generation || _glideStepDir != stepDir || _glideLimit != limit;
            _glideGeneration = library.Generation;
            _glideStepDir = stepDir;
            _glideLimit = limit;
            var handles = _handles;
            var counters = _tick.Counters;
            var dependency = state.Dependency;
            var writers = _tick.Writers;
            for (var i = 0; i < writers->Length; i++)
                dependency = JobHandle.CombineDependencies(dependency, (*writers)[i]);
            writers->Clear();
            var tick = new KindTickJob
            {
                Index = index,
                PlaybackHandle = _playbackHandle,
                RequestHandle = _requestHandle,
                PlayingHandle = _playingHandle,
                HorizonHandle = _horizonHandle,
                ClockHandle = _clockHandle,
                DueHandle = _dueHandle,
                Now = _now,
                Handles = handles,
                Counters = counters,
                StepDir = stepDir,
                LastSystemVersion = state.LastSystemVersion,
                HorizonLimit = limit,
                ForgetHorizons = forget,
                TickVersion = state.GlobalSystemVersion,
            };
            dependency = tick.ScheduleParallel(_query, dependency);
            var senders = _tick.Senders;
            if (senders->Length != 0)
            {
                _storage.Update(ref state);
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
                    };
                    dependency = merge.Schedule(dependency);
                    dependency = sender.Dispose(dependency);
                }
                senders->Clear();
            }
            *_tick.Last = dependency;
            state.Dependency = dependency;
        }
    }
}
