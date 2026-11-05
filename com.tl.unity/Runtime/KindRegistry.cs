using System;
using System.Collections.Generic;
using Unity.Entities;

namespace Tl.Unity
{
    public struct TimelinePlaying : IComponentData, IEnableableComponent
    {
    }

    public interface IKindModule
    {
        void Register(KindRegistry registry);
    }

    public readonly struct KindDescriptor
    {
        public KindDescriptor(string name, Type entry, Type active, Type enabled, int valueBytes, int entryBytes, string signature)
        {
            Name = name;
            Entry = entry;
            Active = active;
            Enabled = enabled;
            ValueBytes = valueBytes;
            EntryBytes = entryBytes;
            Signature = signature;
        }

        public readonly string Name;
        public readonly Type Entry;
        public readonly Type Active;
        public readonly Type Enabled;
        public readonly int ValueBytes;
        public readonly int EntryBytes;
        public readonly string Signature;
    }

    public sealed class KindRegistry
    {
        readonly List<KindDescriptor> _kinds = new List<KindDescriptor>();

        public int Count => _kinds.Count;

        public KindDescriptor this[int index] => _kinds[index];

        public void Add(KindDescriptor descriptor)
        {
            for (var i = 0; i < _kinds.Count; i++)
                if (_kinds[i].Name == descriptor.Name)
                    throw new ArgumentException("Kind '" + descriptor.Name + "' is registered twice.");
            _kinds.Add(descriptor);
        }

        public int IndexOf(string name)
        {
            for (var i = 0; i < _kinds.Count; i++)
                if (_kinds[i].Name == name)
                    return i;
            return -1;
        }

        public static KindRegistry Discover()
        {
            var registry = new KindRegistry();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }
                foreach (var type in types)
                {
                    if (type.IsInterface || type.IsAbstract || !typeof(IKindModule).IsAssignableFrom(type))
                        continue;
                    if (type.GetConstructor(Type.EmptyTypes) == null)
                        continue;
                    ((IKindModule)Activator.CreateInstance(type)).Register(registry);
                }
            }
            return registry;
        }
    }
}
