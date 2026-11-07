using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(TlLibrarySystem))]
    public unsafe partial struct TlTimelineRegisterSystem : ISystem
    {
        EntityQuery _pending;
        EntityQuery _changed;
        EntityQuery _store;
        EntityQuery _published;
        EntityQuery _playbacks;
        BufferTypeHandle<Playback> _playbackHandle;
        ComponentTypeHandle<TimelinePlaying> _playingHandle;

        public void OnCreate(ref SystemState state)
        {
            _pending = TlTimelineQueries.Pending(ref state);
            _changed = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TlTimelineSet>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _changed.SetChangedVersionFilter(ComponentType.ReadWrite<TlTimelineSet>());
            _store = state.GetEntityQuery(ComponentType.ReadOnly<TlLibraryHandle>());
            _published = state.GetEntityQuery(ComponentType.ReadOnly<TimelineLibrary>());
            _playbacks = new EntityQueryBuilder(Allocator.Temp).WithAllRW<Playback>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _playbackHandle = state.GetBufferTypeHandle<Playback>(false);
            _playingHandle = state.GetComponentTypeHandle<TimelinePlaying>(false);
            state.RequireForUpdate(_store);
            state.RequireForUpdate<TlTimelineSet>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_pending.IsEmpty && _changed.IsEmpty)
                return;
            var manager = state.EntityManager;
            manager.CompleteAllTrackedJobs();
            var entities = Collect(manager);
            if (entities.Length == 0)
                return;
            var holder = _store.GetSingletonEntity();
            var library = manager.GetComponentData<TlLibraryHandle>(holder).Library;
            var blobs = new List<BlobAssetReference<TlTimelineBlob>>();
            foreach (var entity in entities)
            {
                var set = manager.GetBuffer<TlTimelineSet>(entity);
                for (var i = 0; i < set.Length; i++)
                    blobs.Add(set[i].Blob);
            }
            var errors = new List<string>();
            if (library.Add(blobs, errors) && Publish(manager, holder, library, errors) && library.Swapped.Count > 0)
                Swap(ref state, manager.GetComponentData<TimelineLibrary>(holder), library.Swapped);
            foreach (var error in errors)
                UnityEngine.Debug.LogError(error);
            foreach (var entity in entities)
            {
                var set = manager.GetBuffer<TlTimelineSet>(entity);
                for (var i = 0; i < set.Length; i++)
                {
                    var element = set[i];
                    element.Id = element.Blob.IsCreated ? library.IdOf(element.Blob.Value.Asset) : (ushort)0;
                    set[i] = element;
                }
                manager.SetComponentEnabled<TlUnregistered>(entity, false);
            }
        }

        NativeList<Entity> Collect(EntityManager manager)
        {
            var result = new NativeList<Entity>(Allocator.Temp);
            var pending = _pending.ToEntityArray(Allocator.Temp);
            result.AddRange(pending);
            var changed = _changed.ToEntityArray(Allocator.Temp);
            foreach (var entity in changed)
            {
                if (pending.Contains(entity))
                    continue;
                var set = manager.GetBuffer<TlTimelineSet>(entity, true);
                for (var i = 0; i < set.Length; i++)
                {
                    if (set[i].Id == 0 && set[i].Blob.IsCreated)
                    {
                        result.Add(entity);
                        break;
                    }
                }
            }
            return result;
        }

        void Swap(ref SystemState state, TimelineLibrary published, List<ushort> swapped)
        {
            var ids = new NativeArray<ushort>(swapped.Count, Allocator.TempJob);
            for (var i = 0; i < swapped.Count; i++)
                ids[i] = swapped[i];
            var index = published.Kinds;
            var handles = default(KindHandleSet);
            for (var k = 0; k < index.KindCount; k++)
            {
                if ((index.Bound & (1ul << k)) == 0) continue;
                KindHandleSet.Put(ref handles, k, state.GetDynamicComponentTypeHandle(index.EntryTypes[k]), state.GetDynamicComponentTypeHandle(index.ActiveTypes[k]));
            }
            _playbackHandle.Update(ref state);
            _playingHandle.Update(ref state);
            new TlTimelineSwapJob
            {
                Index = index,
                View = published.View,
                PlaybackHandle = _playbackHandle,
                PlayingHandle = _playingHandle,
                Handles = handles,
                Swapped = ids,
            }.Run(_playbacks);
            ids.Dispose();
        }

        bool Publish(EntityManager manager, Entity holder, TlLibrary library, List<string> errors)
        {
            if (!manager.HasComponent<TimelineLibrary>(holder))
            {
                if (!_published.IsEmptyIgnoreFilter)
                {
                    errors.Add("Tl: this world already has a timeline library from TimelineLibraryBootstrap; the timelines baked into SubScenes are not used. Remove the old library setup.");
                    return false;
                }
                manager.AddComponentData(holder, new TimelineStep { Forward = true });
                manager.AddBuffer<ClosedPlayback>(holder);
                manager.AddComponentData(holder, library.Component);
            }
            else
                manager.SetComponentData(holder, library.Component);
            var index = manager.GetComponentData<TlLibraryIndex>(holder);
            index.Ids.Clear();
            foreach (var pair in library.Ids)
                index.Ids[pair.Key] = pair.Value;
            return true;
        }
    }
}
