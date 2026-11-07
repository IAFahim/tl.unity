using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(TlLibrarySystem))]
    public partial struct TlTimelineRegisterSystem : ISystem
    {
        EntityQuery _pending;
        EntityQuery _store;
        EntityQuery _published;

        public void OnCreate(ref SystemState state)
        {
            _pending = TlTimelineQueries.Pending(ref state);
            _store = state.GetEntityQuery(ComponentType.ReadOnly<TlLibraryHandle>());
            _published = state.GetEntityQuery(ComponentType.ReadOnly<TimelineLibrary>());
            state.RequireForUpdate(_pending);
            state.RequireForUpdate(_store);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_pending.IsEmpty)
                return;
            var manager = state.EntityManager;
            manager.CompleteAllTrackedJobs();
            var holder = _store.GetSingletonEntity();
            var library = manager.GetComponentData<TlLibraryHandle>(holder).Library;
            var entities = _pending.ToEntityArray(Allocator.Temp);
            var blobs = new List<BlobAssetReference<TlTimelineBlob>>();
            foreach (var entity in entities)
            {
                var set = manager.GetBuffer<TlTimelineSet>(entity);
                for (var i = 0; i < set.Length; i++)
                    blobs.Add(set[i].Blob);
            }
            var errors = new List<string>();
            if (library.Add(blobs, errors))
                Publish(manager, holder, library, errors);
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

        void Publish(EntityManager manager, Entity holder, TlLibrary library, List<string> errors)
        {
            if (!manager.HasComponent<TimelineLibrary>(holder))
            {
                if (!_published.IsEmptyIgnoreFilter)
                {
                    errors.Add("Tl: this world already has a timeline library from TimelineLibraryBootstrap; the timelines baked into SubScenes are not used. Remove the old library setup.");
                    return;
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
        }
    }
}
