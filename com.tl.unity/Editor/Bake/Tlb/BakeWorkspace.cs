using System.Collections.Concurrent;

namespace Tl.Gen.Tlb;

internal sealed class BakeWorkspace
{
    internal static readonly BakeWorkspace Shared = new();

#if NET
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private (ulong[] Structural, ulong[] Quotes)? _masks;
    private readonly Dictionary<ulong, byte[]> _pairPools = [];
    private readonly ConcurrentDictionary<(string Ns, string Type, string? Asm), Type> _typeCache = new();

    internal (ulong[] Structural, ulong[] Quotes)? RentMasks(int blocks)
    {
        lock (_gate)
        {
            if (_masks is { } masks && masks.Structural.Length >= blocks)
            {
                _masks = null;
                return masks;
            }
            return null;
        }
    }

    internal void ReturnMasks(ulong[] structural, ulong[] quotes)
    {
        lock (_gate)
        {
            if (_masks is not { } current || current.Structural.Length < structural.Length)
                _masks = (structural, quotes);
        }
    }

    internal byte[]? RentPool(ulong key)
    {
        lock (_gate)
        {
            if (_pairPools.Remove(key, out var pool))
            {
                return pool;
            }
            return null;
        }
    }

    internal void ReturnPool(ulong key, byte[] pool)
    {
        lock (_gate)
        {
            if (_pairPools.TryGetValue(key, out var current))
            {
                if (current.Length < pool.Length)
                    _pairPools[key] = pool;
            }
            else
            {
                _pairPools[key] = pool;
            }
        }
    }

    internal void Warm(FastDoc doc) =>
        doc.ResolveCache = new Dictionary<(string, string, string?), Type>(_typeCache);

    internal void Reclaim(FastDoc doc)
    {
        foreach (var pair in doc.Pairs)
            if (pair.Pool.Length > 0)
                ReturnPool(pair.Key, pair.Pool);
        if (doc is { LoanStructural: not null, LoanQuotes: not null })
            ReturnMasks(doc.LoanStructural, doc.LoanQuotes);
        foreach (var entry in doc.ResolveCache)
            _typeCache.TryAdd(entry.Key, entry.Value);
        doc.LoanStructural = null;
        doc.LoanQuotes = null;
    }
}
