using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
namespace Tl;
public sealed unsafe class TlWorkers : IDisposable
{
    readonly int _w;
    readonly Thread[]? _threads;
    readonly SemaphoreSlim _gate = new(0);
    readonly ManualResetEventSlim _done = new(false);
    delegate*<void*, int, int, int, void> _body;
    void* _state;
    int* _ends;
    int _chunks;
    int _next;
    int _d;
    Exception? _error;
    bool _disposed;
    public TlWorkers(int workers)
    {
        if (workers < 1) throw new ArgumentOutOfRangeException(nameof(workers));
        _w = workers;
        if (workers > 1)
        {
            _threads = new Thread[workers - 1];
            for (var i = 0; i < _threads.Length; i++) { var t = new Thread(WorkerLoop) { IsBackground = true, Name = "TlWorkers-" + i }; _threads[i] = t; t.Start(); }
        }
    }
    public int WorkerCount => _w;
    public static IntPtr Pin(object o) => GCHandle.ToIntPtr(GCHandle.Alloc(o));
    public static object? Target(IntPtr h) => GCHandle.FromIntPtr(h).Target;
    public static void Free(IntPtr h) => GCHandle.FromIntPtr(h).Free();
    public static void ClampRange(int n, int first, int last, out int firstEntity, out int endEntity)
    {
        var words = (n + 63) >> 6;
        firstEntity = first <= 0 ? 0 : first >= words ? n : first * 64;
        endEntity = last >= words ? n : last <= 0 ? 0 : last * 64;
        if (endEntity < firstEntity) endEntity = firstEntity;
    }
    public static void Split(ulong* bits, int words, int* ends, int chunks)
    {
        if (bits == null) throw new ArgumentNullException(nameof(bits));
        if (ends == null) throw new ArgumentNullException(nameof(ends));
        if (words < 0) throw new ArgumentOutOfRangeException(nameof(words));
        if (chunks <= 0) throw new ArgumentOutOfRangeException(nameof(chunks));
        var total = 0;
        for (var w = 0; w < words; w++) total += KindStore.Pop(bits[w]);
        var target = Math.Max(1, (total + chunks - 1) / chunks);
        var have = 0;
        var chunk = 0;
        for (var w = 0; w < words && chunk < chunks - 1; w++)
        {
            have += KindStore.Pop(bits[w]);
            if (have >= target) { ends[chunk++] = w + 1; have = 0; }
        }
        for (; chunk < chunks - 1; chunk++) ends[chunk] = words;
        ends[chunks - 1] = words;
    }
    public void Run(delegate*<void*, int, int, int, void> body, void* state, int* ends, int chunks)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TlWorkers));
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (chunks < 0) throw new ArgumentOutOfRangeException(nameof(chunks));
        if (chunks > 0 && ends == null) throw new ArgumentNullException(nameof(ends));
        if (chunks == 0) return;
        if (_w == 1) { for (var c = 0; c < chunks; c++) body(state, c == 0 ? 0 : ends[c - 1], ends[c], c); return; }
        _next = 0; _d = 0; _error = null; _body = body; _state = state; _ends = ends; _chunks = chunks; _done.Reset(); _gate.Release(_w - 1);
        while (true)
        {
            var c = Interlocked.Increment(ref _next) - 1;
            if (c >= chunks) break;
            try { body(state, c == 0 ? 0 : ends[c - 1], ends[c], c); }
            catch (Exception ex) { Interlocked.CompareExchange(ref _error, ex, null); }
        }
        _done.Wait();
        if (_error != null) ExceptionDispatchInfo.Capture(_error).Throw();
    }
    void WorkerLoop()
    {
        while (true)
        {
            var got = false;
            var spin = new SpinWait();
            for (var i = 0; i < 200 && !got; i++) { if (_disposed) return; if (_gate.Wait(0)) got = true; else spin.SpinOnce(); }
            if (!got) { try { _gate.Wait(); } catch (ObjectDisposedException) { return; } }
            if (_disposed) return;
            var body = _body;
            var state = _state;
            var ends = _ends;
            var n = _chunks;
            while (true)
            {
                var c = Interlocked.Increment(ref _next) - 1;
                if (c >= n) break;
                try { body(state, c == 0 ? 0 : ends[c - 1], ends[c], c); }
                catch (Exception ex) { Interlocked.CompareExchange(ref _error, ex, null); }
            }
            if (Interlocked.Increment(ref _d) == _w - 1) _done.Set();
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_w > 1 && _threads != null) { try { _gate.Release(_w - 1); } catch (SemaphoreFullException) { } foreach (var t in _threads) t.Join(); }
        _gate.Dispose();
        _done.Dispose();
    }
}
