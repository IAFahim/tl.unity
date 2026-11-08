namespace Tl;

public struct TlStateHash
{
    public const ulong Seed = 14695981039346656037UL, Prime = 1099511628211UL;
    public ulong Value;

    public static TlStateHash Begin() => new() { Value = Seed };

    public void Word(uint word) => Value = (Value ^ word) * Prime;

    public void Lane(ulong lane) => Value = (Value ^ lane) * Prime;

    public void Int(int value) => Word((uint)value);

    public unsafe void Float(float value) => Word(*(uint*)&value);

    public unsafe void FloatLane(float value) => Lane((ulong)(long)*(int*)&value);

    public unsafe void Bytes(byte* data, int length)
    {
        var i = 0;
        for (; i + 4 <= length; i += 4) Word(*(uint*)(data + i));
        if (i == length) return;
        uint tail = 0;
        for (var k = 0; i + k < length; k++) tail |= (uint)data[i + k] << (8 * k);
        Word(tail);
    }

    public unsafe void Column<T>(in T value) where T : unmanaged
    {
        fixed (T* p = &value) Bytes((byte*)p, sizeof(T));
    }

    public unsafe void Slots(Playback* slots, int count)
    {
        Word((uint)count);
        Bytes((byte*)slots, count * sizeof(Playback));
    }
}
