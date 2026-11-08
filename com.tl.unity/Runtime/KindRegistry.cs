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
        public KindDescriptor(string name, Type entry, Type active, Type enabled, int valueBytes, int entryBytes, string signature, Type[] columns)
        {
            Name = name;
            Entry = entry;
            Active = active;
            Enabled = enabled;
            ValueBytes = valueBytes;
            EntryBytes = entryBytes;
            Signature = signature;
            Columns = columns ?? Array.Empty<Type>();
        }

        public readonly string Name;
        public readonly Type Entry;
        public readonly Type Active;
        public readonly Type Enabled;
        public readonly int ValueBytes;
        public readonly int EntryBytes;
        public readonly string Signature;
        public readonly Type[] Columns;
    }

    public readonly struct KindFeeds
    {
        public KindFeeds(string name, int key, string signature)
        {
            Name = name;
            Key = key;
            Signature = signature;
        }

        public readonly string Name;
        public readonly int Key;
        public readonly string Signature;
    }

    public static class TlAssemblies
    {
        public static System.Collections.Generic.IEnumerable<System.Reflection.Assembly> Loaded()
        {
#if UNITY_7000_0_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
#else
            return AppDomain.CurrentDomain.GetAssemblies();
#endif
        }
    }

    public sealed class KindRegistry
    {
        readonly List<KindDescriptor> _kinds = new List<KindDescriptor>();
        readonly List<string> _sources = new List<string>();
        readonly List<Type> _events = new List<Type>();
        readonly List<KindFeeds> _feeds = new List<KindFeeds>();
        readonly List<string> _feedSources = new List<string>();
        string _source = "unknown";

        public int Count => _kinds.Count;

        public KindDescriptor this[int index] => _kinds[index];

        public string SourceOf(int index) => _sources[index];

        public IReadOnlyList<Type> EventBuffers => _events;

        public IReadOnlyList<KindFeeds> Feeds => _feeds;

        public string FeedSourceOf(int index) => _feedSources[index];

        public void AddFeeds(KindFeeds feeds)
        {
            for (var i = 0; i < _feeds.Count; i++)
                if (_feeds[i].Name == feeds.Name && _feeds[i].Key == feeds.Key)
                    throw new ArgumentException("Kind '" + feeds.Name + "' gets feeders from assembly '" + _feedSources[i] + "' and '" + _source + "' with the same key " + feeds.Key + "; rename one of the assemblies.");
            _feeds.Add(feeds);
            _feedSources.Add(_source);
        }

        public void AddEvents(Type buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (!_events.Contains(buffer))
                _events.Add(buffer);
        }

        public void Add(KindDescriptor descriptor)
        {
            for (var i = 0; i < _kinds.Count; i++)
                if (_kinds[i].Name == descriptor.Name)
                    throw new ArgumentException("Kind '" + descriptor.Name + "' is registered twice: by assembly '" + _sources[i] + "' and by assembly '" + _source + "'. Kind names must be unique across assemblies; rename one of the kinds.");
                else if (KindIndex.IdOf(_kinds[i].Name) == KindIndex.IdOf(descriptor.Name))
                    throw new ArgumentException("Kinds '" + _kinds[i].Name + "' (assembly '" + _sources[i] + "') and '" + descriptor.Name + "' (assembly '" + _source + "') have the same kind id " + KindIndex.IdOf(descriptor.Name) + "; rename one of the kinds.");
            _kinds.Add(descriptor);
            _sources.Add(_source);
        }

        public void Include(IKindModule module, string assembly)
        {
            if (module == null)
                throw new ArgumentNullException(nameof(module));
            var previous = _source;
            _source = string.IsNullOrEmpty(assembly) ? "unknown" : assembly;
            try
            {
                module.Register(this);
            }
            finally
            {
                _source = previous;
            }
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
            foreach (var assembly in TlAssemblies.Loaded())
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
                    registry.Include((IKindModule)Activator.CreateInstance(type), assembly.GetName().Name);
                }
            }
            return registry;
        }
    }
}
