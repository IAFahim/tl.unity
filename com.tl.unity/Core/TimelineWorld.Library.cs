using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Tl
{
    public sealed unsafe partial class TimelineWorld : IDisposable
    {
        internal LibraryView _view;
        internal bool _ownsView;
        byte* _base;
        int _byteCount;
        readonly List<nint> _arenas = new();
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
                                if (tl->Stages[s].Steps != null) { NativeMemory.AlignedFree(tl->Stages[s].Steps); break; }
                            NativeMemory.AlignedFree(tl->Stages);
                        }
                        if (tl->Events != null) NativeMemory.AlignedFree(tl->Events);
                        if (tl->EventTickBits != null) NativeMemory.AlignedFree(tl->EventTickBits);
                    }
                    NativeMemory.AlignedFree(_view.Timelines);
                }
                if (_base != null) NativeMemory.AlignedFree(_base);
                foreach (var arena in _arenas) NativeMemory.AlignedFree((void*)arena);
                _arenas.Clear();
                MarkerTableRegistry.Clear(this);
                _ownsView = false;
            }
            _view.Timelines = null;
            _base = null;
            _view.TimelineCount = 0;
            _view.ByteCount = 0;
            _view.Kinds = default;
            _byteCount = 0;
            _generation++;
        }

        unsafe void LoadLibrary(ReadOnlySpan<byte> library)
        {
            if (library.Length < 96) throw new ArgumentException("game.tlib truncated.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(library) != LibraryLayout.LibraryMagic) throw new ArgumentException("game.tlib magic invalid; rebake with current tl.");
            var version = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(4));
            if (version != 1u && version != 2u) throw new ArgumentException("game.tlib version invalid; rebake with current tl.");
            var count = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(8));
            var manifestOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(12));
            var blobOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(16));
            var total = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(20));
            if (total != (uint)library.Length) throw new ArgumentException("game.tlib size mismatch.");
            if (total < 32) throw new ArgumentException("game.tlib truncated.");
            if (manifestOff < 64 || manifestOff % 8 != 0 || blobOff % 8 != 0) throw new ArgumentException("game.tlib offsets must be 8-aligned.");
            if (manifestOff + count * 32u > blobOff || blobOff > total - 32u) throw new ArgumentException("game.tlib sections out of bounds.");
            if (count > 65535) throw new ArgumentException("game.tlib declares more than 65535 timelines.");
            var kindOff = version == 2u ? BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(28)) : 0u;
            var kindLen = version == 2u ? BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(32)) : 0u;
            if (version == 2u && (kindOff % 8 != 0 || kindOff < blobOff || kindLen < 64 || (ulong)kindOff + kindLen > total - 32u)) throw new ArgumentException("game.tlib kind section out of bounds; rebake with current tl.");
            var digestAt = (int)(total - 32u);
#if NET
            Span<byte> calc = stackalloc byte[32];
            if (!SHA256.TryHashData(library.Slice(0, digestAt), calc, out _)) throw new ArgumentException("game.tlib digest failure.");
#else
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(library.Slice(0, digestAt));
            var calc = sha.GetHashAndReset();
#endif
            if (!library.Slice(digestAt).SequenceEqual(calc)) throw new ArgumentException("game.tlib digest mismatch; file is corrupt or truncated.");
            _base = (byte*)NativeMemory.AlignedAlloc((nuint)library.Length, 64);
            _ownsView = true;
            _byteCount = library.Length;
            fixed (byte* src = library) Buffer.MemoryCopy(src, _base, library.Length, library.Length);
            _view.Version = version;
            _view.TimelineCount = (int)count;
            _view.ByteCount = total;
            _view.Kinds = default;
            if (count == 0) { _view.Timelines = null; return; }
            var timelines = (LibraryTimeline*)NativeMemory.AlignedAlloc((nuint)(count * (uint)sizeof(LibraryTimeline)), 64);
            NativeMemory.Clear(timelines, (nuint)(count * (uint)sizeof(LibraryTimeline)));
            _view.Timelines = timelines;
            string? prev = null;
            for (uint i = 0; i < count; i++)
            {
                var at = (int)(manifestOff + i * 32u);
                var id = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at));
                var channel = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 4));
                var nameOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 8));
                var nameLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 12));
                var tlbOff = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 16));
                var tlbLen = BinaryPrimitives.ReadUInt32LittleEndian(library.Slice(at + 20));
                if (id != i + 1) { Free(); throw new ArgumentException($"game.tlib manifest entry {i} id {id} is not dense manifest order (expected {i + 1})."); }
                if (nameOff + nameLen > total - 32u) { Free(); throw new ArgumentException($"game.tlib manifest entry {i} name out of bounds."); }
                string name;
                try { name = System.Text.Encoding.UTF8.GetString(_base + nameOff, (int)nameLen); }
                catch { Free(); throw new ArgumentException($"game.tlib manifest entry {i} name is not valid UTF-8."); }
                if (prev != null && string.CompareOrdinal(prev, name) >= 0) { Free(); throw new ArgumentException($"game.tlib manifest names must sort ordinally (entry {i} '{name}' breaks order)."); }
                prev = name;
                if (tlbOff % 8 != 0 || (ulong)tlbOff + tlbLen > blobOff + (total - 32u - blobOff) || tlbOff < blobOff || tlbOff + tlbLen > total - 32u) { Free(); throw new ArgumentException($"game.tlib manifest entry {i} '{name}' blob out of bounds."); }
                if (channel > 65535) { Free(); throw new ArgumentException($"game.tlib manifest entry {i} channel out of range."); }
                var blob = new ReadOnlySpan<byte>(_base + tlbOff, (int)tlbLen);
                try { TimelineRef.Validate(blob); }
                catch (ArgumentException ex) { Free(); throw new ArgumentException($"game.tlib timeline '{name}': {ex.Message}"); }
                BuildTimeline(timelines + i, (ushort)id, blob, tlbOff, name, i, channel);
            }
            if (version == 2u)
            {
                try { _view.Kinds = KindSectionReader.Create(_base + kindOff, kindLen, (int)count, timelines); }
                catch (ArgumentException) { Free(); throw; }
            }
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
            if (manifestChannel != channel) { Free(); throw new ArgumentException($"game.tlib manifest entry '{name}' channel {manifestChannel} does not match timeline channel {channel}."); }
            tl->Id = id;
            tl->Channel = (ushort)channel;
            tl->Next = (ushort)next;
            if (next > (uint)_view.TimelineCount) { Free(); throw new ArgumentException("game.tlib timeline has unknown next id; file is corrupt or truncated."); }
            tl->Loop = (byte)(loops != 0 ? 1 : 0);
            tl->Hold = (byte)((flags & 1u) != 0 ? 1 : 0);
            tl->Duration = duration;
            tl->StageCount = (int)stageCount;
            tl->EventCount = 0;
            tl->Events = null;
            tl->EventTickBits = null;
            tl->Stages = null;
            if (stageCount == 0) return;
            var stages = (LibraryStage*)NativeMemory.AlignedAlloc((nuint)(stageCount * (uint)sizeof(LibraryStage)), 64);
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
            var flat = totalSteps > 0 ? (LibraryStep*)NativeMemory.AlignedAlloc((nuint)(totalSteps * (uint)sizeof(LibraryStep)), 64) : null;
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
            try { MaterializeSteps(tl, tlbBase, pairs, pairCount, flat, totalSteps, duration, markerOff, markerCount); }
            catch { Free(); throw; }
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
                if (dst->TrackValueIndex >= pe->TrackPoolCount || dst->FirstValueIndex >= pe->ClipPoolCount) throw new ArgumentException("game.tlib step value index out of pool bounds; file is corrupt or truncated.");
                if (dst->FactorSpan != 0 && (dst->SecondValueIndex == 0xFFFFu || dst->SecondValueIndex >= pe->ClipPoolCount)) throw new ArgumentException("game.tlib step blend index out of pool bounds; file is corrupt or truncated.");
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
            var arena = (byte*)NativeMemory.AlignedAlloc(total, 64);
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
                if (dst->TrackValueIndex >= pe->TrackPoolCount || dst->FirstValueIndex >= pe->ClipPoolCount) throw new ArgumentException("game.tlib step value index out of pool bounds; file is corrupt or truncated.");
                if (dst->FactorSpan != 0 && (dst->SecondValueIndex == 0xFFFFu || dst->SecondValueIndex >= pe->ClipPoolCount)) throw new ArgumentException("game.tlib step blend index out of pool bounds; file is corrupt or truncated.");
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
                    tl->EventTickBits = (ulong*)NativeMemory.AlignedAlloc((nuint)(words * 8u), 64);
                    NativeMemory.Clear(tl->EventTickBits, (nuint)(words * 8u));
                }
                return;
            }
            var tmp = (LibraryEvent*)NativeMemory.AlignedAlloc((nuint)(cap * (uint)sizeof(LibraryEvent)), 64);
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
            var events = (LibraryEvent*)NativeMemory.AlignedAlloc((nuint)(uniq * (uint)sizeof(LibraryEvent)), 64);
            for (var i = 0; i < uniq; i++) events[i] = tmp[i];
            NativeMemory.AlignedFree(tmp);
            tl->Events = events;
            tl->EventCount = uniq;
            var words2 = (int)((tl->Duration + 63u) / 64u);
            var bits = (ulong*)NativeMemory.AlignedAlloc((nuint)(words2 * 8u), 64);
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
            for (var i = 1; i < n; i++)
            {
                var key = a[i];
                var j = i - 1;
                while (j >= 0 && Greater(a[j], key)) { a[j + 1] = a[j]; j--; }
                a[j + 1] = key;
            }
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
