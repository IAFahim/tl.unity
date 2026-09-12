using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Tl;

public static unsafe class TimelineKernels
{
	static readonly ulong[] Hashes = new ulong[256];
	static readonly delegate*<byte*, int*, void**, TimelineComponent*, int, uint, int, void>[] Entries = new delegate*<byte*, int*, void**, TimelineComponent*, int, uint, int, void>[64];
	static int _count;

	public static int Bound;
	public static unsafe void Register(ulong hash0, ulong hash1, ulong hash2, ulong hash3, delegate*<byte*, int*, void**, TimelineComponent*, int, uint, int, void> tick)
	{
		if (_count == Hashes.Length) throw new InvalidOperationException("Kernel catalog capacity exhausted.");
		TimelineQuery.Find = &Find;
		(Hashes[_count], Hashes[_count + 1], Hashes[_count + 2], Hashes[_count + 3]) = (hash0, hash1, hash2, hash3);
		Entries[_count >> 2] = tick;
		_count += 4;
	}

	internal static unsafe void* Find(byte* block, uint bytes)
	{
		if (_count == 0) return default;
		Span<byte> hash = stackalloc byte[32];
		SHA256.TryHashData(new ReadOnlySpan<byte>(block, (int)bytes), hash, out _);
		for (var i = 0; i < _count; i += 4)
		{
			var match = true;
			for (var w = 0; w < 4; w++) match &= Hashes[i + w] == BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(8 * w));
			if (!match) continue;
			Bound++;
			return Entries[i >> 2];
		}
		return default;
	}

	public static unsafe void Chain(int head, bool reverse, byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
	{
		int* chain = stackalloc int[64];
		var n = 0;
		for (var e = head; e >= 0; e = PairTable.ConsumerAt[e].Next) chain[n++] = e;
		for (var i = 0; i < n; i++)
		{
			var entry = chain[reverse ? n - 1 - i : i];
			PairTable.ConsumerAt[entry].Execute(slot, gameTick, tick, cycle, flags, columns + PairTable.ConsumerAt[entry].Offset, row);
		}
	}
}
