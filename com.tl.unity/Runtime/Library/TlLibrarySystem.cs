using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct TlLibrarySystem : ISystem
    {
        GCHandle _library;
        Entity _entity;
        EntityQuery _pending;

        public void OnCreate(ref SystemState state)
        {
            _pending = TlTimelineQueries.Pending(ref state);
            state.RequireForUpdate(_pending);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_pending.IsEmpty)
                return;
            state.Enabled = false;
            if (_library.IsAllocated)
                return;
            _library = GCHandle.Alloc(new TlLibrary());
            var manager = state.EntityManager;
            _entity = manager.CreateEntity();
            manager.AddComponentData(_entity, new TlLibraryHandle { Value = _library });
            manager.AddComponentData(_entity, new TlLibraryIndex { Ids = new NativeHashMap<Hash128, ushort>(16, Allocator.Persistent) });
        }

        public void OnDestroy(ref SystemState state)
        {
            if (!_library.IsAllocated)
                return;
            var manager = state.EntityManager;
            manager.CompleteAllTrackedJobs();
            if (manager.Exists(_entity) && manager.HasComponent<TlLibraryIndex>(_entity))
            {
                var index = manager.GetComponentData<TlLibraryIndex>(_entity);
                if (index.Ids.IsCreated)
                    index.Ids.Dispose();
                manager.SetComponentData(_entity, default(TlLibraryIndex));
            }
            ((TlLibrary)_library.Target).Dispose();
            _library.Free();
        }
    }
}
