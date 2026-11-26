namespace Tl;

public sealed unsafe class PlaybackImage : IDisposable
{
    byte* _data;
    long _capacity, _at;
    internal bool Saving;

    public long Length { get; private set; }

    internal void Begin(bool saving)
    {
        if (!saving && Length == 0) throw new InvalidOperationException("The image holds no saved table.");
        Saving = saving;
        _at = 0;
    }

    internal void Io(void* p, long bytes)
    {
        if (bytes <= 0) return;
        if (Saving && _at + bytes > _capacity)
        {
            var capacity = Math.Max(_at + bytes, _capacity * 2);
            var data = (byte*)NativeMemory.AlignedAlloc((nuint)capacity, 64);
            if (_data != null) Buffer.MemoryCopy(_data, data, capacity, _at);
            NativeMemory.AlignedFree(_data);
            _data = data;
            _capacity = capacity;
        }
        if (Saving) Buffer.MemoryCopy(p, _data + _at, bytes, bytes);
        else Buffer.MemoryCopy(_data + _at, p, bytes, bytes);
        _at += bytes;
    }

    internal T Of<T>(T value) where T : unmanaged
    {
        Io(&value, sizeof(T));
        return value;
    }

    internal void Same(long value)
    {
        if (Of(value) != value) throw new InvalidOperationException("The image was saved from a table of another shape or library.");
    }

    internal void End()
    {
        if (Saving) Length = _at;
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(_data);
        _data = null;
        _capacity = Length = 0;
        GC.SuppressFinalize(this);
    }

    ~PlaybackImage() => NativeMemory.AlignedFree(_data);
}
