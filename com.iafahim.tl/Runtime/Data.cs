using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tl;

static class Keying
{
	const ulong Seed = 14695981039346656037, Prime = 1099511628211;

	internal static ulong Of(string text) { var hash = Seed; foreach (var c in text) hash = (hash ^ c) * Prime; return hash | 1; }
}

public static class TypeKey<T>
{
	public static readonly ulong Value = Keying.Of(typeof(T).AssemblyQualifiedName!);
}

#pragma warning disable CS0649
struct NativeHeader
{
	public uint Magic, Version, Loops, Duration, TrackCount, StageCount, PairCount, PairOffset, StageOffset, FrameOffset, Reserved, Bytes;
}

struct NativePair { public ulong Key; public uint Stride, Reserved; }
struct NativeStage { public uint Start, End, ProgramOffset, ProgramCount; }
struct NativeStep { public uint Slot, Pair; }

struct FrameSlot<TTrack, TClip> where TTrack : unmanaged, IBlend<TClip> where TClip : unmanaged
{
	public TTrack Track;
	public TClip First, Second;
	public uint WindowStart, WindowEnd, FactorStart, FactorSpan;
	public byte TrackIndex;

	internal static unsafe Frame<TTrack, TClip> ToFrame(FrameSlot<TTrack, TClip>* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, TClip* scratch)
	{
		if (slot->FactorSpan == 0) *scratch = slot->First;
		else
		{
			var factor = slot->FactorSpan <= 1 ? 0.5f : (tick - slot->FactorStart) / (float)(slot->FactorSpan - 1);
			slot->Track.Blend(in slot->First, in slot->Second, factor, out var blended);
			*scratch = blended;
		}
		if (tick == slot->WindowStart) flags |= FrameFlags.ClipStart;
		if (tick == slot->WindowEnd - 1) flags |= FrameFlags.ClipEnd;
		return new Frame<TTrack, TClip>(in slot->Track, in *scratch, gameTick, tick, cycle, slot->TrackIndex, flags);
	}
}
public readonly unsafe struct TimelineRef
{
	internal readonly byte* _p;
	internal TimelineRef(void* p) => _p = (byte*)p;

	NativeHeader* Header => (NativeHeader*)_p;
	internal NativePair* Pairs => (NativePair*)(_p + Header->PairOffset);
	internal nint Address => (nint)_p;
	internal uint PairCount => _p == null ? 0 : Header->PairCount;

	internal static byte* Load(ReadOnlySpan<byte> baked)
	{
		Validate(baked);
		var block = (byte*)NativeMemory.AlignedAlloc((nuint)((baked.Length + 63) & ~63), 64);
		baked.CopyTo(new(block, baked.Length));
		return block;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal bool Advance(bool reverse, uint pos, long cyc, out uint np, out long nc, out uint t, out long fc, out FrameFlags f)
	{
		np = pos; nc = fc = cyc; t = 0; f = FrameFlags.None;
		return _p != null && TimelineMovement.Advance(Header->Duration, Header->Loops != 0, reverse, pos, cyc, out np, out nc, out t, out fc, out f);
	}

	internal bool Select(bool reverse, uint position, long cycle, out TimelineState next, out uint tick, out long fc, out FrameFlags flags)
	{
		var ok = Advance(reverse, position, cycle, out var np, out var nc, out tick, out fc, out flags);
		next = new TimelineState(1, np, nc);
		return ok;
	}
	internal NativeStage* StageOf(uint tick)
	{
		var count = (int)Header->StageCount;
		var stages = (NativeStage*)(_p + Header->StageOffset);
		if (count <= 4)
		{
			for (var i = 0; i < count; i++) if (tick < stages[i].End) return stages + i;
			return null;
		}
		for (int l = 0, h = count - 1; l <= h;)
		{
			var m = (l + h) >> 1;
			if (tick < stages[m].End) { if (m == 0 || tick >= stages[m - 1].End) return stages + m; h = m - 1; }
			else l = m + 1;
		}
		return null;
	}

	internal bool Uses(ulong key)
	{
		var pairs = Pairs;
		for (var i = 0; i < PairCount; i++) if (pairs[i].Key == key) return true;
		return false;
	}

	internal void Resolve(Span<int> chains)
	{
		var pairs = Pairs;
		for (var i = 0; i < PairCount; i++) chains[i] = PairTable.Head(pairs[i].Key);
	}

	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	internal void Execute(bool reverse, uint tick, uint gameTick, long cycle, FrameFlags flags, int row, Span<int> chains, void** columns)
	{
		var stage = Header->StageCount == 1 ? (NativeStage*)(_p + Header->StageOffset) : StageOf(tick);
		if (stage == null) return;
		var steps = (NativeStep*)(_p + stage->ProgramOffset);
		var consumers = PairTable.ConsumerAt;
		var count = (int)stage->ProgramCount;
		var step = reverse ? steps + count - 1 : steps;
		var stride = reverse ? -1 : 1;
		int* rev = stackalloc int[64];
		while (count-- > 0)
		{
			var slot = _p + step->Slot;
			var head = chains[(int)step->Pair];
			if (reverse && head >= 0 && consumers[head].Next >= 0)
			{
				var n = 0;
				for (var e = head; e >= 0; e = consumers[e].Next)
				{
					if (n == 64) throw new InvalidOperationException("Consumer capacity exhausted.");
					rev[n++] = e;
				}
				while (n-- > 0)
				{
					var e = rev[n];
					consumers[e].Execute(slot, gameTick, tick, cycle, flags, columns + consumers[e].Offset, row);
				}
			}
			else
			{
				for (var entry = head; entry >= 0; entry = consumers[entry].Next)
					consumers[entry].Execute(slot, gameTick, tick, cycle, flags, columns + consumers[entry].Offset, row);
			}
			step += stride;
		}
	}

	static void Validate(ReadOnlySpan<byte> baked)
	{
		void Fail(string message) => throw new ArgumentException(message);
		if (baked.Length < 48) Fail("TLB1 truncated.");
		var h = MemoryMarshal.Read<NativeHeader>(baked);
		if (h.Magic != 0x31424C54 || h.Version != 1) Fail("TLB1 magic or version invalid.");
		if (h.Bytes != (uint)baked.Length) Fail("TLB1 size mismatch.");
		if (h.PairOffset < 48 || (h.PairOffset | h.StageOffset | h.FrameOffset) % 8 != 0) Fail("TLB1 offsets must be 8-aligned.");
		if ((ulong)h.PairOffset + 16ul * h.PairCount > h.StageOffset || (ulong)h.StageOffset + 16ul * h.StageCount > h.FrameOffset || h.FrameOffset > (ulong)baked.Length) Fail("TLB1 sections out of bounds.");
		var pairs = MemoryMarshal.Cast<byte, NativePair>(baked.Slice((int)h.PairOffset, 16 * (int)h.PairCount));
		for (var i = 1; i < pairs.Length; i++) if (pairs[i - 1].Key >= pairs[i].Key) Fail("TLB1 pair keys must be sorted.");
		var stages = MemoryMarshal.Cast<byte, NativeStage>(baked.Slice((int)h.StageOffset, 16 * (int)h.StageCount));
		var programs = (ulong)h.StageOffset + 16ul * h.StageCount;
		uint edge = 0;
		for (var i = 0; i < stages.Length; i++)
		{
			var stage = stages[i];
			if (stage.Start != edge || stage.ProgramOffset < programs || stage.ProgramOffset % 8 != 0 || (ulong)stage.ProgramOffset + 8ul * stage.ProgramCount > h.FrameOffset) Fail("TLB1 stages must be monotonic.");
			var steps = MemoryMarshal.Cast<byte, NativeStep>(baked.Slice((int)stage.ProgramOffset, 8 * (int)stage.ProgramCount));
			for (var j = 0; j < steps.Length; j++)
			{
				var step = steps[j];
				if (step.Pair >= h.PairCount) Fail("TLB1 step pair out of bounds.");
				if (step.Slot < h.FrameOffset || step.Slot % 16 != 0 || (ulong)step.Slot + pairs[(int)step.Pair].Stride > (ulong)baked.Length) Fail("TLB1 slots must be 16-aligned in bounds.");
			}
			edge = stage.End;
		}
		if (edge != h.Duration) Fail("TLB1 stages must cover duration.");
	}
}

public sealed unsafe class TimelineAsset : IDisposable
{
	nint _p;
	TimelineAsset(nint p) => _p = p;

	public static TimelineAsset Load(ReadOnlySpan<byte> baked) => new((nint)TimelineRef.Load(baked));

	public TimelineRef Reference => new((void*)_p);

	public void Dispose() { var p = Interlocked.Exchange(ref _p, 0); if (p != 0) NativeMemory.AlignedFree((void*)p); }
}

public struct TimelineComponent(TimelineRef reference)
{
	public TimelineRef Reference = reference;
	public uint Position;
	public long Cycle;
}

public readonly unsafe struct TickFrame
{
	public readonly void* Slot;
	public readonly uint GameTick, TimelineTick;
	public readonly long Cycle;
	public readonly FrameFlags Flags;

	internal TickFrame(void* slot, uint gameTick, uint timelineTick, long cycle, FrameFlags flags)
	{
		Slot = slot;
		GameTick = gameTick;
		TimelineTick = timelineTick;
		Cycle = cycle;
		Flags = flags;
	}

	public Frame<TTrack, TClip> ToFrame<TTrack, TClip>(ref TClip scratch) where TTrack : unmanaged, IBlend<TClip> where TClip : unmanaged
		=> FrameSlot<TTrack, TClip>.ToFrame((FrameSlot<TTrack, TClip>*)Slot, GameTick, TimelineTick, Cycle, Flags, (TClip*)Unsafe.AsPointer(ref scratch));

	public static Frame<TTrack, TClip> ToFrame<TTrack, TClip>(void* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, ref TClip scratch) where TTrack : unmanaged, IBlend<TClip> where TClip : unmanaged
		=> FrameSlot<TTrack, TClip>.ToFrame((FrameSlot<TTrack, TClip>*)slot, gameTick, tick, cycle, flags, (TClip*)Unsafe.AsPointer(ref scratch));
}

static unsafe class PairTable
{
	internal struct Consumer { public int Next, Pair, Offset; public delegate*<byte*, uint, uint, long, FrameFlags, void**, int, void> Execute; public delegate*<ulong*, int, byte*, void> Bind; }
	struct Slot { public ulong Key; public int Head; }

	const int SlotCount = 1024, PairCapacity = 512, ConsumerCapacity = 1024, MaxPointers = 256;
	static readonly byte* _block = (byte*)NativeMemory.AlignedAlloc((nuint)(16 * SlotCount + 32 * ConsumerCapacity), 64);
	static volatile int _pairs, _consumers, _gate;

	static PairTable() => Unsafe.InitBlock(_block, 0, 16 * SlotCount);

	static Slot* SlotAt => (Slot*)_block;
	internal static Consumer* ConsumerAt => (Consumer*)(_block + 16 * SlotCount);

	internal static void Install(ulong key, delegate*<byte*, uint, uint, long, FrameFlags, void**, int, void> e, delegate*<ulong*, int, byte*, void> b)
	{
		while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0) Thread.Yield();
		try
		{
			var slot = Probe(key);
			var slots = SlotAt;
			if (slots[slot].Key == 0)
			{
				if (_pairs == PairCapacity) throw new InvalidOperationException("Pair capacity exhausted.");
				slots[slot].Head = -1;
				Volatile.Write(ref slots[slot].Key, key);
				_pairs++;
			}
			if (_consumers == ConsumerCapacity || _consumers * 4 + 4 > MaxPointers) throw new InvalidOperationException("Consumer capacity exhausted.");
			var consumers = ConsumerAt;
			consumers[_consumers] = new Consumer { Next = slots[slot].Head, Pair = slot, Execute = e, Bind = b, Offset = _consumers * 4 };
			Volatile.Write(ref slots[slot].Head, _consumers);
			_consumers++;
		}
		finally
		{
			_gate = 0;
		}
	}

	static int Probe(ulong key)
	{
		var slots = SlotAt;
		var slot = (int)key & (SlotCount - 1);
		while (true)
		{
			var candidate = Volatile.Read(ref slots[slot].Key);
			if (candidate == 0 || candidate == key) return slot;
			slot = (slot + 1) & (SlotCount - 1);
		}
	}

	internal static int Head(ulong key)
	{
		var slots = SlotAt;
		var slot = Probe(key);
		return slots[slot].Key == 0 ? -1 : Volatile.Read(ref slots[slot].Head);
	}

	internal static void Bind(TimelineRef asset, ulong* keys, int keyCount, byte* indices, byte* rSlots, byte* rCols, ref int rCount, ref ulong boundMask)
	{
		var slots = SlotAt;
		var consumers = ConsumerAt;
		for (var entry = 0; entry < _consumers; entry++)
			if (asset.Uses(slots[consumers[entry].Pair].Key) && (boundMask & (1ul << entry)) == 0)
			{
				boundMask |= 1ul << entry;
				var offset = consumers[entry].Offset;
				consumers[entry].Bind(keys, keyCount, indices + offset);
				for (var k = 0; k < 4; k++)
				{
					var col = indices[offset + k];
					if (col != 0) { rSlots[rCount] = (byte)(offset + k); rCols[rCount++] = (byte)(col - 1); }
				}
			}
	}
}

public static unsafe class PairRuntime<TTrack, TClip> where TTrack : unmanaged, IBlend<TClip> where TClip : unmanaged
{
	public static readonly ulong Key = Keying.Of(typeof(TTrack).AssemblyQualifiedName! + "\0" + typeof(TClip).AssemblyQualifiedName!);

	public static void Consume(delegate*<byte*, uint, uint, long, FrameFlags, void**, int, void> execute, delegate*<ulong*, int, byte*, void> bind) => PairTable.Install(Key, execute, bind);

	public static void ConsumeUnmanaged(delegate* unmanaged<byte*, uint, uint, long, FrameFlags, void**, int, void> execute, delegate* unmanaged<ulong*, int, byte*, void> bind) => UnmanagedTable.Install(Key, execute, bind);
}

public static class Timeline
{
	public static TimelineQuery Rows(TimelineComponent[] timelines) => new(timelines);

	public static FrameQuery<TTrack, TClip> Query<TTrack, TClip>(in TimelineComponent component) where TTrack : unmanaged, IBlend<TClip> where TClip : unmanaged => new(component);

	public static unsafe void TickUnmanaged(UnmanagedTickState* state, TimelineComponent* rows, int rowCount, void** bases, ulong* keys, int keyCount, uint gameTick, int delta)
		=> UnmanagedTick.TickCoreUnmanaged(state, UnmanagedTable.Block, bases, rows, rowCount, gameTick, delta, keys, keyCount);
}

public unsafe ref struct TimelineQuery
{
	struct PairCache { public nint Asset; public int Count, RefreshCount; public ulong BoundMask; public unsafe fixed int Heads[16]; public unsafe fixed ulong Columns[256]; public unsafe fixed byte ColumnIndex[256], RefreshSlot[256], RefreshCol[256]; public byte Identity; }

	internal static unsafe delegate*<byte*, uint, void*> Find;

	internal Span<TimelineComponent> _rows;
	PairCache _cache;
	bool _bound;
	delegate*<byte*, int*, void**, TimelineComponent*, int, uint, int, void> _kernel;

	internal unsafe TimelineQuery(Span<TimelineComponent> rows)
	{
		_rows = rows;
		new Span<byte>(Unsafe.AsPointer(ref _cache.ColumnIndex[0]), 256).Clear();
	}

	internal static unsafe void* P<T>(ReadOnlySpan<T> s) => Unsafe.AsPointer(ref MemoryMarshal.GetReference(s));

	internal static void Ck<T>(ReadOnlySpan<T> c, Span<TimelineComponent> r) where T : unmanaged
	{
		if (c.Length != r.Length) throw new ArgumentException("Column length must equal row count.");
		if (MemoryMarshal.AsBytes(c).Overlaps(MemoryMarshal.AsBytes(r))) throw new ArgumentException("Columns must not overlap the rows.");
	}

	internal static void CkP<T1, T2>(ReadOnlySpan<T1> a, bool wa, ReadOnlySpan<T2> b, bool wb) where T1 : unmanaged where T2 : unmanaged
	{
		if (TypeKey<T1>.Value == TypeKey<T2>.Value) throw new ArgumentException("Duplicate column type is role-ambiguous.");
		if ((wa || wb) && MemoryMarshal.AsBytes(a).Overlaps(MemoryMarshal.AsBytes(b))) throw new ArgumentException("Writable columns must not overlap.");
	}

	public TimelineQuery<T> Read<T>(ReadOnlySpan<T> c) where T : unmanaged { Ck(c, _rows); return new(this, c, false); }
	public TimelineQuery<T> Write<T>(Span<T> c) where T : unmanaged { Ck(c, _rows); return new(this, c, true); }

	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	public unsafe void Tick(uint gameTick, int delta = 1) => TickCore(null, null, gameTick, delta);

	unsafe Span<int> Resolved => new(Unsafe.AsPointer(ref _cache.Heads[0]), _cache.Count);

	unsafe byte ComputeIdentity(byte* rS, byte* rC)
	{
		var k = (byte)_cache.RefreshCount;
		if (k == 0 || k > 4) return 0;
		for (var i = 0; i < k; i++) if (rS[i] != i || rC[i] != i) return 0;
		return k;
	}

	unsafe void Rebind(TimelineRef r, scoped Span<int> ch, ulong* keys, int keyCount, byte* idx, byte* rS, byte* rC, void** b, void** t, ref void** act)
	{
		r.Resolve(ch);
		PairTable.Bind(r, keys, keyCount, idx, rS, rC, ref _cache.RefreshCount, ref _cache.BoundMask);
		var id = _cache.Identity = ComputeIdentity(rS, rC);
		if (id != 0) act = b;
		else { act = t; for (var i = 0; i < _cache.RefreshCount; i++) t[rS[i]] = b[rC[i]]; }
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	unsafe void** GetTable(void** b)
	{
		if (_cache.Identity != 0) return b;
		var t = (void**)Unsafe.AsPointer(ref _cache.Columns[0]);
		var s = (byte*)Unsafe.AsPointer(ref _cache.RefreshSlot[0]);
		var c = (byte*)Unsafe.AsPointer(ref _cache.RefreshCol[0]);
		for (var i = 0; i < _cache.RefreshCount; i++) t[s[i]] = b[c[i]];
		return t;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
	internal unsafe void TickCore(delegate*<ulong*, int> getKeys, void** bases, uint gameTick, int delta)
	{
		if (delta == 0 || _rows.IsEmpty) return;
		if (_kernel != null) { KernelTick(getKeys, bases, gameTick, delta); return; }
		if ((delta == 1 | delta == -1) && _rows.Length == 1 && _bound)
		{
			ref var c = ref _rows[0];
			var addr = c.Reference.Address;
			if (addr == _cache.Asset && addr != 0)
			{
				var reverse = delta < 0;
				if (!c.Reference.Advance(reverse, c.Position, c.Cycle, out var np, out var nc, out var tick, out var cycle, out var flags)) return;
				c.Reference.Execute(reverse, tick, reverse ? gameTick - 1 : gameTick, cycle, flags, 0, Resolved, GetTable(bases));
				c.Position = np; c.Cycle = nc;
				return;
			}
		}
		TickGeneral(getKeys, bases, gameTick, delta);
	}

	unsafe void KernelTick(delegate*<ulong*, int> getKeys, void** bases, uint gameTick, int delta)
	{
		for (var row = 0; row < _rows.Length; row++)
			if (_rows[row].Reference.Address != _cache.Asset) { _kernel = null; break; }
		if (_kernel == null) { TickGeneral(getKeys, bases, gameTick, delta); return; }
		fixed (TimelineComponent* rows = _rows)
			_kernel((byte*)_cache.Asset, (int*)Unsafe.AsPointer(ref _cache.Heads[0]), GetTable(bases), rows, _rows.Length, gameTick, delta);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	unsafe void TickGeneral(delegate*<ulong*, int> getKeys, void** bases, uint gameTick, int delta)
	{
		ulong* keys = stackalloc ulong[4];
		var keyCount = getKeys != null ? getKeys(keys) : 0;
		var indices = (byte*)Unsafe.AsPointer(ref _cache.ColumnIndex[0]);
		var rSlots = (byte*)Unsafe.AsPointer(ref _cache.RefreshSlot[0]);
		var rCols = (byte*)Unsafe.AsPointer(ref _cache.RefreshCol[0]);
		var table = (void**)Unsafe.AsPointer(ref _cache.Columns[0]);
		if (!_bound)
		{
			for (var row = 0; row < _rows.Length; row++)
			{
				var reference = _rows[row].Reference;
				if (reference.Address != 0) PairTable.Bind(reference, keys, keyCount, indices, rSlots, rCols, ref _cache.RefreshCount, ref _cache.BoundMask);
			}
			_cache.Identity = ComputeIdentity(rSlots, rCols);
			_bound = true;
		}
		void** activeTable = GetTable(bases);
		var rowCount = _rows.Length;
		var cachedAsset = _cache.Asset;
		var warm = cachedAsset != 0;
		if (warm)
		{
			if (rowCount == 1)
				warm = _rows[0].Reference.Address == cachedAsset;
			else
			{
				for (var row = 0; row < rowCount; row++)
					if (_rows[row].Reference.Address != cachedAsset)
					{
						warm = false;
						break;
					}
			}
		}
		nint uniformAddress = 0;
		var pairs = 0;
		var cacheable = false;
		if (!warm)
		{
			uniformAddress = _rows[0].Reference.Address;
			var uniform = true;
			for (var row = 0; row < rowCount; row++)
			{
				var reference = _rows[row].Reference;
				if (reference.Address != uniformAddress) uniform = false;
				pairs = Math.Max(pairs, (int)reference.PairCount);
			}
			cacheable = uniform && uniformAddress != 0 && pairs <= 16;
		}
		Span<int> chains = warm ? default : stackalloc int[pairs];
		nint attached = warm ? _cache.Asset : cacheable ? uniformAddress : 0;
		if (warm)
			chains = Resolved;
		else if (cacheable)
		{
			_cache.Asset = uniformAddress;
			_cache.Count = pairs;
			chains = Resolved;
			_rows[0].Reference.Resolve(chains);
			if (Find != null)
			{
				var kernel = Find((byte*)uniformAddress, ((NativeHeader*)uniformAddress)->Bytes);
				if (kernel != null) _kernel = (delegate*<byte*, int*, void**, TimelineComponent*, int, uint, int, void>)kernel;
			}
		}
		if (delta == 1 || delta == -1)
		{
			var reverse = delta < 0;
			var targetGameTick = reverse ? gameTick - 1 : gameTick;
			if (rowCount == 1)
			{
				ref var c = ref _rows[0];
				if (c.Reference.Advance(reverse, c.Position, c.Cycle, out var np, out var nc, out var tick, out var cycle, out var flags))
				{
					if (c.Reference.Address != attached)
					{
						attached = c.Reference.Address;
						Rebind(c.Reference, chains, keys, keyCount, indices, rSlots, rCols, bases, table, ref activeTable);
					}
					c.Reference.Execute(reverse, tick, targetGameTick, cycle, flags, 0, chains, activeTable);
					c.Position = np;
					c.Cycle = nc;
				}
				return;
			}
			for (var row = 0; row < rowCount; row++)
			{
				ref var c = ref _rows[row];
				if (!c.Reference.Select(reverse, c.Position, c.Cycle, out _, out var tick, out var cycle, out var flags)) continue;
				if (c.Reference.Address != attached)
				{
					attached = c.Reference.Address;
					Rebind(c.Reference, chains, keys, keyCount, indices, rSlots, rCols, bases, table, ref activeTable);
				}
				c.Reference.Execute(reverse, tick, targetGameTick, cycle, flags, row, chains, activeTable);
			}
			for (var row = 0; row < rowCount; row++)
			{
				ref var c = ref _rows[row];
				if (c.Reference.Select(reverse, c.Position, c.Cycle, out var next, out _, out _, out _)) { c.Position = next.Position; c.Cycle = next.Cycle; }
			}
			return;
		}
		var isReverse = delta < 0;
		long remaining = isReverse ? -(long)delta : delta;
		var moved = true;
		while (moved && remaining-- != 0)
		{
			if (isReverse) gameTick--;
			moved = false;
			for (var row = 0; row < rowCount; row++)
			{
				ref var c = ref _rows[row];
				var reference = c.Reference;
				if (!reference.Select(isReverse, c.Position, c.Cycle, out _, out var tick, out var cycle, out var flags)) continue;
				moved = true;
				var address = reference.Address;
				if (address != attached)
				{
					attached = address;
					Rebind(reference, chains, keys, keyCount, indices, rSlots, rCols, bases, table, ref activeTable);
				}
				reference.Execute(isReverse, tick, gameTick, cycle, flags, row, chains, activeTable);
			}
			if (!moved) break;
			for (var row = 0; row < rowCount; row++)
			{
				ref var c = ref _rows[row];
				if (c.Reference.Select(isReverse, c.Position, c.Cycle, out var next, out _, out _, out _)) { c.Position = next.Position; c.Cycle = next.Cycle; }
			}
			if (!isReverse) gameTick++;
		}
	}
}

public ref struct TimelineQuery<TA> where TA : unmanaged
{
	internal TimelineQuery _q; internal ReadOnlySpan<TA> _a; internal bool _w;
	internal TimelineQuery(TimelineQuery q, ReadOnlySpan<TA> a, bool w) { _q = q; _a = a; _w = w; }
	internal void Ck<T>(ReadOnlySpan<T> c, bool w) where T : unmanaged { TimelineQuery.Ck(c, _q._rows); TimelineQuery.CkP(_a, _w, c, w); }
	public TimelineQuery<TA, T> Read<T>(ReadOnlySpan<T> c) where T : unmanaged { Ck(c, false); return new(this, c, false); }
	public TimelineQuery<TA, T> Write<T>(Span<T> c) where T : unmanaged { Ck(c, true); return new(this, c, true); }
	static unsafe int K(ulong* k) { k[0] = TypeKey<TA>.Value; return 1; }
	[MethodImpl(MethodImplOptions.AggressiveOptimization)] public unsafe void Tick(uint gt, int d = 1) { void* b = TimelineQuery.P(_a); _q.TickCore(&K, &b, gt, d); }
}

public ref struct TimelineQuery<TA, TB> where TA : unmanaged where TB : unmanaged
{
	internal TimelineQuery<TA> _p; internal ReadOnlySpan<TB> _b; internal bool _w;
	internal TimelineQuery(TimelineQuery<TA> p, ReadOnlySpan<TB> b, bool w) { _p = p; _b = b; _w = w; }
	internal void Ck<T>(ReadOnlySpan<T> c, bool w) where T : unmanaged { _p.Ck(c, w); TimelineQuery.CkP(_b, _w, c, w); }
	public TimelineQuery<TA, TB, T> Read<T>(ReadOnlySpan<T> c) where T : unmanaged { Ck(c, false); return new(this, c, false); }
	public TimelineQuery<TA, TB, T> Write<T>(Span<T> c) where T : unmanaged { Ck(c, true); return new(this, c, true); }
	static unsafe int K(ulong* k) { k[0] = TypeKey<TA>.Value; k[1] = TypeKey<TB>.Value; return 2; }
	[MethodImpl(MethodImplOptions.AggressiveOptimization)] public unsafe void Tick(uint gt, int d = 1) { void** b = stackalloc void*[2] { TimelineQuery.P(_p._a), TimelineQuery.P(_b) }; _p._q.TickCore(&K, b, gt, d); }
}

public ref struct TimelineQuery<TA, TB, TC> where TA : unmanaged where TB : unmanaged where TC : unmanaged
{
	internal TimelineQuery<TA, TB> _p; internal ReadOnlySpan<TC> _c; internal bool _w;
	internal TimelineQuery(TimelineQuery<TA, TB> p, ReadOnlySpan<TC> c, bool w) { _p = p; _c = c; _w = w; }
	internal void Ck<T>(ReadOnlySpan<T> c, bool w) where T : unmanaged { _p.Ck(c, w); TimelineQuery.CkP(_c, _w, c, w); }
	public TimelineQuery<TA, TB, TC, T> Read<T>(ReadOnlySpan<T> c) where T : unmanaged { Ck(c, false); return new(this, c, false); }
	public TimelineQuery<TA, TB, TC, T> Write<T>(Span<T> c) where T : unmanaged { Ck(c, true); return new(this, c, true); }
	static unsafe int K(ulong* k) { k[0] = TypeKey<TA>.Value; k[1] = TypeKey<TB>.Value; k[2] = TypeKey<TC>.Value; return 3; }
	[MethodImpl(MethodImplOptions.AggressiveOptimization)] public unsafe void Tick(uint gt, int d = 1) { void** b = stackalloc void*[3] { TimelineQuery.P(_p._p._a), TimelineQuery.P(_p._b), TimelineQuery.P(_c) }; _p._p._q.TickCore(&K, b, gt, d); }
}

public ref struct TimelineQuery<TA, TB, TC, TD> where TA : unmanaged where TB : unmanaged where TC : unmanaged where TD : unmanaged
{
	internal TimelineQuery<TA, TB, TC> _p; internal ReadOnlySpan<TD> _d; internal bool _w;
	internal TimelineQuery(TimelineQuery<TA, TB, TC> p, ReadOnlySpan<TD> d, bool w) { _p = p; _d = d; _w = w; }
	public TimelineQuery<TA, TB, TC, TD> Read<T>(ReadOnlySpan<T> c) where T : unmanaged => throw new ArgumentException("At most four columns.");
	public TimelineQuery<TA, TB, TC, TD> Write<T>(Span<T> c) where T : unmanaged => throw new ArgumentException("At most four columns.");
	static unsafe int K(ulong* k) { k[0] = TypeKey<TA>.Value; k[1] = TypeKey<TB>.Value; k[2] = TypeKey<TC>.Value; k[3] = TypeKey<TD>.Value; return 4; }
	[MethodImpl(MethodImplOptions.AggressiveOptimization)] public unsafe void Tick(uint gt, int d = 1) { void** b = stackalloc void*[4] { TimelineQuery.P(_p._p._p._a), TimelineQuery.P(_p._p._b), TimelineQuery.P(_p._c), TimelineQuery.P(_d) }; _p._p._p._q.TickCore(&K, b, gt, d); }
}

public unsafe ref struct FrameQuery<TTrack, TClip> where TTrack : unmanaged, IBlend<TClip> where TClip : unmanaged
{
	readonly TimelineRef _block;
	NativeStep* _steps;
	readonly uint _tick;
	readonly long _cycle;
	int _count;
	TClip _scratch;
	Frame<TTrack, TClip> _current;

	internal FrameQuery(in TimelineComponent component)
	{
		_block = component.Reference;
		_tick = component.Position;
		_cycle = component.Cycle;
		var stage = _block._p == null ? null : _block.StageOf(_tick);
		if (stage != null)
		{
			_steps = (NativeStep*)(_block._p + stage->ProgramOffset);
			_count = (int)stage->ProgramCount;
		}
	}

	public FrameQuery<TTrack, TClip> GetEnumerator() { var copy = this; return copy; }

	public bool MoveNext()
	{
		if (_count == 0) return false;
		var pairs = _block.Pairs;
		var block = _block._p;
		do
		{
			var step = *_steps++;
			_count--;
			if (pairs[step.Pair].Key == PairRuntime<TTrack, TClip>.Key)
			{
				_current = FrameSlot<TTrack, TClip>.ToFrame((FrameSlot<TTrack, TClip>*)(block + step.Slot), 0, _tick, _cycle, FrameFlags.None, (TClip*)Unsafe.AsPointer(ref _scratch));
				return true;
			}
		} while (_count > 0);
		return false;
	}

	public Frame<TTrack, TClip> Current => _current;
}

public unsafe struct UnmanagedTickState
{
	public nint Asset;
	public int RefreshCount;
	public ulong BoundMask;
	public byte Bound;
	public fixed int Heads[16];
	public fixed ulong Columns[256];
	public fixed byte ColumnIndex[256], RefreshSlot[256], RefreshCol[256];
}

static unsafe class UnmanagedTable
{
	internal struct Consumer { public int Next, Pair, Offset; public delegate* unmanaged<byte*, uint, uint, long, FrameFlags, void**, int, void> Execute; public delegate* unmanaged<ulong*, int, byte*, void> Bind; }
	struct Slot { public ulong Key; public int Head; }

	const int SlotCount = 1024, PairCapacity = 512, ConsumerCapacity = 1024, MaxPointers = 256, HeaderBytes = 16;
	internal const int ConsumerOffset = HeaderBytes + 16 * SlotCount;
	static readonly byte* _block = (byte*)NativeMemory.AlignedAlloc((nuint)(ConsumerOffset + 32 * ConsumerCapacity), 64);
	static volatile int _gate;

	static UnmanagedTable() => Unsafe.InitBlock(_block, 0, ConsumerOffset);

	internal static byte* Block => _block;

	internal static void Install(ulong key, delegate* unmanaged<byte*, uint, uint, long, FrameFlags, void**, int, void> e, delegate* unmanaged<ulong*, int, byte*, void> b)
	{
		while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0) Thread.Yield();
		try
		{
			var slots = (Slot*)(_block + HeaderBytes);
			var slot = Probe(_block, key);
			if (slots[slot].Key == 0)
			{
				if (*(int*)_block == PairCapacity) throw new InvalidOperationException("Pair capacity exhausted.");
				slots[slot].Head = -1;
				Volatile.Write(ref slots[slot].Key, key);
				(*(int*)_block)++;
			}
			var index = *(int*)(_block + 4);
			if (index == ConsumerCapacity || index * 4 + 4 > MaxPointers) throw new InvalidOperationException("Consumer capacity exhausted.");
			((Consumer*)(_block + ConsumerOffset))[index] = new Consumer { Next = slots[slot].Head, Pair = slot, Execute = e, Bind = b, Offset = index * 4 };
			Volatile.Write(ref slots[slot].Head, index);
			Volatile.Write(ref *(int*)(_block + 4), index + 1);
		}
		finally { _gate = 0; }
	}

	static int Probe(byte* table, ulong key)
	{
		var slots = (Slot*)(table + HeaderBytes);
		var slot = (int)key & (SlotCount - 1);
		while (true)
		{
			var candidate = slots[slot].Key;
			if (candidate == 0 || candidate == key) return slot;
			slot = (slot + 1) & (SlotCount - 1);
		}
	}

	internal static int Head(byte* table, ulong key)
	{
		var slots = (Slot*)(table + HeaderBytes);
		var slot = Probe(table, key);
		return slots[slot].Key == 0 ? -1 : slots[slot].Head;
	}

	internal static void Bind(byte* table, TimelineRef asset, ulong* keys, int keyCount, byte* indices, byte* rSlots, byte* rCols, ref int rCount, ref ulong boundMask)
	{
		var total = *(int*)(table + 4);
		var slots = (Slot*)(table + HeaderBytes);
		var consumers = (Consumer*)(table + ConsumerOffset);
		for (var entry = 0; entry < total; entry++)
			if (asset.Uses(slots[consumers[entry].Pair].Key) && (boundMask & (1ul << entry)) == 0)
			{
				boundMask |= 1ul << entry;
				var offset = consumers[entry].Offset;
				consumers[entry].Bind(keys, keyCount, indices + offset);
				for (var k = 0; k < 4; k++)
				{
					var col = indices[offset + k];
					if (col != 0) { rSlots[rCount] = (byte)(offset + k); rCols[rCount++] = (byte)(col - 1); }
				}
			}
	}
}

static unsafe class UnmanagedTick
{
	static void Resolve(byte* table, TimelineRef r, int* chains)
	{
		for (var i = 0; i < r.PairCount; i++) chains[i] = UnmanagedTable.Head(table, r.Pairs[i].Key);
	}

	static void** Table(UnmanagedTickState* state, void** bases, void** columns)
	{
		for (var i = 0; i < state->RefreshCount; i++) columns[state->RefreshSlot[i]] = bases[state->RefreshCol[i]];
		return columns;
	}

	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	static void Execute(byte* table, TimelineRef reference, bool reverse, uint tick, uint gameTick, long cycle, FrameFlags flags, int row, int* chains, void** columns)
	{
		var p = reference._p;
		var header = (NativeHeader*)p;
		var stage = header->StageCount == 1 ? (NativeStage*)(p + header->StageOffset) : reference.StageOf(tick);
		if (stage == null) return;
		var steps = (NativeStep*)(p + stage->ProgramOffset);
		var consumers = (UnmanagedTable.Consumer*)(table + UnmanagedTable.ConsumerOffset);
		var count = (int)stage->ProgramCount;
		var step = reverse ? steps + count - 1 : steps;
		var stride = reverse ? -1 : 1;
		int* rev = stackalloc int[64];
		while (count-- > 0)
		{
			var slot = p + step->Slot;
			var head = chains[step->Pair];
			if (reverse && head >= 0 && consumers[head].Next >= 0)
			{
				var n = 0;
				for (var e = head; e >= 0; e = consumers[e].Next) rev[n++] = e;
				while (n-- > 0) { var e = rev[n]; consumers[e].Execute(slot, gameTick, tick, cycle, flags, columns + consumers[e].Offset, row); }
			}
			else for (var entry = head; entry >= 0; entry = consumers[entry].Next) consumers[entry].Execute(slot, gameTick, tick, cycle, flags, columns + consumers[entry].Offset, row);
			step += stride;
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	internal static void TickCoreUnmanaged(UnmanagedTickState* state, byte* table, void** bases, TimelineComponent* rows, int rowCount, uint gameTick, int delta, ulong* keys, int keyCount)
	{
		if (delta == 0 || rowCount == 0) return;
		var indices = state->ColumnIndex;
		var rSlots = state->RefreshSlot;
		var rCols = state->RefreshCol;
		var columns = (void**)state->Columns;
		if (state->Bound == 0)
		{
			for (var row = 0; row < rowCount; row++)
				if (rows[row].Reference.Address != 0) UnmanagedTable.Bind(table, rows[row].Reference, keys, keyCount, indices, rSlots, rCols, ref state->RefreshCount, ref state->BoundMask);
			state->Bound = 1;
		}
		void** activeTable = Table(state, bases, columns);
		var uniformAddress = rows[0].Reference.Address;
		var uniform = uniformAddress != 0;
		var pairs = 0;
		for (var row = 0; row < rowCount; row++)
		{
			var reference = rows[row].Reference;
			if (reference.Address != uniformAddress) uniform = false;
			if (reference.PairCount > pairs) pairs = (int)reference.PairCount;
		}
		int* chains = stackalloc int[pairs];
		nint attached = 0;
		if (uniform && pairs <= 16)
		{
			attached = uniformAddress;
			chains = state->Heads;
			if (state->Asset != uniformAddress)
			{
				state->Asset = uniformAddress;
				Resolve(table, rows[0].Reference, chains);
			}
		}
		var isReverse = delta < 0;
		long remaining = isReverse ? -(long)delta : delta;
		var moved = true;
		while (moved && remaining-- != 0)
		{
			if (isReverse) gameTick--;
			moved = false;
			for (var row = 0; row < rowCount; row++)
			{
				var c = rows + row;
				var reference = c->Reference;
				if (!reference.Select(isReverse, c->Position, c->Cycle, out _, out var tick, out var cycle, out var flags)) continue;
				moved = true;
				var address = reference.Address;
				if (address != attached)
				{
					attached = address;
					Resolve(table, reference, chains);
					UnmanagedTable.Bind(table, reference, keys, keyCount, indices, rSlots, rCols, ref state->RefreshCount, ref state->BoundMask);
					activeTable = Table(state, bases, columns);
				}
				Execute(table, reference, isReverse, tick, gameTick, cycle, flags, row, chains, activeTable);
			}
			if (!moved) break;
			for (var row = 0; row < rowCount; row++)
			{
				var c = rows + row;
			if (c->Reference.Select(isReverse, c->Position, c->Cycle, out var next, out _, out _, out _)) { c->Position = next.Position; c->Cycle = next.Cycle; }
			}
			if (!isReverse) gameTick++;
		}
	}
}
