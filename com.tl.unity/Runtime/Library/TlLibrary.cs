using System;
using System.Collections.Generic;
using Unity.Entities;

namespace Tl.Unity
{
    public sealed class TlLibrary : IDisposable
    {
        const char Later = '～';

        sealed class Entry
        {
            public Hash128 Asset;
            public Hash128 Content;
            public string Name;
            public string Stored;
            public byte[] Tlb;
            public byte[] Kinds;
            public Hash128[] Next;
            public uint TickRate;
        }

        readonly List<Entry> _entries = new List<Entry>();
        readonly Dictionary<Hash128, ushort> _ids = new Dictionary<Hash128, ushort>();
        readonly TlLibrarySettings _settings;
        global::Tl.TimelineWorld _world;
        KindIndex _kinds;
        bool _disposed;

        public TlLibrary() : this(TlLibrarySettings.Default)
        {
        }

        public TlLibrary(TlLibrarySettings settings)
        {
            _settings = settings;
        }

        public uint TickRate { get; private set; }

        public uint Generation { get; private set; }

        public int Count => _entries.Count;

        public bool IsLoaded => _world != null;

        public byte[] Bytes { get; private set; }

        public TimelineLibrary Component => new TimelineLibrary { View = _world.View, Kinds = _kinds, Generation = Generation };

        public ushort IdOf(Hash128 asset) => _ids.TryGetValue(asset, out var id) ? id : (ushort)0;

        public IEnumerable<KeyValuePair<Hash128, ushort>> Ids => _ids;

        public List<ushort> Swapped { get; } = new List<ushort>();

        public bool Add(IReadOnlyList<BlobAssetReference<TlTimelineBlob>> blobs, List<string> errors)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TlLibrary));
            Swapped.Clear();
            var incoming = Collect(blobs, errors);
            if (incoming.Count == 0)
                return false;
            var entries = new List<Entry>(_entries);
            var tickRate = TickRate;
            var added = new List<Entry>();
            var replaced = false;
            var swapped = new List<ushort>();
            foreach (var entry in incoming)
            {
                if (tickRate == 0)
                    tickRate = entry.TickRate;
                if (entry.TickRate != tickRate)
                {
                    errors.Add("Tl: timeline '" + entry.Name + "' is baked at " + entry.TickRate + " ticks per second but the library runs at " + tickRate + "; use one tick rate for every timeline and rebake. It was not added.");
                    continue;
                }
                if (_ids.TryGetValue(entry.Asset, out var id))
                {
                    if (!SameLayout(entries[id - 1], entry))
                    {
                        errors.Add("Tl: the change to timeline '" + entry.Name + "' adds or removes a type of track, or a script changed. That cannot be swapped in while the game runs, so the earlier version of '" + entry.Name + "' keeps playing. Stop and press Play again to use the change.");
                        continue;
                    }
                    swapped.Add(id);
                    entry.Stored = entries[id - 1].Stored;
                    entries[id - 1] = entry;
                    replaced = true;
                }
                else
                    added.Add(entry);
            }
            added.Sort(Order);
            foreach (var entry in added)
            {
                entry.Stored = StoredName(entries.Count == 0 ? null : entries[entries.Count - 1].Stored, entry.Name);
                entries.Add(entry);
            }
            if (added.Count == 0 && !replaced)
                return false;
            byte[] bytes;
            try
            {
                bytes = TlLibraryAssembler.Assemble(Parts(entries), tickRate, _settings);
            }
            catch (InvalidOperationException e)
            {
                errors.Add(e.Message);
                return false;
            }
            if (!Load(bytes, errors))
                return false;
            _entries.Clear();
            _entries.AddRange(entries);
            _ids.Clear();
            for (var i = 0; i < _entries.Count; i++)
                _ids[_entries[i].Asset] = (ushort)(i + 1);
            TickRate = tickRate;
            Bytes = bytes;
            Swapped.AddRange(swapped);
            Generation++;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_world != null)
            {
                _kinds.Dispose();
                _world.Dispose();
                _world = null;
            }
            GC.SuppressFinalize(this);
        }

        List<Entry> Collect(IReadOnlyList<BlobAssetReference<TlTimelineBlob>> blobs, List<string> errors)
        {
            var seen = new Dictionary<Hash128, Entry>();
            foreach (var blob in blobs)
            {
                if (!blob.IsCreated)
                    continue;
                ref var value = ref blob.Value;
                if (seen.TryGetValue(value.Asset, out var twin))
                {
                    if (twin.Content != value.Content)
                        errors.Add("Tl: two different bakes of timeline '" + twin.Name + "' (" + value.Asset + ") are loaded; the first one is used. Rebake the SubScenes that hold it.");
                    continue;
                }
                if (_ids.TryGetValue(value.Asset, out var id) && _entries[id - 1].Content == value.Content)
                    continue;
                var next = new Hash128[value.NextAssets.Length];
                for (var i = 0; i < next.Length; i++)
                    next[i] = value.NextAssets[i];
                seen[value.Asset] = new Entry
                {
                    Asset = value.Asset,
                    Content = value.Content,
                    Name = TlTimelineBlobs.NameOf(ref value),
                    Tlb = TlTimelineBlobs.ToArray(ref value.Tlb),
                    Kinds = TlTimelineBlobs.ToArray(ref value.Kinds),
                    Next = next,
                    TickRate = value.TickRate,
                };
            }
            var result = new List<Entry>(seen.Values);
            result.Sort(Order);
            return result;
        }

        List<TlLibraryPart> Parts(List<Entry> entries)
        {
            var ids = new Dictionary<Hash128, ushort>();
            for (var i = 0; i < entries.Count; i++)
                ids[entries[i].Asset] = (ushort)(i + 1);
            var parts = new List<TlLibraryPart>(entries.Count);
            foreach (var entry in entries)
                parts.Add(new TlLibraryPart { Name = entry.Stored, Tlb = entry.Tlb, Kinds = entry.Kinds, Next = NextId(entry, ids) });
            return parts;
        }

        static ushort NextId(Entry entry, Dictionary<Hash128, ushort> ids)
        {
            var local = TlLibraryAssembler.NextOf(entry.Tlb);
            if (local == 0 || local > entry.Next.Length)
                return 0;
            return ids.TryGetValue(entry.Next[local - 1], out var id) ? id : (ushort)0;
        }

        bool Load(byte[] bytes, List<string> errors)
        {
            TlGeneratedBootstrap.InstallDiscoveredModules();
            global::Tl.TimelineWorld world = null;
            KindIndex kinds = default;
            var built = false;
            try
            {
                world = new global::Tl.TimelineWorld(bytes);
                kinds = KindIndex.Build(world.View, world);
                built = true;
                kinds.Bind(KindRegistry.Discover());
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                if (built)
                    kinds.Dispose();
                world?.Dispose();
                errors.Add("Tl: the timeline library could not be loaded: " + e.Message);
                return false;
            }
            if (_world != null)
            {
                _kinds.Dispose();
                _world.Dispose();
            }
            _world = world;
            _kinds = kinds;
            return true;
        }

        static bool SameLayout(Entry before, Entry after)
        {
            TlKindPayload a, b;
            try
            {
                a = TlKindPayload.Read(before.Kinds, before.Name);
                b = TlKindPayload.Read(after.Kinds, after.Name);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            if (a == null || b == null)
                return (a == null || a.KindMask == 0) && (b == null || b.KindMask == 0);
            return a.KindMask == b.KindMask && a.SameCatalog(b);
        }

        static string StoredName(string previous, string name)
        {
            var stored = name;
            while (previous != null && string.CompareOrdinal(stored, previous) <= 0)
                stored = Later + stored;
            return stored;
        }

        static int Order(Entry a, Entry b)
        {
            var byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : Compare(a.Asset, b.Asset);
        }

        static int Compare(Hash128 a, Hash128 b)
        {
            var x = a.Value;
            var y = b.Value;
            if (x.x != y.x) return x.x.CompareTo(y.x);
            if (x.y != y.y) return x.y.CompareTo(y.y);
            if (x.z != y.z) return x.z.CompareTo(y.z);
            return x.w.CompareTo(y.w);
        }
    }
}
