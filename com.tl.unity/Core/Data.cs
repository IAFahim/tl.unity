using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using unsafe ExecThunk = delegate*<byte*, byte*, ushort, Tl.FrameFlags, void**, int, void>;
using unsafe BindThunk = delegate*<ulong*, int, byte*, void>;
using unsafe KeysThunk = delegate*<ulong*, byte*, int>;
using unsafe DiagThunk = delegate*<ulong, long, void>;
using static System.Runtime.CompilerServices.MethodImplOptions;

namespace Tl;

static class Keying
{
	internal const ulong Seed = 14695981039346656037, Prime = 1099511628211;

	internal static ulong Of(string text) { var hash = Seed; foreach (var c in text) hash = (hash ^ c) * Prime; return hash | 1; }

	internal static string StableName(Type type) => Name(type) + ", " + type.Assembly.GetName().Name;

	static string Name(Type type)
	{
		var prefix = type.IsNested ? Name(type.DeclaringType!) + "+" : type.Namespace is { } space ? space + "." : "";
		if (!type.IsConstructedGenericType) return prefix + type.Name;
		var text = prefix + type.Name + "<";
		foreach (var argument in type.GetGenericArguments()) text += StableName(argument) + ",";
		return text;
	}
}

#if NET
public static class TypeKey<T> where T : allows ref struct
#else
public static class TypeKey<T>
#endif
{
	public static readonly ulong Value = Keying.Of(Keying.StableName(typeof(T)));
}

#pragma warning disable CS0649
struct NativeHeader
{
    public uint Magic, Version, Loops, Duration, TrackCount, StageCount, PairCount, PairOffset, StageOffset, PoolOffset, FrameOffset, HotLength, Bytes;
    public uint Reserved0, Reserved1, Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
struct NativePair
{
    public ulong Key;
    public uint SlotStride;
    public uint TrackPoolOffset;
    public uint TrackPoolCount;
    public uint TrackValueBytes;
    public uint ClipPoolOffset;
    public uint ClipPoolCount;
    public uint ClipValueBytes;
    public ulong Layout;
}

struct NativeStage { public uint Start, End, ProgramOffset, ProgramCount; }
struct NativeStep { public uint Slot, Pair; }

[StructLayout(LayoutKind.Sequential)]
 struct SlotRow
{
	internal const ushort NoClipValue = 0xFFFF;
	internal const uint RowBytes = 24u;

	public ushort TrackValueIndex;
	public ushort FirstValueIndex;
	public ushort SecondValueIndex;
	public byte TrackIndex;
	public uint WindowStart;
	public uint WindowEnd;
	public uint FactorStart;
	public uint FactorSpan;

	internal static unsafe Frame<TTrack, TClip> ToFrame<TTrack, TClip>(SlotRow* row, byte* pair, ushort tick, FrameFlags flags, ref TClip scratch)
		where TTrack : unmanaged, IBlend<TClip>
		where TClip : unmanaged
	{
		var pools = (NativePair*)pair;
		ref readonly var track = ref *(TTrack*)(pair + pools->TrackPoolOffset + row->TrackValueIndex * (nuint)sizeof(TTrack));
		var clipPool = pair + pools->ClipPoolOffset;
		if (row->FactorSpan == 0) scratch = *(TClip*)(clipPool + row->FirstValueIndex * (nuint)sizeof(TClip));
		else
		{
			var factor = row->FactorSpan <= 1 ? 0.5f : (tick - row->FactorStart) / (float)(row->FactorSpan - 1);
			var first = *(TClip*)(clipPool + row->FirstValueIndex * (nuint)sizeof(TClip));
			var second = *(TClip*)(clipPool + row->SecondValueIndex * (nuint)sizeof(TClip));
			track.Blend(in first, in second, factor, out var blended);
			scratch = blended;
		}
		if (tick == row->WindowStart) flags |= FrameFlags.ClipStart;
		if (tick == row->WindowEnd - 1) flags |= FrameFlags.ClipEnd;
		return new Frame<TTrack, TClip>(in track, in scratch, tick, (ushort)(row->WindowEnd - row->WindowStart), (ushort)(tick - row->WindowStart), row->TrackIndex, flags);
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


	internal int StageCount => _p == null ? 0 : (int)Header->StageCount;
	internal NativeStage* Stages => (NativeStage*)(_p + Header->StageOffset);
	internal NativeStage* StageOf(ushort tick)
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

	internal NativePair* PairOf(ulong key)
	{
		var pairs = Pairs;
		for (var i = 0; i < PairCount; i++) if (pairs[i].Key == key) return pairs + i;
		return null;
	}

	internal void Resolve(Span<int> chains)
	{
		var pairs = Pairs;
		for (var i = 0; i < PairCount; i++) chains[i] = PairTable.HeadOf(pairs[i].Key);
	}

	internal void Resolve(Span<int> chains, ulong pairKey)
	{
		var pairs = Pairs;
		for (var i = 0; i < PairCount; i++)
			chains[i] = pairs[i].Key == pairKey ? PairTable.HeadOf(pairs[i].Key) : -1;
	}

	[MethodImpl(AggressiveOptimization)]
	internal void ExecuteDispatch(bool reverse, ushort tick, FrameFlags flags, int row, Span<int> chains, void** columns)
	{
		var stage = Header->StageCount == 1 ? (NativeStage*)(_p + Header->StageOffset) : StageOf(tick);
		if (stage == null) return;
		var steps = (NativeStep*)(_p + stage->ProgramOffset);
		var pairs = Pairs;
		var count = (int)stage->ProgramCount;
		var step = reverse ? steps + count - 1 : steps;
		var stride = reverse ? -1 : 1;
		while (count-- > 0)
		{
			PairTable.RunDispatch(chains[(int)step->Pair], reverse, _p + step->Slot, (byte*)(pairs + step->Pair), tick, flags, row, columns);
			step += stride;
		}
	}

	[MethodImpl(AggressiveOptimization)]
	internal void ExecuteWindow(bool reverse, ushort tick, FrameFlags flags, int row, Span<int> chains, void** columns)
	{
		var stage = Header->StageCount == 1 ? (NativeStage*)(_p + Header->StageOffset) : StageOf(tick);
		if (stage == null) return;
		var steps = (NativeStep*)(_p + stage->ProgramOffset);
		var pairs = Pairs;
		var count = (int)stage->ProgramCount;
		var step = reverse ? steps + count - 1 : steps;
		var stride = reverse ? -1 : 1;
		while (count-- > 0)
		{
			var slot = _p + step->Slot;
			var pair = (byte*)(pairs + step->Pair);
			var head = chains[(int)step->Pair];
			PairTable.RunChain(head, reverse, slot, pair, tick, flags, columns, row);
			step += stride;
		}
	}

	internal static void Validate(ReadOnlySpan<byte> baked)
	{
		void Fail(string message) => throw new ArgumentException(message);
		if (baked.Length < 64) Fail("TLB truncated.");
		var h = MemoryMarshal.Read<NativeHeader>(baked);
		if (h.Magic != 0x31424C54 || (h.Version != 5 && h.Version != 6)) Fail("TLB magic or version invalid; rebake the asset with the current toolchain.");
		if (h.Version == 6 && baked.Length < 80) Fail("TLB truncated.");
		if (h.Duration > ushort.MaxValue) Fail("TLB duration exceeds the 65,535-tick position domain.");
		if (h.PairCount > 256) Fail("TLB declares more than 256 pairs; the runtime resolves at most 256 pair chains per asset.");
		if (h.Bytes != (uint)baked.Length) Fail("TLB size mismatch.");
		if (h.HotLength == 0 || h.HotLength > h.Bytes) Fail("TLB hot length invalid.");
		var minPair = h.Version == 6 ? 80u : 64u;
		if (h.PairOffset < minPair || (h.PairOffset | h.StageOffset | h.PoolOffset | h.FrameOffset) % 8 != 0) Fail("TLB offsets must be 8-aligned.");
		if (h.PairOffset + 48ul * h.PairCount > h.StageOffset) Fail("TLB pair table out of bounds.");
		if (h.StageOffset + 16ul * h.StageCount > h.PoolOffset || h.PoolOffset > h.FrameOffset || h.FrameOffset > h.HotLength) Fail("TLB sections out of bounds.");
		uint next = 0, channel = 0, flags = 0, markerOff = 0, markerCount = 0;
		if (h.Version == 6)
		{
			next = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice(52));
			channel = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice(56));
			flags = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice(60));
			markerOff = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice(64));
			markerCount = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice(68));
			if ((flags & ~1u) != 0) Fail("TLB flags invalid.");
			if (h.Loops != 0 && next != 0) Fail("TLB next is incompatible with loop:true.");
			if (h.Loops != 0 && (flags & 1u) != 0) Fail("TLB hold is incompatible with loop:true.");
			if (next > ushort.MaxValue || channel > ushort.MaxValue) Fail("TLB next/channel out of range.");
			if (markerCount == 0) { if (markerOff != 0) Fail("TLB marker offset must be zero when count is zero."); }
			else
			{
				if (markerOff < h.FrameOffset || markerOff % 8 != 0 || markerOff > h.HotLength) Fail("TLB marker section out of bounds.");
				ValidateMarkers(baked, (int)markerOff, (int)markerCount, h.Duration, h.HotLength);
			}
		}
		var pairs = MemoryMarshal.Cast<byte, NativePair>(baked.Slice((int)h.PairOffset, 48 * (int)h.PairCount));
		Action<string> fail = Fail;
		for (var i = 1; i < pairs.Length; i++) if (pairs[i - 1].Key >= pairs[i].Key) Fail("TLB pair keys must be sorted.");
		for (var i = 0; i < pairs.Length; i++)
		{
			var pair = pairs[i];
			if (pair.SlotStride % 8 != 0 || pair.SlotStride < SlotRow.RowBytes) Fail("TLB slot stride must be 8-aligned.");
			ValidatePool(h, h.PairOffset + 48ul * (uint)i + pair.TrackPoolOffset, pair.TrackPoolCount, pair.TrackValueBytes, fail);
			ValidatePool(h, h.PairOffset + 48ul * (uint)i + pair.ClipPoolOffset, pair.ClipPoolCount, pair.ClipValueBytes, fail);
		}
		var stages = MemoryMarshal.Cast<byte, NativeStage>(baked.Slice((int)h.StageOffset, 16 * (int)h.StageCount));
		var programs = h.StageOffset + 16ul * h.StageCount;
		uint edge = 0;
		foreach (var stage in stages)
		{
			if (stage.End <= stage.Start) Fail("TLB stages must partition [0, Duration) into non-empty ranges.");
			if (stage.Start != edge || stage.ProgramOffset < programs || stage.ProgramOffset % 8 != 0 || stage.ProgramOffset + 8ul * stage.ProgramCount > h.PoolOffset) Fail("TLB stages must be monotonic.");
			var steps = MemoryMarshal.Cast<byte, NativeStep>(baked.Slice((int)stage.ProgramOffset, 8 * (int)stage.ProgramCount));
			foreach (var step in steps)
			{
				if (step.Pair >= h.PairCount) Fail("TLB step pair out of bounds.");
				var pair = pairs[(int)step.Pair];
				if (step.Slot < h.FrameOffset || step.Slot % 8 != 0 || (ulong)step.Slot + pair.SlotStride > h.HotLength) Fail("TLB slots must be 8-aligned in bounds.");
				if (h.Version == 6 && markerCount != 0 && step.Slot >= markerOff) Fail("TLB slots must precede the marker section.");
				ValidateRow(baked, (int)step.Slot, pair, h.TrackCount, stage.Start, stage.End, fail);
			}
			edge = stage.End;
		}
		if (edge != h.Duration) Fail("TLB stages must cover duration.");
	}

	static void ValidateMarkers(ReadOnlySpan<byte> baked, int at, int count, uint duration, uint hotLength)
	{
		void Fail(string message) => throw new ArgumentException(message);
		var off = (uint)at;
		for (var i = 0; i < count; i++)
		{
			if (off + 16u > hotLength) Fail("TLB marker entry out of bounds.");
			var tick = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice((int)off));
			var size = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice((int)off + 4));
			var key = BinaryPrimitives.ReadUInt64LittleEndian(baked.Slice((int)off + 8));
			if (key == 0) Fail("TLB marker type key invalid.");
			if (size == 0 || size > 4096) Fail("TLB marker payload size out of range.");
			if (tick >= duration && duration != 0) Fail("TLB marker tick outside duration.");
			off += 16u + ((size + 3u) & ~3u);
			if (off > hotLength) Fail("TLB marker payload out of bounds.");
		}
		if (off > hotLength) Fail("TLB marker section out of bounds.");
	}

	static void ValidatePool(NativeHeader h, ulong address, uint count, uint valueBytes, Action<string> fail)
	{
		if (address % 16 != 0) fail("TLB value pools must be 16-byte aligned.");
		if (address < h.PoolOffset || count > 65535 || address + (ulong)count * valueBytes > h.FrameOffset) fail("TLB value pool out of bounds.");
	}

	static void ValidateRow(ReadOnlySpan<byte> baked, int at, NativePair pair, uint trackCount, uint stageStart, uint stageEnd, Action<string> fail)
	{
		var trackValue = BinaryPrimitives.ReadUInt16LittleEndian(baked.Slice(at));
		var first = BinaryPrimitives.ReadUInt16LittleEndian(baked.Slice(at + 2));
		var second = BinaryPrimitives.ReadUInt16LittleEndian(baked.Slice(at + 4));
		var trackIndex = baked[at + 6];
		var windowStart = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice(at + 8));
		var windowEnd = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice(at + 12));
		var factorStart = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice(at + 16));
		var factorSpan = BinaryPrimitives.ReadUInt32LittleEndian(baked.Slice(at + 20));
		if (trackValue >= pair.TrackPoolCount || first >= pair.ClipPoolCount) fail("TLB slot value index out of bounds.");
		if (second != SlotRow.NoClipValue && second >= pair.ClipPoolCount) fail("TLB slot second value index out of bounds.");
		if (factorSpan != 0 && second == SlotRow.NoClipValue) fail("TLB blend window requires a second value index.");
		if (factorSpan == 0 && second != SlotRow.NoClipValue) fail("TLB second value index requires a blend window.");
		if (trackIndex >= trackCount) fail("TLB slot track index out of bounds.");
		if (stageStart < windowStart || stageEnd > windowEnd) fail("TLB slot window must contain the referencing stage.");
		if (factorSpan != 0 && (stageStart < factorStart || stageEnd > factorStart + (ulong)factorSpan)) fail("TLB blend window must contain the referencing stage.");
	}
}



	static unsafe class PairTable
{
internal const int SlotRow = 40;
internal const int MemoResults = 10;
	internal struct Consumer { public int Next, Offset, Feed, Ordinal, NameOffset; public ulong PairKey; public ExecThunk Execute; public BindThunk Bind; public KeysThunk Keys; public DiagThunk Diag; public byte DispatchOnly; }
	struct Slot { public ulong Key; public int Head, Tail; }
	const int SlotCount = 1024;
	internal static ulong _slotsBase = (ulong)NativeMemory.AlignedAlloc((nuint)(8 + 24 * SlotCount), 64);
	static ulong _consumersBase = (ulong)NativeMemory.AlignedAlloc((nuint)(sizeof(Consumer) * 64), 64);
	internal static int _consumerCapacity = 64;
	static ulong _namesBase = (ulong)NativeMemory.AlignedAlloc(4096, 64);
	static int _namesLength;
	static int _namesCapacity = 4096;
	static volatile int _gate;
	internal static volatile int _sealed;
	internal static int _epoch;
	internal static int _pairs, _consumers;

	static PairTable()
	{
		Unsafe.InitBlock((void*)_slotsBase, 0, (uint)(8 + 24 * SlotCount));
		*(int*)_slotsBase = SlotCount;
	}

	internal static Consumer* ConsumerAt => (Consumer*)Volatile.Read(ref _consumersBase);
	internal static int PointerBound => _consumerCapacity * SlotRow;
	internal static int ConsumerCount => Volatile.Read(ref _consumers);

	static void Claim(ulong key, out Slot* slots, out int slot)
	{
		while (true)
		{
			slot = Probe(key, _slotsBase, out slots);
			if (slots[slot].Key != 0) return;
			if (_pairs * 2 < *(int*)_slotsBase) break;
			GrowSlots();
		}
		slots[slot].Head = -1;
		slots[slot].Tail = -1;
		Volatile.Write(ref slots[slot].Key, key);
		_pairs++;
	}

	static void GrowSlots()
	{
		var old = _slotsBase;
		var oldCap = *(int*)old;
		var cap = oldCap * 2;
		var grown = (ulong)NativeMemory.AlignedAlloc((nuint)(8 + 24 * cap), 64);
		Unsafe.InitBlock((void*)grown, 0, (uint)(8 + 24 * cap));
		*(int*)grown = cap;
		var from = (Slot*)(old + 8ul);
		var to = (Slot*)(grown + 8ul);
		for (var i = 0; i < oldCap; i++)
		{
			if (from[i].Key == 0) continue;
			var s = (int)(from[i].Key & (ulong)(cap - 1));
			while (to[s].Key != 0) s = (s + 1) & (cap - 1);
			to[s] = from[i];
			((ulong*)(to + cap))[s] = ((ulong*)(from + oldCap))[i];
		}
		Volatile.Write(ref _slotsBase, grown);
	}

	internal static void VerifyLayout(ulong key, ulong layout)
	{
		while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0) Thread.Yield();
		try
		{
			Claim(key, out var slots, out var slot);
			Volatile.Write(ref ((ulong*)(slots + *(int*)_slotsBase))[slot], layout);
		}
		finally
		{
			_gate = 0;
		}
	}

	internal static ulong LayoutOf(ulong key)
	{
		var block = Volatile.Read(ref _slotsBase);
		var slot = Probe(key, block, out var slots);
		return slots[slot].Key == key ? Volatile.Read(ref ((ulong*)(slots + *(int*)block))[slot]) : 0;
	}

	internal static int Install(ulong key, ExecThunk e, BindThunk b, string identity, bool dispatchOnly = false, KeysThunk resultKeys = null, DiagThunk diag = null, int feed = -1)
	{
		while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0) Thread.Yield();
		try
		{
			if (_sealed != 0) throw new InvalidOperationException($"Consumer registry is sealed; '{identity}' cannot install after Timeline.SealConsumers().");
			var name = InternName(identity);
			Claim(key, out var slots, out var slot);
			if (_consumers == _consumerCapacity)
			{
				var grown = (Consumer*)NativeMemory.AlignedAlloc((nuint)(sizeof(Consumer) * _consumerCapacity * 2), 64);
				var copy = sizeof(Consumer) * _consumers;
				Buffer.MemoryCopy((Consumer*)_consumersBase, grown, copy, copy);
				_consumerCapacity *= 2;
				Volatile.Write(ref _consumersBase, (ulong)grown);
			}
			var consumers = ConsumerAt;
			var entry = _consumers;
			var ordinal = 0;
			for (var walk = slots[slot].Head; walk >= 0; walk = consumers[walk].Next) ordinal++;
			if (feed >= 0 && (feed >= entry || consumers[feed].PairKey != key || consumers[feed].DispatchOnly != 0 || consumers[feed].Keys == null))
				throw new InvalidOperationException($"Consumer '{identity}' names feed handle {feed}, which is not a result-producing Fold consumer of the same pair installed before it.");
			consumers[entry] = new Consumer { Next = -1, PairKey = key, Execute = e, Bind = b, Keys = resultKeys, Diag = diag, DispatchOnly = dispatchOnly ? (byte)1 : (byte)0, Offset = ordinal * SlotRow, Feed = feed >= 0 ? consumers[feed].Ordinal : -1, Ordinal = ordinal, NameOffset = name };
			if (slots[slot].Tail >= 0) Volatile.Write(ref consumers[slots[slot].Tail].Next, entry);
			else Volatile.Write(ref slots[slot].Head, entry);
			slots[slot].Tail = entry;
			Volatile.Write(ref _consumers, entry + 1);
			_epoch++;
			return entry;
		}
		finally
		{
			_gate = 0;
		}
	}

	static int InternName(string identity)
	{
		Span<byte> small = stackalloc byte[512];
		byte[]? large = null;
		int length;
		if (identity.Length < 128) length = Encoding.UTF8.GetBytes(identity.AsSpan(), small);
		else { large = Encoding.UTF8.GetBytes(identity); length = large.Length; }
		if (length > ushort.MaxValue) throw new InvalidOperationException("Consumer identity exceeds 65,535 UTF-8 bytes.");
		if (_namesCapacity - _namesLength < length + 2)
		{
			var cap = _namesCapacity;
			while (cap - _namesLength < length + 2) cap *= 2;
			var grown = (ulong)NativeMemory.AlignedAlloc((nuint)cap, 64);
			Buffer.MemoryCopy((void*)_namesBase, (void*)grown, cap, _namesLength);
			NativeMemory.Free((void*)_namesBase);
			_namesBase = grown;
			_namesCapacity = cap;
		}
		var offset = _namesLength;
		var ptr = (byte*)(_namesBase + (ulong)offset);
		ptr[0] = (byte)length;
		ptr[1] = (byte)(length >> 8);
		var destination = new Span<byte>(ptr + 2, length);
		if (large == null) small[..length].CopyTo(destination);
		else large.AsSpan(0, length).CopyTo(destination);
		_namesLength += length + 2;
		return offset;
	}

	static int CompareNames(int left, int right)
	{
		var names = (byte*)_namesBase;
		var a = names + left;
		var b = names + right;
		var leftLength = a[0] | (a[1] << 8);
		var rightLength = b[0] | (b[1] << 8);
		var count = Math.Min(leftLength, rightLength);
		for (var i = 0; i < count; i++)
		{
			var delta = a[2 + i] - b[2 + i];
			if (delta != 0) return delta;
		}
		return leftLength - rightLength;
	}

	internal static void Seal()
	{
		while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0) Thread.Yield();
		try
		{
			_sealed = 1;
			var source = ConsumerAt;
			var slots = (Slot*)(_slotsBase + 8ul);
			var cap = *(int*)_slotsBase;
			var total = Math.Max(_consumers, 1);
			var arena = (Consumer*)NativeMemory.AlignedAlloc((nuint)(sizeof(Consumer) * total), 64);
			int* order = null;
			int* dense = null;
			var capacity = 0;
			var next = 0;
			for (var s = 0; s < cap; s++)
			{
				if (slots[s].Key == 0) continue;
				var head = slots[s].Head;
				if (head < 0) continue;
				var count = ChainLength(head);
				if (count > capacity)
				{
					if (order != null) NativeMemory.Free(order);
					if (dense != null) NativeMemory.Free(dense);
					capacity = count;
					order = (int*)NativeMemory.AlignedAlloc((nuint)(sizeof(int) * capacity), 16);
					dense = (int*)NativeMemory.AlignedAlloc((nuint)(sizeof(int) * capacity), 16);
				}
				var filled = 0;
				for (var e = head; e >= 0; e = source[e].Next) order[filled++] = e;
				for (var a = 1; a < count; a++)
				{
					var value = order[a];
					var b = a - 1;
					while (b >= 0 && CompareNames(source[order[b]].NameOffset, source[value].NameOffset) > 0) { order[b + 1] = order[b]; b--; }
					order[b + 1] = value;
				}
				for (var i = 0; i < count; i++) dense[source[order[i]].Ordinal] = i;
				for (var i = 0; i < count; i++)
				{
					arena[next + i] = source[order[i]];
					arena[next + i].Ordinal = i;
					arena[next + i].Offset = i * SlotRow;
					arena[next + i].Next = next + i + 1;
				}
				arena[next + count - 1].Next = -1;
				for (var i = 0; i < count; i++)
					if (arena[next + i].Feed >= 0)
						arena[next + i].Feed = dense[arena[next + i].Feed];
				slots[s].Head = next;
				slots[s].Tail = next + count - 1;
				next += count;
			}
			if (order != null) NativeMemory.Free(order);
			if (dense != null) NativeMemory.Free(dense);
			_consumerCapacity = _consumers;
			NativeMemory.Free(source);
			Volatile.Write(ref _consumersBase, (ulong)arena);
			_epoch++;
		}
		finally
		{
			_gate = 0;
		}
	}

	internal static int ChainLength(int head)
	{
		var length = 0;
		var consumers = ConsumerAt;
		for (var entry = head; entry >= 0; entry = consumers[entry].Next) length++;
		return length;
	}

	[MethodImpl(AggressiveInlining)]
	internal static void RunChain(int head, bool reverse, byte* slot, byte* pair, ushort tick, FrameFlags flags, void** columns, int row)
	{
		var consumers = ConsumerAt;
		if (reverse && head >= 0 && _sealed != 0)
		{
			var end = head;
			while (consumers[end].Next >= 0) end = consumers[end].Next;
			for (var entry = end; entry >= head; entry--)
			{
				if (consumers[entry].DispatchOnly != 0) continue;
				consumers[entry].Execute(slot, pair, tick, flags, columns + consumers[entry].Offset, row);
			}
		}
		else if (reverse && head >= 0 && consumers[head].Next >= 0)
		{
			int* rev = stackalloc int[ChainLength(head)];
			var n = 0;
			for (var e = head; e >= 0; e = consumers[e].Next)
			{
				if (consumers[e].DispatchOnly != 0) continue;
				rev[n++] = e;
			}
			while (n-- > 0)
			{
				var e = rev[n];
				consumers[e].Execute(slot, pair, tick, flags, columns + consumers[e].Offset, row);
			}
		}
		else
		{
			for (var entry = head; entry >= 0; entry = consumers[entry].Next)
			{
				if (consumers[entry].DispatchOnly != 0) continue;
				consumers[entry].Execute(slot, pair, tick, flags, columns + consumers[entry].Offset, row);
			}
		}
	}

	internal static void RunDispatch(int head, bool reverse, byte* slot, byte* pair, ushort tick, FrameFlags flags, int row, void** columns)
	{
		var consumers = ConsumerAt;
		if (reverse && head >= 0 && _sealed != 0)
		{
			var end = head;
			while (consumers[end].Next >= 0) end = consumers[end].Next;
			for (var entry = end; entry >= head; entry--)
			{
				if (consumers[entry].DispatchOnly == 0) continue;
				if (columns == null && consumers[entry].Keys != null) ThrowUnfedLiveColumn(entry, -1);
			}
			for (var entry = end; entry >= head; entry--)
			{
				if (consumers[entry].DispatchOnly == 0) continue;
				consumers[entry].Execute(slot, pair, tick, flags, columns + consumers[entry].Offset, row);
			}
		}
		else if (reverse && head >= 0 && consumers[head].Next >= 0)
		{
			int* rev = stackalloc int[ChainLength(head)];
			var n = 0;
			for (var e = head; e >= 0; e = consumers[e].Next)
			{
				if (consumers[e].DispatchOnly == 0) continue;
				if (columns == null && consumers[e].Keys != null) ThrowUnfedLiveColumn(e, -1);
				rev[n++] = e;
			}
			while (n-- > 0) consumers[rev[n]].Execute(slot, pair, tick, flags, columns + consumers[rev[n]].Offset, row);
		}
		else for (var entry = head; entry >= 0; entry = consumers[entry].Next)
			if (consumers[entry].DispatchOnly != 0)
			{
				if (columns == null && consumers[entry].Keys != null) ThrowUnfedLiveColumn(entry, -1);
				consumers[entry].Execute(slot, pair, tick, flags, columns + consumers[entry].Offset, row);
			}
	}

	[DoesNotReturn]
	internal static void ThrowUnfedLiveColumn(int entry, int slot)
	{
		var consumers = ConsumerAt;
		if (slot < 0)
		{
			ulong* keys = stackalloc ulong[SlotRow];
			byte* meta = stackalloc byte[SlotRow];
			var n = Math.Min(consumers[entry].Keys(keys, meta), SlotRow);
			for (var j = 0; j < n && slot < 0; j++) if ((meta[j] & 0x60) == 0) slot = j;
			for (var j = 0; j < n && slot < 0; j++) if ((meta[j] & 0x20) != 0) slot = j;
		}
		if (slot >= 0 && consumers[entry].Diag != null) consumers[entry].Diag(0, slot);
		throw new ArgumentException("A live timeline consumer requires caller columns.");
	}

	internal static int HeadOf(ulong key)
	{
		var slot = Probe(key, Volatile.Read(ref _slotsBase), out var slots);
		return slots[slot].Key == 0 ? -1 : Volatile.Read(ref slots[slot].Head);
	}

	internal static int PairBound(ulong key)
	{
		if (_sealed == 0) return PointerBound;
		var head = HeadOf(key);
		return head < 0 ? 0 : ChainLength(head) * SlotRow;
	}

	internal static ulong DigestOf(ulong key)
	{
		while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0) Thread.Yield();
		try
		{
			var consumers = ConsumerAt;
			var names = (byte*)_namesBase;
			var hash = Keying.Seed;
			var length = 0;
			for (var entry = HeadOf(key); entry >= 0; entry = consumers[entry].Next)
			{
				length++;
				var name = names + consumers[entry].NameOffset;
				var nameLength = name[0] | (name[1] << 8);
				for (var i = 0; i < nameLength; i++) hash = (hash ^ name[2 + i]) * Keying.Prime;
				hash = (hash ^ consumers[entry].DispatchOnly) * Keying.Prime;
				var feed = consumers[entry].Feed;
				hash = (hash ^ (byte)feed) * Keying.Prime;
				hash = (hash ^ (byte)(feed >> 8)) * Keying.Prime;
				hash = (hash ^ (byte)(feed >> 16)) * Keying.Prime;
				hash = (hash ^ (byte)(feed >> 24)) * Keying.Prime;
			}
			hash = (hash ^ (byte)length) * Keying.Prime;
			hash = (hash ^ (byte)(length >> 8)) * Keying.Prime;
			return hash | 1;
		}
		finally
		{
			_gate = 0;
		}
	}

	static int Probe(ulong key, ulong block, out Slot* slots)
	{
		slots = (Slot*)(block + 8ul);
		var cap = *(int*)block;
		var slot = (int)key & (cap - 1);
		while (true)
		{
			var candidate = Volatile.Read(ref slots[slot].Key);
			if (candidate == 0 || candidate == key) return slot;
			slot = (slot + 1) & (cap - 1);
		}
	}

	internal static void BindPair(TimelineRef asset, ulong pairKey, ulong* keys, int keyCount, byte* indices, int* rSlots, int* rCols, ref int rCount)
	{
		var consumers = ConsumerAt;
		var total = ConsumerCount;
		for (var entry = 0; entry < total; entry++)
			if (consumers[entry].DispatchOnly == 0 && consumers[entry].Bind != null && consumers[entry].PairKey == pairKey && asset.Uses(pairKey))
			{
				var offset = consumers[entry].Offset;
				consumers[entry].Bind(keys, keyCount, indices + offset);
				for (var k = 0; k < SlotRow; k++)
				{
					var col = indices[offset + k];
					if (col != 0) { rSlots[rCount] = offset + k; rCols[rCount++] = col - 1; }
				}
			}
	}
}

