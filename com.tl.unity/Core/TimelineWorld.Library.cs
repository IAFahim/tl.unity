using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Tl
{
    internal enum LibraryError : byte
    {
        None = 0,
        Truncated = 1,
        BadMagic = 2,
        BadVersion = 3,
        SizeMismatch = 4,
        BadOffsets = 5,
        SectionsOutOfBounds = 6,
        TooManyTimelines = 7,
        KindSectionOutOfBounds = 8,
        DigestFailure = 9,
        DigestMismatch = 10,
        ManifestIdNotDense = 11,
        ManifestNameOutOfBounds = 12,
        ManifestNameNotUtf8 = 13,
        ManifestNameOrder = 14,
        ManifestBlobOutOfBounds = 15,
        ManifestChannelOutOfRange = 16,
        TimelineInvalid = 17,
        ChannelMismatch = 18,
        UnknownNext = 19,
        KindSection = 20,
    }

    public sealed unsafe partial class TimelineWorld : IDisposable
    {
        internal LibraryView _view;
        internal bool _ownsView;
        byte* _base;
        int _byteCount;
        readonly List<nint> _arenas = new();
        internal static long NativeBlockCount;
        uint _manifestOff;
        int _manifestCount;

        public ushort Find(string name)
        {
            var lo = 0;
            var hi = _manifestCount - 1;
            while (name != null && lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var at = _base + _manifestOff + mid * 32;
                var found = Encoding.UTF8.GetString(_base + BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(at + 8, 4)), (int)BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(at + 12, 4)));
                var order = string.CompareOrdinal(found, name);
                if (order == 0) return (ushort)(mid + 1);
                if (order < 0) lo = mid + 1;
                else hi = mid - 1;
            }
            return 0;
        }

        struct MarkerTable
        {
            public void* Values;
            public int Stride;
            public int Count;
        }

        MarkerTable* _markers;
        int _markersCapacity;

        internal void RegisterMarkers(ushort timeline, void* values, int stride, int count)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (stride <= 0 || count < 0) throw new ArgumentOutOfRangeException(nameof(stride));
            if (_markers == null || timeline >= _markersCapacity)
            {
                var capacity = _markers == null ? Math.Max(64, (int)timeline + 1) : Math.Max(_markersCapacity * 2, (int)timeline + 1);
                var next = (MarkerTable*)TrackAlloc((nuint)capacity * (nuint)sizeof(MarkerTable));
                NativeMemory.Clear(next, (nuint)capacity * (nuint)sizeof(MarkerTable));
                if (_markers != null)
                {
                    new ReadOnlySpan<MarkerTable>(_markers, _markersCapacity).CopyTo(new Span<MarkerTable>(next, _markersCapacity));
                    TrackFree(_markers);
                }
                _markers = next;
                _markersCapacity = capacity;
            }
            _markers[timeline] = new MarkerTable { Values = values, Stride = stride, Count = count };
        }

        internal bool TryResolveMarker(ushort timeline, uint marker, int size, out byte* value)
        {
            value = null;
            if (_markers == null || timeline >= _markersCapacity) return false;
            var table = _markers[timeline];
            if ((ulong)marker >= (ulong)table.Count) return false;
            if (size > table.Stride) return false;
            value = (byte*)table.Values + (nuint)marker * (nuint)table.Stride;
            return true;
        }

        internal void ClearMarkers()
        {
            if (_markers == null) return;
            TrackFree(_markers);
            _markers = null;
            _markersCapacity = 0;
        }

        static void* TrackAlloc(nuint size)
        {
            var p = NativeMemory.AlignedAlloc(size, 64);
            Interlocked.Increment(ref NativeBlockCount);
            return p;
        }

        static void TrackFree(void* p)
        {
            if (p == null) return;
            NativeMemory.AlignedFree(p);
            Interlocked.Decrement(ref NativeBlockCount);
        }
        public unsafe TimelineWorld(ReadOnlySpan<byte> library)
        {
            LoadLibrary(library);
        }

        public LibraryView View => _view;
        internal void AdoptView(LibraryView view)
        {
            Free();
            _view = view;
            _ownsView = false;
        }

        public unsafe void Reload(ReadOnlySpan<byte> library)
        {
            Free();
            LoadLibrary(library);
            _generation++;
        }

        public unsafe void Dispose()
        {
            Free();
            GC.SuppressFinalize(this);
        }

        ~TimelineWorld() { unsafe { Free(); } }

        unsafe void Free()
        {
            ClearMarkers();
            if (_ownsView)
            {
                if (_view.Timelines != null)
                {
                    for (var i = 0; i < _view.TimelineCount; i++)
                    {
                        var tl = _view.Timelines + i;
                        if (tl->Stages != null)
                        {
                            for (var s = 0; s < tl->StageCount; s++)
                                if (tl->Stages[s].Steps != null) { TrackFree(tl->Stages[s].Steps); break; }
                            TrackFree(tl->Stages);
                        }
                        if (tl->Events != null) TrackFree(tl->Events);
                        if (tl->EventTickBits != null) TrackFree(tl->EventTickBits);
                    }
                    TrackFree(_view.Timelines);
                }
                if (_base != null) TrackFree(_base);
                foreach (var arena in _arenas) TrackFree((void*)arena);
                _arenas.Clear();
                _ownsView = false;
            }
            _view.Timelines = null;
            _base = null;
            _view.TimelineCount = 0;
            _view.ByteCount = 0;
            _view.Kinds = default;
            _byteCount = 0;
            _manifestCount = 0;
            _generation++;
        }

        internal static LibraryError Check(ReadOnlySpan<byte> library, out int offset)
        {
            offset = -1;
            if (library.Length < 96) { offset = 0; return LibraryError.Truncated; }
            if (BinaryPrimitives.ReadUInt32LittleEndian(library) != LibraryLayout.LibraryMagic) { offset = 0; return LibraryError.BadMagic; }
            var version = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(4));
            if (version != 1u && version != 2u) { offset = 4; return LibraryError.BadVersion; }
            var count = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(8));
            var manifestOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(12));
            var blobOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(16));
            var total = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(20));
            if (total != (uint)library.Length) { offset = 20; return LibraryError.SizeMismatch; }
            if (total < 32) { offset = 20; return LibraryError.Truncated; }
            if (manifestOff < 64 || manifestOff % 8 != 0 || blobOff % 8 != 0) { offset = 12; return LibraryError.BadOffsets; }
            if (manifestOff + count * 32u > blobOff || blobOff > total - 32u) { offset = 12; return LibraryError.SectionsOutOfBounds; }
            if (count > 65535) { offset = 8; return LibraryError.TooManyTimelines; }
            var kindOff = version == 2u ? BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(28)) : 0u;
            var kindLen = version == 2u ? BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(32)) : 0u;
            if (version == 2u && (kindOff % 8 != 0 || kindOff < blobOff || kindLen < 64 || (ulong)kindOff + kindLen > total - 32u)) { offset = 28; return LibraryError.KindSectionOutOfBounds; }
            var digestAt = (int)(total - 32u);
#if NET
            Span<byte> calc = stackalloc byte[32];
            if (!SHA256.TryHashData(library.Slice(0, digestAt), calc, out _)) { offset = digestAt; return LibraryError.DigestFailure; }
            var calcSpan = calc;
#else
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(library.Slice(0, digestAt));
            var calcSpan = sha.GetHashAndReset();
#endif
            if (!library.Slice(digestAt).SequenceEqual(calcSpan)) { offset = digestAt; return LibraryError.DigestMismatch; }
            if (count == 0) return LibraryError.None;
            var durations = new LibraryTimeline[count];
            string? prev = null;
            for (uint i = 0; i < count; i++)
            {
                var at = (int)(manifestOff + i * 32u);
                offset = at;
                var id = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at));
                var channel = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 4));
                var nameOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 8));
                var nameLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 12));
                var tlbOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 16));
                var tlbLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 20));
                if (id != i + 1) return LibraryError.ManifestIdNotDense;
                if (nameOff + nameLen > total - 32u) return LibraryError.ManifestNameOutOfBounds;
                string name;
                try { name = Encoding.UTF8.GetString(library.Slice((int)nameOff, (int)nameLen)); }
                catch { return LibraryError.ManifestNameNotUtf8; }
                if (prev != null && string.CompareOrdinal(prev, name) >= 0) return LibraryError.ManifestNameOrder;
                prev = name;
                if (tlbOff % 8 != 0 || (ulong)tlbOff + tlbLen > blobOff + (total - 32u - blobOff) || tlbOff < blobOff || tlbOff + tlbLen > total - 32u) return LibraryError.ManifestBlobOutOfBounds;
                if (channel > 65535) return LibraryError.ManifestChannelOutOfRange;
                var blob = library.Slice((int)tlbOff, (int)tlbLen);
                try { TimelineRef.Validate(blob); }
                catch { offset = (int)tlbOff; return LibraryError.TimelineInvalid; }
                durations[i].Duration = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12));
                var blobVersion = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4));
                uint blobChannel = 0;
                uint blobNext = 0;
                if (blobVersion == 6)
                {
                    blobNext = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(52));
                    blobChannel = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(56));
                }
                if (channel != blobChannel) { offset = (int)tlbOff; return LibraryError.ChannelMismatch; }
                if (blobNext > count) { offset = (int)tlbOff; return LibraryError.UnknownNext; }
            }
            if (version == 2u)
            {
                try
                {
                    fixed (byte* libPtr = library)
                    {
                        fixed (LibraryTimeline* tlsPtr = durations)
                        {
                            KindSectionReader.Create(libPtr + kindOff, kindLen, (int)count, tlsPtr);
                        }
                    }
                }
                catch { offset = (int)kindOff; return LibraryError.KindSection; }
            }
            offset = -1;
            return LibraryError.None;
        }

        static string Describe(LibraryError error, ReadOnlySpan<byte> library, int offset)
        {
            switch (error)
            {
                case LibraryError.Truncated: return "game.tlib truncated.";
                case LibraryError.BadMagic: return "game.tlib magic invalid; rebake with current tl.";
                case LibraryError.BadVersion: return "game.tlib version invalid; rebake with current tl.";
                case LibraryError.SizeMismatch: return "game.tlib size mismatch.";
                case LibraryError.BadOffsets: return "game.tlib offsets must be 8-aligned.";
                case LibraryError.SectionsOutOfBounds: return "game.tlib sections out of bounds.";
                case LibraryError.TooManyTimelines: return "game.tlib declares more than 65535 timelines.";
                case LibraryError.KindSectionOutOfBounds: return "game.tlib kind section out of bounds; rebake with current tl.";
                case LibraryError.DigestFailure: return "game.tlib digest failure.";
                case LibraryError.DigestMismatch: return "game.tlib digest mismatch; file is corrupt or truncated.";
                case LibraryError.UnknownNext: return "game.tlib timeline has unknown next id; file is corrupt or truncated.";
                case LibraryError.ManifestIdNotDense:
                case LibraryError.ManifestNameOutOfBounds:
                case LibraryError.ManifestNameNotUtf8:
                case LibraryError.ManifestNameOrder:
                case LibraryError.ManifestBlobOutOfBounds:
                case LibraryError.ManifestChannelOutOfRange:
                {
                    var manifestOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(12));
                    var i = (uint)((offset - (int)manifestOff) / 32);
                    var at = offset;
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at));
                    var nameOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 8));
                    var nameLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 12));
                    string name = "";
                    if (error != LibraryError.ManifestIdNotDense && error != LibraryError.ManifestNameOutOfBounds && error != LibraryError.ManifestNameNotUtf8)
                    {
                        try { name = Encoding.UTF8.GetString(library.Slice((int)nameOff, (int)nameLen)); } catch { name = ""; }
                    }
                    if (error == LibraryError.ManifestIdNotDense) return $"game.tlib manifest entry {i} id {id} is not dense manifest order (expected {i + 1}).";
                    if (error == LibraryError.ManifestNameOutOfBounds) return $"game.tlib manifest entry {i} name out of bounds.";
                    if (error == LibraryError.ManifestNameNotUtf8) return $"game.tlib manifest entry {i} name is not valid UTF-8.";
                    if (error == LibraryError.ManifestNameOrder) return $"game.tlib manifest names must sort ordinally (entry {i} '{name}' breaks order).";
                    if (error == LibraryError.ManifestBlobOutOfBounds) return $"game.tlib manifest entry {i} '{name}' blob out of bounds.";
                    return $"game.tlib manifest entry {i} channel out of range.";
                }
                case LibraryError.TimelineInvalid:
                {
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(8));
                    var manifestOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(12));
                    string name = "";
                    uint tlbLen = 0;
                    for (uint i = 0; i < count; i++)
                    {
                        var at = (int)(manifestOff + i * 32u);
                        if (BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 16)) == (uint)offset)
                        {
                            var nameOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 8));
                            var nameLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 12));
                            tlbLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 20));
                            try { name = Encoding.UTF8.GetString(library.Slice((int)nameOff, (int)nameLen)); } catch { name = ""; }
                            break;
                        }
                    }
                    var blob = library.Slice(offset, (int)tlbLen);
                    string inner = "";
                    try { TimelineRef.Validate(blob); } catch (ArgumentException ex) { inner = ex.Message; }
                    return $"game.tlib timeline '{name}': {inner}";
                }
                case LibraryError.ChannelMismatch:
                {
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(8));
                    var manifestOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(12));
                    string name = "";
                    uint manifestChannel = 0;
                    uint channel = 0;
                    for (uint i = 0; i < count; i++)
                    {
                        var at = (int)(manifestOff + i * 32u);
                        if (BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 16)) != (uint)offset) continue;
                        var mc = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 4));
                        var tlbLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 20));
                        var blob = library.Slice(offset, (int)tlbLen);
                        var bv = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4));
                        uint bc = 0;
                        if (bv == 6) bc = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(56));
                        if (mc == bc) continue;
                        manifestChannel = mc;
                        channel = bc;
                        var nameOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 8));
                        var nameLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 12));
                        try { name = Encoding.UTF8.GetString(library.Slice((int)nameOff, (int)nameLen)); } catch { name = ""; }
                        break;
                    }
                    return $"game.tlib manifest entry '{name}' channel {manifestChannel} does not match timeline channel {channel}.";
                }
                case LibraryError.KindSection:
                {
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(8));
                    var manifestOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(12));
                    var kindOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(28));
                    var kindLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(32));
                    var durations = new LibraryTimeline[count];
                    for (uint i = 0; i < count; i++)
                    {
                        var at = (int)(manifestOff + i * 32u);
                        var tlbOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 16));
                        var tlbLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 20));
                        var blob = library.Slice((int)tlbOff, (int)tlbLen);
                        durations[i].Duration = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12));
                    }
                    try
                    {
                        fixed (byte* libPtr = library)
                        {
                            fixed (LibraryTimeline* tlsPtr = durations)
                            {
                                KindSectionReader.Create(libPtr + kindOff, kindLen, (int)count, tlsPtr);
                            }
                        }
                    }
                    catch (ArgumentException ex) { return ex.Message; }
                    return "game.tlib kind section invalid; rebake with current tl.";
                }
                default: return "game.tlib invalid.";
            }
        }

        unsafe void LoadLibrary(ReadOnlySpan<byte> library)
        {
            var error = Check(library, out var offset);
            if (error != LibraryError.None) throw new ArgumentException(Describe(error, library, offset));
            var version = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(4));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(8));
            var manifestOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(12));
            var total = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(20));
            var kindOff = version == 2u ? BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(28)) : 0u;
            var kindLen = version == 2u ? BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(32)) : 0u;
            _base = (byte*)TrackAlloc((nuint)library.Length);
            _ownsView = true;
            _byteCount = library.Length;
            fixed (byte* src = library) Buffer.MemoryCopy(src, _base, library.Length, library.Length);
            _view.Version = version;
            _view.TimelineCount = (int)count;
            _view.ByteCount = total;
            _view.Kinds = default;
            _manifestOff = manifestOff;
            _manifestCount = (int)count;
            if (count == 0) { _view.Timelines = null; return; }
            var timelines = (LibraryTimeline*)TrackAlloc((nuint)(count * (uint)sizeof(LibraryTimeline)));
            NativeMemory.Clear(timelines, (nuint)(count * (uint)sizeof(LibraryTimeline)));
            _view.Timelines = timelines;
            for (uint i = 0; i < count; i++)
            {
                var at = (int)(manifestOff + i * 32u);
                var id = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at));
                var channel = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 4));
                var nameOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 8));
                var nameLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 12));
                var tlbOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 16));
                var tlbLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 20));
                var name = Encoding.UTF8.GetString(_base + nameOff, (int)nameLen);
                var blob = new ReadOnlySpan<byte>(_base + tlbOff, (int)tlbLen);
                BuildTimeline(timelines + i, (ushort)id, blob, tlbOff, name, i, channel);
            }
            if (version == 2u) _view.Kinds = KindSectionReader.CreateView(_base + kindOff, kindLen, (int)count, timelines);
        }

        unsafe void BuildTimeline(LibraryTimeline* tl, ushort id, ReadOnlySpan<byte> blob, uint tlbOff, string name, uint index, uint manifestChannel)
        {
            var version = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4));
            var loops = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(8));
            var duration = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12));
            var trackCount = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16));
            var stageCount = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(20));
            var pairCount = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(24));
            var pairOff = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(28));
            var stageOff = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(32));
            var frameOff = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(40));
            uint next = 0, channel = 0, flags = 0, markerOff = 0, markerCount = 0;
            if (version == 6)
            {
                next = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(52));
                channel = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(56));
                flags = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(60));
                markerOff = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(64));
                markerCount = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(68));
            }
            _ = manifestChannel;
            tl->Id = id;
            tl->Channel = (ushort)channel;
            tl->Next = (ushort)next;
            _ = next;
            tl->Loop = (byte)(loops != 0 ? 1 : 0);
            tl->Hold = (byte)((flags & 1u) != 0 ? 1 : 0);
            tl->Duration = duration;
            tl->StageCount = (int)stageCount;
            tl->EventCount = 0;
            tl->Events = null;
            tl->EventTickBits = null;
            tl->Stages = null;
            if (stageCount == 0) return;
            var stages = (LibraryStage*)TrackAlloc((nuint)(stageCount * (uint)sizeof(LibraryStage)));
            NativeMemory.Clear(stages, (nuint)(stageCount * (uint)sizeof(LibraryStage)));
            tl->Stages = stages;
            var tlbBase = _base + tlbOff;
            var pairs = (NativePair*)(tlbBase + pairOff);
            ulong* keys = stackalloc ulong[(int)Math.Max(1u, pairCount)];
            for (var i = 0; i < (int)pairCount; i++) keys[i] = pairs[i].Key;
            var totalSteps = 0;
            for (uint s = 0; s < stageCount; s++)
            {
                var at = tlbBase + stageOff + s * 16u;
                var start = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(at, 4));
                var end = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(at + 4, 4));
                var prog = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(at + 8, 4));
                var progCount = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(at + 12, 4));
                stages[s].Start = start;
                stages[s].End = end;
                stages[s].StepCount = (int)progCount;
                totalSteps += (int)progCount;
            }
            var flat = totalSteps > 0 ? (LibraryStep*)TrackAlloc((nuint)(totalSteps * (uint)sizeof(LibraryStep))) : null;
            if (flat != null) NativeMemory.Clear(flat, (nuint)(totalSteps * (uint)sizeof(LibraryStep)));
            var cursor = 0;
            for (uint s = 0; s < stageCount; s++)
            {
                var at = tlbBase + stageOff + s * 16u;
                var prog = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(at + 8, 4));
                var progCount = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(at + 12, 4));
                stages[s].Steps = progCount > 0 ? flat + cursor : null;
                for (uint k = 0; k < progCount; k++)
                {
                    var stepAt = tlbBase + prog + k * 8u;
                    var slot = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(stepAt, 4));
                    var pair = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(stepAt + 4, 4));
                    var row = (SlotRow*)(tlbBase + slot);
                    var dst = flat + cursor;
                    dst->Pair = pair;
                    dst->PairKey = keys[pair];
                    dst->TrackIndex = row->TrackIndex;
                    dst->WindowStart = row->WindowStart;
                    dst->WindowEnd = row->WindowEnd;
                    dst->FactorStart = row->FactorStart;
                    dst->FactorSpan = row->FactorSpan;
                    dst->TrackValueIndex = row->TrackValueIndex;
                    dst->FirstValueIndex = row->FirstValueIndex;
                    dst->SecondValueIndex = row->SecondValueIndex;
                    dst->FoldLane = null;
                    dst->FrameRow = row;
                    cursor++;
                }
            }
            BuildEvents(tl, tlbBase, markerOff, markerCount, totalSteps, flat);
            MaterializeSteps(tl, tlbBase, pairs, pairCount, flat, totalSteps, duration, markerOff, markerCount);
            _ = trackCount;
            _ = frameOff;
        }
        static nuint Align16(nuint v) => (v + 15u) & ~(nuint)15u;

        unsafe void MaterializeSteps(LibraryTimeline* tl, byte* tlbBase, NativePair* pairs, uint pairCount, LibraryStep* flat, int totalSteps, uint duration, uint markerOff, uint markerCount)
        {
            var registry = _systems ??= new SystemRegistry();
            registry.RefreshModules();
            var total = (nuint)0;
            var strideMax = 0u;
            for (var i = 0; i < totalSteps; i++)
            {
                var dst = flat + i;
                if (dst->Pair >= pairCount) continue;
                var pe = pairs + dst->Pair;
                var second = dst->FactorSpan != 0 ? pe->ClipValueBytes : 0u;
                total += Align16((nuint)(pe->TrackValueBytes + pe->ClipValueBytes + second));
                var window = dst->WindowEnd > dst->WindowStart ? dst->WindowEnd - dst->WindowStart : 0u;
                if (window > 0 && registry.TryGet(dst->PairKey, out var entry))
                {
                    var laneBytes = entry.Module.FoldLaneBytes(entry.Index);
                    if (laneBytes > 0) total += Align16((nuint)window * 2u * (uint)laneBytes);
                }
            }
            var stride = 0u;
            if (markerCount > 0)
            {
                var mp = tlbBase + markerOff;
                var off = 0u;
                for (uint m = 0; m < markerCount; m++)
                {
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(mp + off + 4, 4));
                    if (size > strideMax) strideMax = size;
                    off += 16u + ((size + 3u) & ~3u);
                }
                stride = (strideMax + 7u) & ~7u;
                if (stride == 0) stride = 8u;
                total += Align16((nuint)markerCount * stride);
            }
            if (total == 0) return;
            var arena = (byte*)TrackAlloc(total);
            NativeMemory.Clear(arena, total);
            _arenas.Add((nint)arena);
            var cursor = arena;
            for (var i = 0; i < totalSteps; i++)
            {
                var dst = flat + i;
                dst->FrameRow = null;
                dst->FoldLane = null;
                if (dst->Pair >= pairCount) continue;
                var pe = pairs + dst->Pair;
                var trackLen = pe->TrackValueBytes;
                var firstLen = pe->ClipValueBytes;
                var secondLen = dst->FactorSpan != 0 ? pe->ClipValueBytes : 0u;
                var trackSrc = (byte*)pe + pe->TrackPoolOffset + (nuint)dst->TrackValueIndex * trackLen;
                var firstSrc = (byte*)pe + pe->ClipPoolOffset + (nuint)dst->FirstValueIndex * firstLen;
                new ReadOnlySpan<byte>(trackSrc, (int)trackLen).CopyTo(new Span<byte>(cursor, (int)trackLen));
                new ReadOnlySpan<byte>(firstSrc, (int)firstLen).CopyTo(new Span<byte>(cursor + trackLen, (int)firstLen));
                var rowBase = cursor;
                var secondPtr = rowBase + trackLen;
                if (secondLen != 0)
                {
                    var secondSrc = (byte*)pe + pe->ClipPoolOffset + (nuint)dst->SecondValueIndex * firstLen;
                    new ReadOnlySpan<byte>(secondSrc, (int)secondLen).CopyTo(new Span<byte>(rowBase + trackLen + firstLen, (int)secondLen));
                    secondPtr = rowBase + trackLen + firstLen;
                }
                dst->FrameRow = rowBase;
                cursor += Align16((nuint)(trackLen + firstLen + secondLen));
                var window = dst->WindowEnd > dst->WindowStart ? dst->WindowEnd - dst->WindowStart : 0u;
                if (window == 0 || !registry.TryGet(dst->PairKey, out var entry)) continue;
                var laneBytes = entry.Module.FoldLaneBytes(entry.Index);
                if (laneBytes <= 0) continue;
                dst->FoldLane = cursor;
                for (uint w = 0; w < window; w++)
                {
                    var tick = dst->WindowStart + w;
                    float blend = dst->FactorSpan == 0 ? 0f : dst->FactorSpan <= 1 ? 0.5f : unchecked(tick - dst->FactorStart) / (float)unchecked(dst->FactorSpan - 1);
                    var at = cursor + (nuint)w * 2u * (uint)laneBytes;
                    entry.Module.FoldStep(entry.Index, rowBase, rowBase + trackLen, secondPtr, blend, (ushort)tick, (ushort)window, (ushort)w, (ushort)dst->TrackIndex, (ushort)duration, 1, at);
                    entry.Module.FoldStep(entry.Index, rowBase, rowBase + trackLen, secondPtr, blend, (ushort)tick, (ushort)window, (ushort)w, (ushort)dst->TrackIndex, (ushort)duration, -1, at + (uint)laneBytes);
                }
                cursor += Align16((nuint)window * 2u * (uint)laneBytes);
            }
            if (markerCount > 0)
            {
                var table = cursor;
                var mp = tlbBase + markerOff;
                var off = 0u;
                for (uint m = 0; m < markerCount; m++)
                {
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(mp + off + 4, 4));
                    new ReadOnlySpan<byte>(mp + off + 16, (int)size).CopyTo(new Span<byte>(table + m * stride, (int)size));
                    off += 16u + ((size + 3u) & ~3u);
                }
                MarkerTableRegistry.Register(this, tl->Id, table, (int)stride, (int)markerCount);
            }
        }

        unsafe void BuildEvents(LibraryTimeline* tl, byte* tlbBase, uint markerOff, uint markerCount, int totalSteps, LibraryStep* flat)
        {
            var cap = totalSteps * 2 + (int)markerCount;
            if (cap == 0 || tl->Duration == 0)
            {
                if (tl->Duration > 0)
                {
                    var words = (int)((tl->Duration + 63u) / 64u);
                    tl->EventTickBits = (ulong*)TrackAlloc((nuint)(words * 8u));
                    NativeMemory.Clear(tl->EventTickBits, (nuint)(words * 8u));
                }
                return;
            }
            var tmp = (LibraryEvent*)TrackAlloc((nuint)(cap * (uint)sizeof(LibraryEvent)));
            var n = 0;
            var flatIdx = 0;
            for (var s = 0; s < tl->StageCount; s++)
            {
                var st = tl->Stages + s;
                for (var k = 0; k < st->StepCount; k++, flatIdx++)
                {
                    var step = flat + flatIdx;
                    tmp[n++] = new LibraryEvent { Tick = step->WindowStart, Kind = 0, StepIndex = flatIdx, Marker = uint.MaxValue };
                    var endTick = step->WindowEnd > 0 ? step->WindowEnd - 1u : 0u;
                    if (endTick != step->WindowStart) tmp[n++] = new LibraryEvent { Tick = endTick, Kind = 1, StepIndex = flatIdx, Marker = uint.MaxValue };
                }
            }
            var mp = tlbBase + markerOff;
            var off = 0u;
            for (uint m = 0; m < markerCount; m++)
            {
                if (mp == tlbBase) break;
                var tick = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(mp + off, 4));
                tmp[n++] = new LibraryEvent { Tick = tick, Kind = 2, StepIndex = -1, Marker = m };
                var size = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(mp + off + 4, 4));
                off += 16u + ((size + 3u) & ~3u);
            }
            SortEvents(tmp, n);
            var uniq = 0;
            for (var i = 0; i < n; i++)
            {
                if (i > 0 && tmp[i].Tick == tmp[i - 1].Tick && tmp[i].Kind == tmp[i - 1].Kind && tmp[i].StepIndex == tmp[i - 1].StepIndex && tmp[i].Marker == tmp[i - 1].Marker) continue;
                tmp[uniq++] = tmp[i];
            }
            var events = (LibraryEvent*)TrackAlloc((nuint)(uniq * (uint)sizeof(LibraryEvent)));
            for (var i = 0; i < uniq; i++) events[i] = tmp[i];
            TrackFree(tmp);
            tl->Events = events;
            tl->EventCount = uniq;
            var words2 = (int)((tl->Duration + 63u) / 64u);
            var bits = (ulong*)TrackAlloc((nuint)(words2 * 8u));
            NativeMemory.Clear(bits, (nuint)(words2 * 8u));
            for (var i = 0; i < uniq; i++)
            {
                var t = events[i].Tick;
                if (t < tl->Duration) bits[t >> 6] |= 1UL << (int)(t & 63);
            }
            tl->EventTickBits = bits;
        }

        static unsafe void SortEvents(LibraryEvent* a, int n)
        {
            if (n <= 1) return;
            var sorted = true;
            for (var i = 1; i < n; i++)
            {
                if (Greater(a[i - 1], a[i])) { sorted = false; break; }
            }
            if (sorted) return;
            var scratch = (LibraryEvent*)NativeMemory.AlignedAlloc((nuint)n * (nuint)sizeof(LibraryEvent), 64);
            try
            {
                var target = new Span<LibraryEvent>(a, n);
                var source = target;
                var destination = new Span<LibraryEvent>(scratch, n);
                var width = 1;
                while (width < n)
                {
                    for (var left = 0; left < n; left += width * 2)
                    {
                        var mid = Math.Min(left + width, n);
                        var right = Math.Min(left + width * 2, n);
                        MergeRun(source.Slice(left, mid - left), source.Slice(mid, right - mid), destination.Slice(left, right - left));
                    }
                    var tmp = source;
                    source = destination;
                    destination = tmp;
                    width <<= 1;
                }
                if (!source.Overlaps(target)) source.CopyTo(target);
            }
            finally
            {
                NativeMemory.AlignedFree(scratch);
            }
        }

        static void MergeRun(Span<LibraryEvent> leftRun, Span<LibraryEvent> rightRun, Span<LibraryEvent> merged)
        {
            var i = 0;
            var j = 0;
            var k = 0;
            while (i < leftRun.Length && j < rightRun.Length)
            {
                if (Greater(leftRun[i], rightRun[j])) merged[k++] = rightRun[j++];
                else merged[k++] = leftRun[i++];
            }
            while (i < leftRun.Length) merged[k++] = leftRun[i++];
            while (j < rightRun.Length) merged[k++] = rightRun[j++];
        }

        static bool Greater(LibraryEvent a, LibraryEvent b)
        {
            if (a.Tick != b.Tick) return a.Tick > b.Tick;
            if (a.Kind != b.Kind) return a.Kind > b.Kind;
            if (a.StepIndex != b.StepIndex) return a.StepIndex > b.StepIndex;
            return a.Marker > b.Marker;
        }
    }
}
