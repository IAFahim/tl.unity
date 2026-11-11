using System.Collections.Concurrent;
using System.Collections.Generic;
namespace Tl;

public interface ITlModule
{
    int PairCount { get; }
    ulong PairKey(int index);
    int FoldLaneBytes(int index);
    unsafe void FoldStep(int index, byte* track, byte* first, byte* second, float blend, ushort tick, ushort windowLength, ushort withinClip, ushort trackIndex, ushort duration, sbyte direction, byte* lane);
}

public static class TlGenerated
{
    static readonly List<ITlModule> _modules = new();
    static readonly object _gate = new();
    internal static int Version;

    public static void Install(ITlModule module)
    {
        if (module is null) throw new ArgumentNullException(nameof(module));
        lock (_gate)
        {
            _modules.Add(module);
            Version++;
        }
    }

    public static IReadOnlyList<ITlModule> Modules
    {
        get { lock (_gate) return _modules.ToArray(); }
    }
}

sealed class SystemRegistry
{
    internal readonly struct Entry
    {
        public Entry(ITlModule module, int index)
        {
            Module = module;
            Index = index;
        }

        public readonly ITlModule Module;
        public readonly int Index;
    }
    internal nint ViewStamp;
    internal uint ViewBytes;
    internal int ViewCount;
    internal ulong ViewGeneration;
    internal int ModuleVersion;
    internal readonly Dictionary<ulong, SystemSteps> Steps = new();

    internal void Reset(nint stamp, uint bytes, int count, ulong generation)
    {
        ViewStamp = stamp;
        ViewBytes = bytes;
        ViewCount = count;
        ViewGeneration = generation;
        FreeSteps();
    }

    internal void FreeSteps()
    {
        foreach (var built in Steps.Values) SystemDispatch.Free(built);
        Steps.Clear();
    }

    internal void RefreshModules()
    {
        var version = Volatile.Read(ref TlGenerated.Version);
        if (version == ModuleVersion) return;
        ModuleVersion = version;
        foreach (var module in TlGenerated.Modules)
            for (var i = 0; i < module.PairCount; i++)
                _entries.TryAdd(module.PairKey(i), new Entry(module, i));
    }

    readonly ConcurrentDictionary<ulong, Entry> _entries = new();

    internal SystemRegistry()
    {
        RefreshModules();
    }

    internal bool TryGet(ulong key, out Entry entry) => _entries.TryGetValue(key, out entry);
}
