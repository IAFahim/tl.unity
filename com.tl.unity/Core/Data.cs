using System.Buffers.Binary;

namespace Tl;

static class Keying
{
	internal const ulong Seed = 14695981039346656037, Prime = 1099511628211;

	internal static ulong Of(string text) { var hash = Seed; foreach (var c in text) hash = (hash ^ c) * Prime; return hash | 1; }

	internal static string StableName(Type type)
	{
		var prefix = type.IsNested ? StableName(type.DeclaringType!) + "+" : type.Namespace is { } space ? space + "." : "";
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
}
public readonly unsafe struct TimelineRef
{
	static uint U32(ReadOnlySpan<byte> s, int o) => LibraryLayout.U32(s, o);
	static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o));
	static void Fail(string message) => throw new ArgumentException(message);

	internal static void Validate(ReadOnlySpan<byte> baked)
	{
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
			next = U32(baked, 52);
			channel = U32(baked, 56);
			flags = U32(baked, 60);
			markerOff = U32(baked, 64);
			markerCount = U32(baked, 68);
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
		for (var i = 1; i < pairs.Length; i++) if (pairs[i - 1].Key >= pairs[i].Key) Fail("TLB pair keys must be sorted.");
		for (var i = 0; i < pairs.Length; i++)
		{
			var pair = pairs[i];
			if (pair.SlotStride % 8 != 0 || pair.SlotStride < SlotRow.RowBytes) Fail("TLB slot stride must be 8-aligned.");
			ValidatePool(h, h.PairOffset + 48ul * (uint)i + pair.TrackPoolOffset, pair.TrackPoolCount, pair.TrackValueBytes);
			ValidatePool(h, h.PairOffset + 48ul * (uint)i + pair.ClipPoolOffset, pair.ClipPoolCount, pair.ClipValueBytes);
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
				ValidateRow(baked, (int)step.Slot, pair, h.TrackCount, stage.Start, stage.End);
			}
			edge = stage.End;
		}
		if (edge != h.Duration) Fail("TLB stages must cover duration.");
	}

	static void ValidateMarkers(ReadOnlySpan<byte> baked, int at, int count, uint duration, uint hotLength)
	{
		var off = (uint)at;
		for (var i = 0; i < count; i++)
		{
			if (off + 16u > hotLength) Fail("TLB marker entry out of bounds.");
			var tick = U32(baked, (int)off);
			var size = U32(baked, (int)off + 4);
			var key = BinaryPrimitives.ReadUInt64LittleEndian(baked.Slice((int)off + 8));
			if (key == 0) Fail("TLB marker type key invalid.");
			if (size == 0 || size > 4096) Fail("TLB marker payload size out of range.");
			if (tick >= duration && duration != 0) Fail("TLB marker tick outside duration.");
			off += 16u + ((size + 3u) & ~3u);
			if (off > hotLength) Fail("TLB marker payload out of bounds.");
		}
		if (off > hotLength) Fail("TLB marker section out of bounds.");
	}

	static void ValidatePool(NativeHeader h, ulong address, uint count, uint valueBytes)
	{
		if (address % 16 != 0) Fail("TLB value pools must be 16-byte aligned.");
		if (address < h.PoolOffset || count > 65535 || address + (ulong)count * valueBytes > h.FrameOffset) Fail("TLB value pool out of bounds.");
	}

	static void ValidateRow(ReadOnlySpan<byte> baked, int at, NativePair pair, uint trackCount, uint stageStart, uint stageEnd)
	{
		var trackValue = U16(baked, at);
		var first = U16(baked, at + 2);
		var second = U16(baked, at + 4);
		var trackIndex = baked[at + 6];
		var windowStart = U32(baked, at + 8);
		var windowEnd = U32(baked, at + 12);
		var factorStart = U32(baked, at + 16);
		var factorSpan = U32(baked, at + 20);
		if (trackValue >= pair.TrackPoolCount || first >= pair.ClipPoolCount) Fail("TLB slot value index out of bounds.");
		if (second != SlotRow.NoClipValue && second >= pair.ClipPoolCount) Fail("TLB slot second value index out of bounds.");
		if (factorSpan != 0 && second == SlotRow.NoClipValue) Fail("TLB blend window requires a second value index.");
		if (factorSpan == 0 && second != SlotRow.NoClipValue) Fail("TLB second value index requires a blend window.");
		if (trackIndex >= trackCount) Fail("TLB slot track index out of bounds.");
		if (stageStart < windowStart || stageEnd > windowEnd) Fail("TLB slot window must contain the referencing stage.");
		if (factorSpan != 0 && (stageStart < factorStart || stageEnd > factorStart + (ulong)factorSpan)) Fail("TLB blend window must contain the referencing stage.");
	}
}
