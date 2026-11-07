using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Entities;

namespace Tl
{
    public ref struct TlBaker
    {
        readonly IBaker _baker;
        readonly Entity _entity;
        readonly HashSet<ComponentType> _added;
        readonly UnityEngine.Object _context;
        readonly Dictionary<ComponentType, byte[]> _values;

        public TlBaker(IBaker baker, Entity entity, HashSet<ComponentType> added, UnityEngine.Object context)
        {
            _baker = baker;
            _entity = entity;
            _added = added;
            _context = context;
            _values = new Dictionary<ComponentType, byte[]>();
        }

        public Entity Entity => _entity;

        public bool IsActive => _baker != null;

        public void AddComponent<T>() where T : unmanaged, IComponentData
        {
            if (_added.Add(ComponentType.ReadWrite<T>()))
                _baker.AddComponent<T>(_entity);
        }

        public void AddComponent<T>(T value) where T : unmanaged, IComponentData
        {
            AddBlob(value);
        }

        public void AddBuffer<T>() where T : unmanaged, IBufferElementData
        {
            if (_added.Add(ComponentType.ReadWrite<T>()))
                _baker.AddBuffer<T>(_entity);
        }

        public void AddBlob<T>(T component) where T : unmanaged, IComponentData
        {
            var type = ComponentType.ReadWrite<T>();
            var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref component, 1));
            if (_added.Add(type))
            {
                _baker.AddComponent(_entity, component);
                _values[type] = bytes.ToArray();
            }
            else if (!_values.TryGetValue(type, out var first) || !bytes.SequenceEqual(first))
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
