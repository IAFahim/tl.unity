using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Tl;

static unsafe class TimelineTable
{
    const int MaxCapacity = 65536;
    const long GraveyardGraceLoads = 4096;
    const int StateEmpty = 0;
    const int StateLive = 1;
    const int StateDead = 2;
    const long LowMask = 0xFFFFFFFFL;
    internal const uint DeadMotion = 0x4000_0000u;
    internal static readonly SlotView* RestView = NewRestView();

    static SlotView* NewRestView()
    {
        var view = (SlotView*)NativeMemory.AllocZeroed((nuint)sizeof(SlotView));
        view->AbiVersion = SlotView.AbiVersionV4;
        return view;
    }

    internal static ushort DomainOf(uint motion) => (motion & DeadMotion) != 0 ? ushort.MaxValue : (ushort)(motion & 0xFFFF);

    internal static long MotionEpoch => Volatile.Read(ref _deaths) + Volatile.Read(ref _revivals);

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct Entry
    {
        public ulong Key0;
        public ulong Key1;
        public ulong Key2;
        public ulong Key3;
        public byte* Block;
        public nint Bytes;
        public long Count;
        public int State;
        public ushort Id;
    }

    struct GraveNode
    {
        public nint Next;
        public byte* Block;
        public nint Bytes;
        public long Stamp;
    }

    static readonly byte* _arena;
    static readonly uint* _motion;
    static int _gate;
    static int _used;
    static int _live;
    static int _nextId = 1;
    static long _loads;
    static long _allocBytes;
    static long _freeBytes;
    static long _allocCount;
    static long _freeCount;
    static long _peakLiveBytes;
    static long _graveyardBytes;
    static long _graveyardCount;
    static long _deaths;
    static long _revivals;
    static long _overReleases;
    static long _releases;
    static long _publishes;
    static nint _graveyard;

    static TimelineTable()
    {
        _arena = (byte*)NativeMemory.AlignedAlloc((nuint)MaxCapacity * (nuint)sizeof(Entry) + (nuint)MaxCapacity * sizeof(uint), 64);
        new Span<Entry>(Entries, MaxCapacity).Clear();
        new Span<int>(ById, MaxCapacity).Fill(-1);
        _motion = (uint*)NativeMemory.AlignedAlloc((MaxCapacity * sizeof(uint)), 64);
        new Span<uint>(_motion, MaxCapacity).Clear();
    }

    internal static uint* Motion => _motion;

    static uint MotionOf(byte* block)
        => ((NativeHeader*)block)->Duration | (((NativeHeader*)block)->Loops != 0 ? 0x80000000u : 0u);

    internal static Entry* Entries => (Entry*)_arena;
    internal static int* ById => (int*)(_arena + (nuint)MaxCapacity * (nuint)sizeof(Entry));
    static int Mask => MaxCapacity - 1;

    internal static int Capacity => MaxCapacity;
    internal static int LiveEntries => Volatile.Read(ref _live);
    internal static int DistinctContents => Volatile.Read(ref _nextId) - 1;
    internal static long AllocCount => Volatile.Read(ref _allocCount);
    internal static long FreeCount => Volatile.Read(ref _freeCount);
    internal static long AllocBytes => Volatile.Read(ref _allocBytes);
    internal static long FreeBytes => Volatile.Read(ref _freeBytes);
    internal static long PeakLiveBytes => Volatile.Read(ref _peakLiveBytes);
    internal static long GraveyardBlocks => Volatile.Read(ref _graveyardCount);
    internal static long GraveyardBytes => Volatile.Read(ref _graveyardBytes);
    internal static long Deaths => Volatile.Read(ref _deaths);
    internal static long Revivals => Volatile.Read(ref _revivals);
    internal static long OverReleases => Volatile.Read(ref _overReleases);
    internal static long Releases => Volatile.Read(ref _releases);
    internal static long Publishes => Volatile.Read(ref _publishes);

    internal static ushort Load(ReadOnlySpan<byte> baked)
    {
        Digest(baked, out var k0, out var k1, out var k2, out var k3);
        TimelineRef.Validate(baked);
        AcquireGate();
        try
        {
            _loads++;
            if ((nint)Volatile.Read(ref _graveyard) != 0) DrainGraveyard(GraveyardGraceLoads);
            var entry = Find(k0, k1, k2, k3, baked, out var empty);
            if (entry != null)
            {
                if (Volatile.Read(ref entry->State) == StateLive)
                    return entry->Id;
                return Revive(entry, baked);
            }
            if (_nextId >= MaxCapacity)
                throw new InvalidOperationException($"Timeline index domain exhausted: {MaxCapacity} distinct timeline contents over the process lifetime; indices are content identities and never move to other content.");
            if ((_used + 1) * 4 > MaxCapacity * 3)
                throw new InvalidOperationException($"Intern table exhausted: {_used} distinct timeline contents occupy the {MaxCapacity}-slot table.");
            return Publish(empty, k0, k1, k2, k3, baked);
        }
        finally
        {
            Volatile.Write(ref _gate, 0);
        }
    }

    internal static void LoadPinned(ReadOnlySpan<byte> baked, out ushort index, out long generation)
    {
        while (true)
        {
            index = Load(baked);
            try
            {
                Pin(index, out generation);
                return;
            }
            catch (ArgumentException)
            {
            }
        }
    }

    internal static TimelineRef Reference(ushort index)
    {
        if (TryLive(index, out var block))
            return new TimelineRef(block);
        throw NotLoaded(index);
    }

    internal static bool IsLive(ushort index)
        => TryLive(index, out _);

    internal static void Pin(ushort index, out long generation)
    {
        generation = 0;
        AcquireGate();
        try
        {
            if (TryLive(index, out var _))
            {
                var entry = Entries + Volatile.Read(ref ById[index]);
                generation = (long)((ulong)Interlocked.Increment(ref entry->Count) >> 32);
                return;
            }
        }
        finally
        {
            Volatile.Write(ref _gate, 0);
        }
        throw NotLoaded(index);
    }

    static bool TryLive(ushort index, out byte* block)
    {
        block = null;
        if (index >= (uint)Volatile.Read(ref _nextId)) return false;
        var slot = Volatile.Read(ref ById[index]);
        if (slot < 0) return false;
        var entry = Entries + slot;
        if (Volatile.Read(ref entry->State) != StateLive || entry->Id != index) return false;
        block = entry->Block;
        return block != null;
    }

    internal static ArgumentException NotLoaded(ushort index)
        => new($"Timeline index {index} is not loaded; indices come from TimelineAsset.Load and die when every acquisition is disposed.");

    internal static void Release(ushort index, long generation)
    {
        Interlocked.Increment(ref _releases);
        AcquireGate();
        try
        {
            void Over() => Interlocked.Increment(ref _overReleases);
            if (index >= (uint)Volatile.Read(ref _nextId))
            {
                Over();
                return;
            }
            var slot = Volatile.Read(ref ById[index]);
            if (slot < 0)
            {
                Over();
                return;
            }
            var entry = Entries + slot;
            if (Volatile.Read(ref entry->State) != StateLive || entry->Id != index || (Volatile.Read(ref entry->Count) >> 32) != generation)
            {
                Over();
                return;
            }
            var after = Interlocked.Decrement(ref entry->Count);
            if (after < 0)
            {
                Over();
                Interlocked.Increment(ref entry->Count);
                return;
            }
            if ((after & LowMask) != 0) return;
            Volatile.Write(ref entry->State, StateDead);
            if ((Volatile.Read(ref entry->Count) & LowMask) != 0)
            {
                Volatile.Write(ref entry->State, StateLive);
                return;
            }
            Volatile.Write(ref _motion[index], DeadMotion);
            var block = entry->Block;
            var bytes = entry->Bytes;
            entry->Block = null;
            entry->Bytes = 0;
            if (block != null)
            {
                Interlocked.Increment(ref _deaths);
                Interlocked.Decrement(ref _live);
                PushGraveyard(block, bytes);
            }
        }
        finally
        {
            Volatile.Write(ref _gate, 0);
        }
    }

    internal static void Drain()
    {
        AcquireGate();
        try
        {
            DrainGraveyard(0);
        }
        finally
        {
            Volatile.Write(ref _gate, 0);
        }
    }

    static ushort Revive(Entry* entry, ReadOnlySpan<byte> baked)
    {
        entry->Block = AllocateBlock(baked);
        entry->Bytes = baked.Length;
        Volatile.Write(ref _motion[entry->Id], MotionOf(entry->Block));
        var generation = (ulong)Volatile.Read(ref entry->Count) >> 32;
        Interlocked.Exchange(ref entry->Count, (long)((generation + 1) << 32));
        Volatile.Write(ref entry->State, StateLive);
        Interlocked.Increment(ref _live);
        Interlocked.Increment(ref _revivals);
        Interlocked.Increment(ref _publishes);
        return entry->Id;
    }

    static ushort Publish(Entry* entry, ulong k0, ulong k1, ulong k2, ulong k3, ReadOnlySpan<byte> baked)
    {
        var id = (ushort)_nextId;
        _nextId++;
        _used++;
        entry->Block = AllocateBlock(baked);
        entry->Bytes = baked.Length;
        entry->Key0 = k0;
        entry->Key1 = k1;
        entry->Key2 = k2;
        entry->Key3 = k3;
        entry->Id = id;
        var generation = (ulong)Volatile.Read(ref entry->Count) >> 32;
        Interlocked.Exchange(ref entry->Count, (long)((generation + 1) << 32));
        Volatile.Write(ref entry->State, StateLive);
        Volatile.Write(ref _motion[id], MotionOf(entry->Block));
        Volatile.Write(ref ById[id], (int)(entry - Entries));
        Interlocked.Increment(ref _live);
        Interlocked.Increment(ref _publishes);
        return id;
    }

    static byte* AllocateBlock(ReadOnlySpan<byte> baked)
    {
        var padded = (baked.Length + 63) & ~63;
        var block = (byte*)NativeMemory.AlignedAlloc((nuint)padded, 64);
        baked.CopyTo(new Span<byte>(block, baked.Length));
        Interlocked.Add(ref _allocBytes, padded);
        Interlocked.Increment(ref _allocCount);
        var live = Volatile.Read(ref _allocBytes) - Volatile.Read(ref _freeBytes) - Volatile.Read(ref _graveyardBytes);
        long peak;
        while (live > (peak = Volatile.Read(ref _peakLiveBytes)))
        {
            Interlocked.CompareExchange(ref _peakLiveBytes, live, peak);
        }
        return block;
    }

    static Entry* Find(ulong k0, ulong k1, ulong k2, ulong k3, ReadOnlySpan<byte> baked, out Entry* empty)
    {
        empty = null;
        var index = (int)(Mix(k0, k1) & (ulong)Mask);
        for (var n = 0; n < MaxCapacity; n++)
        {
            var entry = Entries + ((index + n) & Mask);
            var state = Volatile.Read(ref entry->State);
            if (state == StateEmpty)
            {
                if (empty == null) empty = entry;
                return null;
            }
            if (Volatile.Read(ref entry->Key0) != k0 || Volatile.Read(ref entry->Key1) != k1) continue;
            if (Volatile.Read(ref entry->Key2) != k2 || Volatile.Read(ref entry->Key3) != k3) continue;
            if (state == StateDead) return entry;
            if (entry->Bytes != baked.Length) continue;
            if (!new ReadOnlySpan<byte>(entry->Block, (int)entry->Bytes).SequenceEqual(baked)) continue;
            return entry;
        }
        if (empty == null) throw new InvalidOperationException($"Intern table probe exhausted: no slot remains among {MaxCapacity} entries for content ({k0:X16}, {k1:X16}).");
        return null;
    }

    static void PushGraveyard(byte* block, nint bytes)
    {
        var node = (GraveNode*)NativeMemory.Alloc((nuint)sizeof(GraveNode));
        node->Next = Volatile.Read(ref _graveyard);
        node->Block = block;
        node->Bytes = bytes;
        node->Stamp = Volatile.Read(ref _loads);
        Volatile.Write(ref _graveyard, (nint)node);
        Interlocked.Add(ref _graveyardBytes, bytes);
        Interlocked.Increment(ref _graveyardCount);
    }

    static void DrainGraveyard(long minAge)
    {
        var loads = Volatile.Read(ref _loads);
        nint kept = 0;
        var node = (nint)Volatile.Read(ref _graveyard);
        while (node != 0)
        {
            var current = (GraveNode*)node;
            node = current->Next;
            if (loads - current->Stamp < minAge)
            {
                current->Next = kept;
                kept = (nint)current;
                continue;
            }
            Interlocked.Add(ref _freeBytes, current->Bytes);
            Interlocked.Increment(ref _freeCount);
            Interlocked.Add(ref _graveyardBytes, -current->Bytes);
            Interlocked.Decrement(ref _graveyardCount);
            NativeMemory.AlignedFree(current->Block);
            NativeMemory.Free(current);
        }
        Volatile.Write(ref _graveyard, kept);
    }

    static void AcquireGate()
    {
        var spins = 0;
        while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
        {
            Thread.SpinWait(64);
            if (++spins >= 4096)
            {
                spins = 0;
                Thread.Yield();
            }
        }
    }

    static ulong Mix(ulong low, ulong high)
    {
        var h = low ^ (high * 0x9E3779B97F4A7C15UL) + 0xA0761D6478BD642FUL;
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h;
    }

    static void Digest(ReadOnlySpan<byte> data, out ulong k0, out ulong k1, out ulong k2, out ulong k3)
    {
#if NET
        Span<byte> hash = stackalloc byte[32];
        SHA256.TryHashData(data, hash, out _);
#else
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(data);
        var hash = sha.GetHashAndReset();
#endif
        fixed (byte* p = hash)
        {
            k0 = Unsafe.ReadUnaligned<ulong>(p);
            k1 = Unsafe.ReadUnaligned<ulong>(p + 8);
            k2 = Unsafe.ReadUnaligned<ulong>(p + 16);
            k3 = Unsafe.ReadUnaligned<ulong>(p + 24);
        }
    }
}
