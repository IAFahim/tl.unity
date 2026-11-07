using System.Collections.Generic;
using Unity.Entities;

namespace Tl
{
    public ref struct TlBaker
    {
        readonly IBaker _baker;
        readonly Entity _entity;
        readonly HashSet<ComponentType> _added;
        readonly UnityEngine.Object _context;

        public TlBaker(IBaker baker, Entity entity, HashSet<ComponentType> added, UnityEngine.Object context)
        {
            _baker = baker;
            _entity = entity;
            _added = added;
            _context = context;
        }

        public Entity Entity => _entity;

        public void AddComponent<T>() where T : unmanaged, IComponentData
        {
            if (_added.Add(ComponentType.ReadWrite<T>()))
                _baker.AddComponent<T>(_entity);
        }

        public void AddBuffer<T>() where T : unmanaged, IBufferElementData
        {
            if (_added.Add(ComponentType.ReadWrite<T>()))
                _baker.AddBuffer<T>(_entity);
        }

        public void AddBlob<T>(T component) where T : unmanaged, IComponentData
        {
            if (_added.Add(ComponentType.ReadWrite<T>()))
                _baker.AddComponent(_entity, component);
            else
                TlBakeReport.SecondValueIgnored<T>(_context);
        }

        public BlobAssetReference<TlCurveBlob> CreateCurve(in TlCurve curve)
        {
            var blob = TlCurveBlob.Create(curve);
            _baker.AddBlobAsset(ref blob, out _);
            return blob;
        }
    }
}
