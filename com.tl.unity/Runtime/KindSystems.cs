using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace Tl.Unity
{
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineRequestSystem))]
    [UpdateAfter(typeof(TimelineAdvanceSystem))]
    public unsafe partial class TimelineKindTickSystem : SystemBase
    {
        readonly List<KindPulseSender> _senders = new List<KindPulseSender>();
        readonly List<JobHandle> _writers = new List<JobHandle>();
        EntityQuery _query;
        KindRegistry _registry;
        KindHandleSet _handles;
        NativeArray<int> _registryToLibrary;
        NativeArray<int> _counters;
        uint _bound;
        bool _isBound;

        public bool Forward { get; set; } = true;

        public KindRegistry Registry => _registry;

        public int DroppedPulses => _counters[KindCounter.DroppedPulses];

        public int DroppedStarts => _counters[KindCounter.DroppedStarts];

        public int LibraryKind(string name)
        {
            var index = TimelineLibraryBootstrap.Kinds;
            for (var k = 0; k < index.KindCount; k++)
                if (index.Sec.NameOf(k) == name)
                    return k;
            return -1;
        }

        public KindPulseSender BeginPulses(int foreachCount)
        {
            var sender = new KindPulseSender(foreachCount, Allocator.Persistent);
            _senders.Add(sender);
            return sender;
        }

        public void AddPulseWriter(JobHandle writer)
        {
            _writers.Add(writer);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _registry = KindRegistry.Discover();
            _counters = new NativeArray<int>(KindCounter.Count, Allocator.Persistent);
            _query = new EntityQueryBuilder(Allocator.Temp)
                .WithAllRW<Playback>()
                .WithAllRW<TimelineRequest>()
                .WithAllRW<TimelinePlaying>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(this);
            RequireForUpdate<TimelineLibrary>();
        }

        protected override void OnDestroy()
        {
            for (var i = 0; i < _senders.Count; i++)
                _senders[i].Dispose();
            _senders.Clear();
            if (_registryToLibrary.IsCreated) _registryToLibrary.Dispose();
            if (_counters.IsCreated) _counters.Dispose();
            base.OnDestroy();
        }

        void Bind(in KindIndex index)
        {
            if (_registryToLibrary.IsCreated) _registryToLibrary.Dispose();
            _handles = default;
            var set = default(KindHandleSet);
            _registryToLibrary = new NativeArray<int>(System.Math.Max(1, _registry.Count), Allocator.Persistent);
            for (var r = 0; r < _registryToLibrary.Length; r++) _registryToLibrary[r] = -1;
            for (var k = 0; k < index.KindCount; k++)
            {
                var r = _registry.IndexOf(index.Sec.NameOf(k));
                if (r < 0) continue;
                var d = _registry[r];
                index.Verify(k, in d);
                _registryToLibrary[r] = k;
                KindHandleSet.Put(ref set, k, GetDynamicComponentTypeHandle(ComponentType.ReadWrite(d.Entry)), GetDynamicComponentTypeHandle(ComponentType.ReadWrite(d.Active)));
            }
            _handles = set;
            _isBound = true;
        }

        protected override void OnUpdate()
        {
            var index = TimelineLibraryBootstrap.Kinds;
            if (index.KindCount == 0)
                return;
            if (!_isBound || _bound != TimelineLibraryBootstrap.Generation)
            {
                CompleteDependency();
                Bind(in index);
                _bound = TimelineLibraryBootstrap.Generation;
            }
            var handles = _handles;
            KindHandleSet.Update(ref handles, this, index.KindCount);
            _handles = handles;
            var counters = (int*)_counters.GetUnsafePtr();
            var dependency = Dependency;
            for (var i = 0; i < _writers.Count; i++)
                dependency = JobHandle.CombineDependencies(dependency, _writers[i]);
            _writers.Clear();
            var tick = new KindTickJob
            {
                Index = index,
                PlaybackHandle = GetBufferTypeHandle<Playback>(false),
                RequestHandle = GetBufferTypeHandle<TimelineRequest>(false),
                PlayingHandle = GetComponentTypeHandle<TimelinePlaying>(false),
                Handles = handles,
                Counters = counters,
                StepDir = Forward ? 1 : -1,
                LastSystemVersion = LastSystemVersion,
            };
            dependency = tick.ScheduleParallel(_query, dependency);
            if (_senders.Count != 0)
            {
                var storage = GetEntityStorageInfoLookup();
                for (var i = 0; i < _senders.Count; i++)
                {
                    var stream = _senders[i].Stream;
                    var merge = new KindMergeJob
                    {
                        Reader = stream.AsReader(),
                        ForEachCount = stream.ForEachCount,
                        RegistryToLibrary = _registryToLibrary,
                        Storage = storage,
                        Handles = handles,
                        Counters = counters,
                        Index = index,
                    };
                    dependency = merge.Schedule(dependency);
                    dependency = _senders[i].Dispose(dependency);
                }
                _senders.Clear();
            }
            Dependency = dependency;
        }
    }
}
