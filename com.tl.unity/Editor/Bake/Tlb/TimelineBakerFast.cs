using System.Buffers.Binary;
using System.Numerics;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Tl.Gen.Tlb;

internal interface IPairHelper
{
    ulong Key { get; }
    int ClipSize { get; }
    int TrackSize { get; }
    unsafe void CopyPayload(byte* dst, object box);
}

internal sealed class PairHelper<TTrack, TClip> : IPairHelper
    where TTrack : unmanaged, IBlend<TClip>
    where TClip : unmanaged
{
    public ulong Key => Keying.Of(Keying.StableName(typeof(TTrack)) + "\0" + Keying.StableName(typeof(TClip)));
    public int ClipSize => Unsafe.SizeOf<TClip>();
    public int TrackSize => Unsafe.SizeOf<TTrack>();
    public unsafe void CopyPayload(byte* dst, object box) => *(TClip*)dst = (TClip)box;
}

internal interface ITypeHelper
{
    int Size { get; }
    unsafe void CopyTo(byte* dst, object box);
    unsafe object Read(byte* source);
}

internal sealed class TypeHelper<T> : ITypeHelper
    where T : unmanaged
{
    public int Size => Unsafe.SizeOf<T>();
    public unsafe void CopyTo(byte* dst, object box) => *(T*)dst = (T)box;
    public unsafe object Read(byte* source) => *(T*)source;
}

internal sealed class FieldEntry
{
    internal string Name = "";
    internal string TypeName = "";
    internal FieldKind Kind;
    internal FieldInfo Field = null!;
    internal Type Declaring = null!;
    internal int FloatOrdinal = -1;
    internal int Ordinal = -1;
    internal int ByteOffset = -1;
}

internal enum FieldKind : byte
{
    Bool, Byte, SByte, Short, UShort, Int, UInt, Long, ULong, Float, Double, Enum, TimelineId, Struct, Unsupported,
}

internal unsafe delegate void FloatApplier(object box, int* ids, float* vals, int count);

internal sealed class FieldTable
{
    internal readonly Dictionary<string, FieldEntry> ByName = new(StringComparer.Ordinal);
    private int _bucketMask;
    private int[] _buckets = [-1];
    private ulong[] _entryHashes = [];
    private byte[][] _entryNames = [];
    internal FieldEntry[] Entries = [];
    internal FloatApplier? FloatApply;

    internal FieldEntry? Lookup(ReadOnlySpan<byte> name)
    {
        var h = HashOf(name);
        var i = (int)(h & (uint)_bucketMask);
        while (true)
        {
            var b = _buckets[i];
            if (b < 0)
                return null;
            if (_entryHashes[b] == h && _entryNames[b].AsSpan().SequenceEqual(name))
                return Entries[b];
            i = (i + 1) & _bucketMask;
        }
    }

    internal static ulong HashOf(ReadOnlySpan<byte> bytes)
    {
        ulong h = 14695981039346656037;
        foreach (var b in bytes)
            h = (h ^ b) * 1099511628211;
        return h;
    }

    internal void Freeze()
    {
        var list = ByName.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Entries = list.ToArray();
        for (var i = 0; i < Entries.Length; i++)
            Entries[i].Ordinal = i;
        _entryHashes = new ulong[Entries.Length];
        _entryNames = new byte[Entries.Length][];
        var cap = 16;
        while (cap < Entries.Length * 2)
            cap <<= 1;
        _bucketMask = cap - 1;
        _buckets = new int[cap];
        Array.Fill(_buckets, -1);
        for (var e = 0; e < Entries.Length; e++)
        {
            var nameBytes = Encoding.UTF8.GetBytes(Entries[e].Name);
            _entryNames[e] = nameBytes;
            _entryHashes[e] = HashOf(nameBytes);
            var i = (int)(_entryHashes[e] & (uint)_bucketMask);
            while (_buckets[i] >= 0)
                i = (i + 1) & _bucketMask;
            _buckets[i] = e;
        }
        var floatCount = 0;
        foreach (var e in Entries)
            if (e.Kind == FieldKind.Float)
            {
                e.FloatOrdinal = floatCount++;
            }
        FloatApply = floatCount > 0 ? BuildFloatApplier(Entries.Where(e => e.Kind == FieldKind.Float).ToList(), Entries[0].Declaring) : null;
    }

    private static FloatApplier BuildFloatApplier(List<FieldEntry> floatEntries, Type declaring)
    {
    var method = new DynamicMethod(
        "apply_floats_" + declaring.Name,
        typeof(void),
        [typeof(object), typeof(int*), typeof(float*), typeof(int)],
        declaring.Module,
        skipVisibility: true);
    var il = method.GetILGenerator();
    var i = il.DeclareLocal(typeof(int));
    var loop = il.DefineLabel();
    var cond = il.DefineLabel();
    var next = il.DefineLabel();
    var cases = new Label[floatEntries.Count];
    for (var k = 0; k < cases.Length; k++)
        cases[k] = il.DefineLabel();
    var def = il.DefineLabel();
    il.Emit(OpCodes.Ldc_I4_0);
    il.Emit(OpCodes.Stloc, i);
    il.Emit(OpCodes.Br, cond);
    il.MarkLabel(loop);
    il.Emit(OpCodes.Ldarg_1);
    il.Emit(OpCodes.Ldloc, i);
    il.Emit(OpCodes.Conv_I);
    il.Emit(OpCodes.Ldc_I4_4);
    il.Emit(OpCodes.Mul);
    il.Emit(OpCodes.Add);
    il.Emit(OpCodes.Ldobj, typeof(int));
    il.Emit(OpCodes.Switch, [.. cases, def]);
    for (var k = 0; k < floatEntries.Count; k++)
    {
        il.MarkLabel(cases[k]);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox, declaring);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldobj, typeof(float));
        il.Emit(OpCodes.Stfld, floatEntries[k].Field);
        il.Emit(OpCodes.Br, next);
    }
    il.MarkLabel(def);
    il.MarkLabel(next);
    il.Emit(OpCodes.Ldloc, i);
    il.Emit(OpCodes.Ldc_I4_1);
    il.Emit(OpCodes.Add);
    il.Emit(OpCodes.Stloc, i);
    il.MarkLabel(cond);
    il.Emit(OpCodes.Ldloc, i);
    il.Emit(OpCodes.Ldarg_3);
    il.Emit(OpCodes.Blt, loop);
    il.Emit(OpCodes.Ret);
    return (FloatApplier)method.CreateDelegate(typeof(FloatApplier));
    }

}

internal sealed class FastPairInfo
{
    internal Type TrackType = null!;
    internal Type ClipType = null!;
    internal ulong Key;
    internal int ClipSize;
    internal int TrackSize;
    internal IPairHelper Helper = null!;
    internal FieldTable Fields = null!;
    internal byte[] Pool = [];
    internal int PoolLength;

    internal void EnsurePool(int size)
    {
        if (PoolLength + size <= Pool.Length)
            return;
        var cap = Pool.Length == 0 ? 1 << 16 : Pool.Length;
        while (cap < PoolLength + size)
            cap *= 2;
        Array.Resize(ref Pool, cap);
    }
}

internal sealed class FastTrackInfo
{
    internal string? Name;
    internal string Ns = "";
    internal string TypeName = "";
    internal string? Asm;
    internal bool HaveNs, HaveType;
    internal Type? TrackType;
    internal bool TrackTypeFailed;
    internal bool ResolveNeedsRefresh;
    internal string? InferError;
    internal byte[]? TrackBytes;
    internal bool PopulateDone;
    internal int DataStart = -1, DataEnd = -1;
    internal bool DataCaptured;
    internal readonly List<int> ClipIds = new();
    internal string? ClipArrayError;
}

internal sealed class FastClip
{
    internal uint Start, End;
    internal int ClipIndex;
    internal int AuthoredIndex;
    internal string? Name;
    internal string Ns = "";
    internal string TypeName = "";
    internal string? Asm;
    internal bool NsSet;
    internal bool TypeSet;
    internal bool ResolveNeedsRefresh;
    internal string? InferError;
    internal int PairId = -1;
    internal Type? ClipType;
    internal bool ClipTypeFailed;
    internal int PayloadOffset = -1;
    internal string? PopulateError;
    internal int DataStart = -1, DataEnd = -1;
    internal bool DataCaptured;
    internal bool PopulateDone;
}

public sealed class LibraryManifest
{
    internal Dictionary<string, ushort> Timelines = new(StringComparer.Ordinal);
    internal Dictionary<string, ushort> Channels = new(StringComparer.Ordinal);
    internal uint TickRate = 60u;
}
internal sealed class FastMarker
{
    internal string Ns = "";
    internal string TypeName = "";
    internal string? Asm;
    internal uint Tick;
    internal Type? MarkerType;
    internal int DataStart = -1, DataEnd = -1;
    internal bool DataCaptured;
    internal byte[] Payload = [];
    internal ulong Key;
}
internal sealed class FastDoc
{
    internal BakerAssemblyResolver Resolver = null!;
    internal string? RootName;
    internal uint Duration;
    internal bool Loops;
    internal byte[] Utf8 = null!;
    internal BakeWorkspace? Workspace;
    internal ulong[]? LoanStructural;
    internal ulong[]? LoanQuotes;
    internal readonly List<FastTrackInfo> Tracks = new();
    internal readonly List<FastClip> Clips = new();
    internal readonly List<FastPairInfo> Pairs = new();
    internal Dictionary<(string, string, string?), Type> ResolveCache = new();
    internal readonly Dictionary<(Type, Type), int> PairIds = new();
    internal string? NextName;
    internal bool Hold;
    internal string? ChannelName;
    internal readonly List<FastMarker> Markers = new();
    internal LibraryManifest? Manifest;
    internal uint NextId;
    internal uint ChannelId;
}
internal static class TimelineBakerFast
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldTable> FieldTables = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> UnmanagedCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, Type), bool> BlendCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ITypeHelper> TypeHelpers = new();

    internal static byte[] BakeJson(string json, BakerAssemblyResolver? resolver = null, bool autoNamespace = false, LibraryManifest? manifest = null)
    {
        resolver ??= new BakerAssemblyResolver();
        return BakeJsonUtf8(Encoding.UTF8.GetBytes(json), resolver, null, autoNamespace, manifest);
    }

    internal static byte[] BakeJsonUtf8(byte[] utf8, BakerAssemblyResolver? resolver = null, BakeWorkspace? workspace = null, bool autoNamespace = false, LibraryManifest? manifest = null)
    {
        resolver ??= new BakerAssemblyResolver();
        var doc = TimelineBakerSimd.TryParseFast(utf8, resolver, workspace, out var fast, autoNamespace) ? fast : ParseFast(utf8, resolver, workspace, autoNamespace, manifest);
        if (manifest != null) doc.Manifest = manifest;
        try
        {
            return TimelineBakerFastCore.BakeFast(doc, resolver);
        }
        finally
        {
            workspace?.Reclaim(doc);
        }
    }

    internal static FastDoc ParseFast(byte[] utf8, BakerAssemblyResolver resolver, BakeWorkspace? workspace = null, bool autoNamespace = false, LibraryManifest? manifest = null)
    {
        var walker = new Walker(utf8, resolver, workspace, autoNamespace, manifest);
        walker.Run();
        return walker.Finish();
    }

    internal static FieldTable TableFor(Type structType) => FieldTables.GetOrAdd(structType, BuildFieldTable);

    internal static ITypeHelper HelperFor(Type t) => TypeHelpers.GetOrAdd(t, x =>
        (ITypeHelper)Activator.CreateInstance(typeof(TypeHelper<>).MakeGenericType(x))!);

    internal static bool IsUnmanagedCached(Type t) => UnmanagedCache.GetOrAdd(t, BakerAssemblyResolver.IsUnmanaged);

    internal static bool IsBlendPairCached(Type trackType, Type clipType) => BlendCache.GetOrAdd((trackType, clipType), IsBlendPair);

    private static bool IsBlendPair((Type Track, Type Clip) key)
    {
        foreach (var iface in key.Track.GetInterfaces())
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IBlend<>) && iface.GetGenericArguments()[0] == key.Clip)
                return true;
        return false;
    }

    internal static int EnsurePair(FastDoc doc, Type trackType, Type clipType)
    {
        if (doc.PairIds.TryGetValue((trackType, clipType), out var pairId))
            return pairId;
        if (!IsUnmanagedCached(trackType) || !IsUnmanagedCached(clipType) || !IsBlendPairCached(trackType, clipType))
            return -1;
        var helper = (IPairHelper)Activator.CreateInstance(typeof(PairHelper<,>).MakeGenericType(trackType, clipType))!;
        var pair = new FastPairInfo
        {
            TrackType = trackType,
            ClipType = clipType,
            Key = helper.Key,
            ClipSize = helper.ClipSize,
            TrackSize = helper.TrackSize,
            Helper = helper,
            Fields = TableFor(clipType),
        };
        if (doc.Workspace?.RentPool(pair.Key) is { } rented)
            pair.Pool = rented;
        pairId = doc.Pairs.Count;
        doc.Pairs.Add(pair);
        doc.PairIds[(trackType, clipType)] = pairId;
        return pairId;
    }

    private static FieldTable BuildFieldTable(Type structType)
    {
        var table = new FieldTable();
        var allFields = structType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var f in allFields)
        {
            var name = f.Name;
            if (name.StartsWith("<") && name.Contains(">k__BackingField"))
                name = name.Substring(1, name.IndexOf('>') - 1);
            var kind = KindOf(f.FieldType);
            table.ByName[name] = new FieldEntry { Name = name, TypeName = f.FieldType.Name, Kind = kind, Field = f, Declaring = structType };
        }
        table.Freeze();
        return table;
    }

    private static FieldKind KindOf(Type t)
    {
        if (t == typeof(bool)) return FieldKind.Bool;
        if (t == typeof(byte)) return FieldKind.Byte;
        if (t == typeof(sbyte)) return FieldKind.SByte;
        if (t == typeof(short)) return FieldKind.Short;
        if (t == typeof(ushort)) return FieldKind.UShort;
        if (t == typeof(int)) return FieldKind.Int;
        if (t == typeof(uint)) return FieldKind.UInt;
        if (t == typeof(long)) return FieldKind.Long;
        if (t == typeof(ulong)) return FieldKind.ULong;
        if (t == typeof(float)) return FieldKind.Float;
        if (t == typeof(double)) return FieldKind.Double;
        if (t == typeof(Tl.TimelineId)) return FieldKind.TimelineId;
        if (t.IsEnum) return FieldKind.Enum;
        if (t.IsValueType && IsUnmanagedCached(t)) return FieldKind.Struct;
        return FieldKind.Unsupported;
    }

    internal static string KindNameOfValue(JsonTokenType t) => t switch
    {
        JsonTokenType.StartObject or JsonTokenType.EndObject => "Object",
        JsonTokenType.StartArray or JsonTokenType.EndArray => "Array",
        JsonTokenType.String => "String",
        JsonTokenType.Number => "Number",
        JsonTokenType.True => "True",
        JsonTokenType.False => "False",
        JsonTokenType.Null => "Null",
        _ => t.ToString(),
    };

    internal static string? PopulateSliceUtf8(byte[] utf8, int dataStart, int dataEnd, FieldTable table, object box, Type structType, string contextName, LibraryManifest? manifest = null)
    {
        var slice = utf8.AsSpan(dataStart, dataEnd - dataStart);
        var sub = new Utf8JsonReader(slice);
        if (!sub.Read() || sub.TokenType != JsonTokenType.StartObject)
            return $"wrong-typed value for {contextName}: expected object, got {KindNameOfValue(sub.TokenType)}.";
        return PopulateObject(ref sub, table, box, structType, contextName, utf8, dataStart, checkDup: false, null, manifest);
    }

    internal static unsafe string? PopulateObject(ref Utf8JsonReader r, FieldTable table, object box, Type structType, string contextName, byte[] utf8, int baseOffset, bool checkDup, List<DupScope>? scopes, LibraryManifest? manifest = null)
    {
        if (checkDup)
            scopes!.Add(DupScopePool.Rent());
        try
        {
            string? error = null;
            Span<int> ids = stackalloc int[32];
            Span<float> vals = stackalloc float[32];
            var n = 0;
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject)
                    break;
                if (checkDup)
                    DupCheck.Do(ref r, scopes!, utf8, baseOffset);
                var nameSpan = r.ValueSpan;
                FieldEntry? entry;
                string? decodedName = null;
                if (!r.HasValueSequence && !NameBytes.HasEscape(nameSpan))
                {
                    entry = table.Lookup(nameSpan);
                }
                else
                {
                    decodedName = r.GetString()!;
                    entry = table.ByName.GetValueOrDefault(decodedName);
                }
                r.Read();
                if (entry == null)
                {
                    error ??= $"unknown field name in track/payload: field '{decodedName ?? Encoding.UTF8.GetString(nameSpan)}' not found on type '{structType.Name}' in {contextName}.";
                    SkipValue(ref r, scopes, utf8, baseOffset);
                    continue;
                }
                if (error is null && entry is { Kind: FieldKind.Float, FloatOrdinal: >= 0 })
                {
                    if (r.TokenType == JsonTokenType.Number && r.TryGetSingle(out var v) && !float.IsInfinity(v) && !float.IsNaN(v))
                    {
                        if (n == ids.Length)
                        {
                            FlushFloats(table, box, ids, vals, n);
                            n = 0;
                        }
                        ids[n] = entry.FloatOrdinal;
                        vals[n] = v;
                        n++;
                    }
                    else
                    {
                        error = $"wrong-typed value: field '{entry.Name}' in {contextName} expected float, got {RawOf(ref r, utf8, baseOffset)}.";
                    }
                    continue;
                }
                var err = SetField(ref r, entry, box, contextName, utf8, baseOffset, scopes, manifest);
                if (err != null)
                    error ??= err;
            }
            if (error == null && n > 0)
                FlushFloats(table, box, ids, vals, n);
            return error;
        }
        finally
        {
            if (checkDup)
            {
                var s = scopes![^1];
                scopes.RemoveAt(scopes.Count - 1);
                s.Reset();
                DupScopePool.Return(s);
            }
        }
    }

    private static unsafe void FlushFloats(FieldTable table, object box, Span<int> ids, Span<float> vals, int n)
    {
        fixed (int* ip = ids)
        fixed (float* vp = vals)
            table.FloatApply!(box, ip, vp, n);
    }

    private static string RawOf(ref Utf8JsonReader r, byte[] utf8, int baseOffset)
    {
        var start = baseOffset + (int)r.TokenStartIndex;
        if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            r.TrySkip();
        var end = baseOffset + (int)r.BytesConsumed;
        return Encoding.UTF8.GetString(utf8, start, end - start);
    }

    private static void SkipValue(ref Utf8JsonReader r, List<DupScope>? scopes, byte[] utf8, int baseOffset)
    {
        if (scopes != null)
            SkipValueWithDupScanning(ref r, scopes, utf8, baseOffset);
        else if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            r.TrySkip();
    }

    internal static void SkipValueWithDupScanning(ref Utf8JsonReader r, List<DupScope> scopes, byte[] utf8, int baseOffset)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.StartObject:
                scopes.Add(DupScopePool.Rent());
                try
                {
                    while (r.Read())
                    {
                        if (r.TokenType == JsonTokenType.EndObject)
                            break;
                        DupCheck.Do(ref r, scopes, utf8, baseOffset);
                        r.Read();
                        SkipValueWithDupScanning(ref r, scopes, utf8, baseOffset);
                    }
                }
                finally
                {
                    var s = scopes[^1];
                    scopes.RemoveAt(scopes.Count - 1);
                    s.Reset();
                    DupScopePool.Return(s);
                }
                break;
            case JsonTokenType.StartArray:
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.EndArray)
                        break;
                    SkipValueWithDupScanning(ref r, scopes, utf8, baseOffset);
                }
                break;
        }
    }

    private static string? SetField(ref Utf8JsonReader r, FieldEntry entry, object box, string contextName, byte[] utf8, int baseOffset, List<DupScope>? scopes, LibraryManifest? manifest = null)
    {
        if (Scalar(ref r, entry, manifest) is { } value)
        {
            entry.Field.SetValue(box, value);
            return null;
        }
        using var document = JsonDocument.ParseValue(ref r);
        try
        {
            if (entry.Kind == FieldKind.Unsupported)
                return $"wrong-typed value: field '{entry.Name}' in {contextName} has unsupported primitive type '{entry.TypeName}'.";
            entry.Field.SetValue(box, ValueJson.ReadField(entry.Field, document.RootElement, contextName + "." + entry.Name, manifest));
            return null;
        }
        catch (BakeDiagnosticException error)
        {
            return error.Message;
        }
    }

    private static object? Scalar(ref Utf8JsonReader r, FieldEntry entry, LibraryManifest? manifest)
    {
        var number = r.TokenType == JsonTokenType.Number;
        switch (entry.Kind)
        {
            case FieldKind.Bool: return r.TokenType == JsonTokenType.True ? true : r.TokenType == JsonTokenType.False ? false : null;
            case FieldKind.Byte: return number && r.TryGetByte(out var b) ? b : null;
            case FieldKind.SByte: return number && r.TryGetSByte(out var sb) ? sb : null;
            case FieldKind.Short: return number && r.TryGetInt16(out var s) ? s : null;
            case FieldKind.UShort: return number && r.TryGetUInt16(out var us) ? us : null;
            case FieldKind.Int: return number && r.TryGetInt32(out var i) ? i : null;
            case FieldKind.UInt: return number && r.TryGetUInt32(out var ui) ? ui : null;
            case FieldKind.Long: return number && r.TryGetInt64(out var l) ? l : null;
            case FieldKind.ULong: return number && r.TryGetUInt64(out var ul) ? ul : null;
            case FieldKind.Float: return number && r.TryGetSingle(out var f) && !float.IsNaN(f) && !float.IsInfinity(f) ? f : null;
            case FieldKind.Double: return number && r.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;
            case FieldKind.Enum when r.TokenType == JsonTokenType.String:
                var text = r.GetString()!;
                return Enum.IsDefined(entry.Field.FieldType, text) ? Enum.Parse(entry.Field.FieldType, text, false) : null;
            case FieldKind.TimelineId when r.TokenType == JsonTokenType.String:
                var name = r.GetString()!;
                if (name.Length == 0) return default(Tl.TimelineId);
                return manifest != null && manifest.Timelines.TryGetValue(name, out var id) ? new Tl.TimelineId(id) : null;
            default: return null;
        }
    }

}

internal static class DupScopePool
{
    [ThreadStatic]
    private static List<DupScope>? _pool;

    internal static DupScope Rent()
    {
        var pool = _pool ??= [];
        if (pool.Count == 0)
            return new DupScope();
        var s = pool[^1];
        pool.RemoveAt(pool.Count - 1);
        return s;
    }

    internal static void Return(DupScope s) => (_pool ??= []).Add(s);
}

internal static class NameBytes
    {
        public static bool HasEscape(ReadOnlySpan<byte> span) => span.IndexOf((byte)'\\') >= 0;
    }

internal static class DupCheck
{
    internal static void Do(ref Utf8JsonReader r, List<DupScope> scopes, byte[] utf8, int baseOffset)
    {
        var scope = scopes[^1];
        var tok = baseOffset + (int)r.TokenStartIndex;
        if (!r.HasValueSequence && !NameBytes.HasEscape(r.ValueSpan))
        {
            var span = r.ValueSpan;
            if (scope.Find(span, utf8))
                throw new BakeDiagnosticException($"duplicate field: '{Encoding.UTF8.GetString(span)}'", LineOf(utf8, tok), ColOf(utf8, tok));
            scope.Add(span, tok + 1);
            if (scope.Decoded is { Count: > 0 })
            {
                var s = r.GetString()!;
                if (!scope.Decoded.Add(s))
                    throw new BakeDiagnosticException($"duplicate field: '{s}'", LineOf(utf8, tok), ColOf(utf8, tok));
            }
        }
        else
        {
            var s = r.GetString()!;
            scope.Decoded ??= new HashSet<string>(StringComparer.Ordinal);
            if (!scope.Decoded.Add(s))
                throw new BakeDiagnosticException($"duplicate field: '{s}'", LineOf(utf8, tok), ColOf(utf8, tok));
            var maxLen = Encoding.UTF8.GetMaxByteCount(s.Length);
            var buf = new byte[maxLen];
            var written = Encoding.UTF8.GetBytes(s, buf);
            if (scope.Find(buf.AsSpan(0, written), utf8))
                throw new BakeDiagnosticException($"duplicate field: '{s}'", LineOf(utf8, tok), ColOf(utf8, tok));
        }
    }

    private static int LineOf(byte[] utf8, int index)
    {
        var line = 1;
        var count = Math.Min(index, utf8.Length);
        for (var i = 0; i < count; i++)
            if (utf8[i] == (byte)'\n')
                line++;
        return line;
    }

    private static int ColOf(byte[] utf8, int index)
    {
        var col = 1;
        var count = Math.Min(index, utf8.Length);
        for (var i = 0; i < count; i++)
        {
            if (utf8[i] == (byte)'\n')
                col = 1;
            else
                col++;
        }
        return col;
    }
}

internal sealed class DupScope
{
    private const int InitialCapacity = 16;
    private int _mask = InitialCapacity - 1;
    private int[] _slots = NewSlots(InitialCapacity);
    private ulong[] _hashes = new ulong[InitialCapacity];
    private int[] _starts = new int[InitialCapacity];
    private int[] _lens = new int[InitialCapacity];
    private int _count;
    internal HashSet<string>? Decoded;

    private static int[] NewSlots(int cap)
    {
        var slots = new int[cap];
        Array.Fill(slots, -1);
        return slots;
    }

    internal void Reset()
    {
        if (_count > 0)
            Array.Fill(_slots, -1);
        _count = 0;
        Decoded = null;
    }

    internal bool Find(ReadOnlySpan<byte> name, byte[] buffer)
    {
        var h = FieldTable.HashOf(name);
        var i = (int)(h & (ulong)_mask);
        while (_slots[i] >= 0)
        {
            var e = _slots[i];
            var len = _lens[e];
            if (_hashes[e] == h && len == name.Length && buffer.AsSpan(_starts[e], len).SequenceEqual(name))
                return true;
            i = (i + 1) & _mask;
        }
        return false;
    }

    internal void Add(ReadOnlySpan<byte> name, int start)
    {
        if ((_count + 1) * 4 >= (_mask + 1) * 3)
            Grow();
        var h = FieldTable.HashOf(name);
        var i = (int)(h & (ulong)_mask);
        while (_slots[i] >= 0)
            i = (i + 1) & _mask;
        _slots[i] = _count;
        _hashes[_count] = h;
        _starts[_count] = start;
        _lens[_count] = name.Length;
        _count++;
    }

    private void Grow()
    {
        var newCap = (_mask + 1) * 2;
        var newSlots = NewSlots(newCap);
        var newHashes = new ulong[newCap];
        var newStarts = new int[newCap];
        var newLens = new int[newCap];
        var newMask = newCap - 1;
        for (var e = 0; e < _count; e++)
        {
            var i = (int)(_hashes[e] & (ulong)newMask);
            while (newSlots[i] >= 0)
                i = (i + 1) & newMask;
            newSlots[i] = e;
            newHashes[e] = _hashes[e];
            newStarts[e] = _starts[e];
            newLens[e] = _lens[e];
        }
        _mask = newMask;
        _slots = newSlots;
        _hashes = newHashes;
        _starts = newStarts;
        _lens = newLens;
    }
}


internal ref struct Walker
{
    private Utf8JsonReader _r;
    private readonly byte[] _utf8;
    private readonly BakerAssemblyResolver _resolver;
    private readonly FastDoc _doc;
    private readonly List<(int Group, int Rank, int Seq, string Message)> _pending = new();
    private int _seq;
    private readonly bool _auto;
    private readonly List<DupScope> _scopes = new();

    internal Walker(byte[] utf8, BakerAssemblyResolver resolver, BakeWorkspace? workspace = null, bool autoNamespace = false, LibraryManifest? manifest = null)
    {
        _utf8 = utf8;
        _resolver = resolver;
        _auto = autoNamespace;
        _doc = new FastDoc { Utf8 = utf8, Resolver = resolver, Workspace = workspace, Manifest = manifest };
        workspace?.Warm(_doc);
        _r = new Utf8JsonReader(utf8);
    }

    internal FastDoc Finish()
    {
        if (_pending.Count == 0)
            return _doc;
        var best = _pending[0];
        foreach (var p in _pending)
            if (p.Group < best.Group || (p.Group == best.Group && (p.Rank < best.Rank || (p.Rank == best.Rank && p.Seq < best.Seq))))
                best = p;
        throw new BakeDiagnosticException(best.Message);
    }

    private void Record(int group, int rank, string message) => _pending.Add((group, rank, _seq++, message));

    private string Raw()
    {
        var start = (int)_r.TokenStartIndex;
        if (_r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            SkipValueWithDup();
        var end = (int)_r.BytesConsumed;
        return Encoding.UTF8.GetString(_utf8, start, end - start);
    }

    private void PendingRoot(string message) => Record(0, 0, message);
    private void PendingRootPost(string message) => Record(1, 0, message);
    private void PendingTrackProp(int track, string message) => Record(2 + track, 0, message);
    private void PendingTrackPost(int track, string message) => Record(2 + track, 1, message);
    private void PendingClip(int track, string message) => Record(2 + track, 2, message);

    internal void Run()
    {
        if (!_r.Read())
            throw new JsonException("The input does not contain any JSON tokens.");
        if (_r.TokenType != JsonTokenType.StartObject)
        {
            PendingRoot("Root of timeline document must be a JSON object.");
            SkipRest();
            return;
        }
        PushScope();
        var seenDuration = false;
        var seenTracks = false;
        while (_r.Read())
        {
            if (_r.TokenType == JsonTokenType.EndObject)
                break;
            DupCheck.Do(ref _r, _scopes, _utf8, 0);
            var name = NameToken();
            switch (name)
            {
                case "schema":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.Number || !_r.TryGetInt32(out var schema) || schema != 1)
                        PendingRoot($"unsupported timeline schema: expected schema 1, got '{Raw()}'.");
                    break;
                case "name":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.String)
                        PendingRoot($"wrong-typed value: root 'name' must be a string, got '{Raw()}'.");
                    else
                        _doc.RootName = _r.GetString()!;
                    break;
                case "duration":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.Number || !_r.TryGetUInt32(out _doc.Duration))
                        PendingRoot($"wrong-typed value: 'duration' must be an unsigned integer, got '{Raw()}'.");
                    else
                        seenDuration = true;
                    break;
                case "loop":
                    _r.Read();
                    if (_r.TokenType is JsonTokenType.True)
                        _doc.Loops = true;
                    else if (_r.TokenType is JsonTokenType.False)
                        _doc.Loops = false;
                    else
                        PendingRoot($"wrong-typed value: 'loop' must be a boolean, got '{Raw()}'.");
                    break;
                case "next":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.String)
                        PendingRoot($"wrong-typed value: root 'next' must be a string, got '{Raw()}'.");
                    else if (_r.GetString()!.Length == 0)
                        PendingRoot("wrong-typed value: root 'next' must be a non-empty timeline name.");
                    else
                        _doc.NextName = _r.GetString()!;
                    break;
                case "hold":
                    _r.Read();
                    if (_r.TokenType is JsonTokenType.True)
                        _doc.Hold = true;
                    else if (_r.TokenType is JsonTokenType.False)
                        _doc.Hold = false;
                    else
                        PendingRoot($"wrong-typed value: 'hold' must be a boolean, got '{Raw()}'.");
                    break;
                case "channel":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.String)
                        PendingRoot($"wrong-typed value: root 'channel' must be a string, got '{Raw()}'.");
                    else if (_r.GetString()!.Length == 0)
                        PendingRoot("wrong-typed value: root 'channel' must be a non-empty channel name.");
                    else
                        _doc.ChannelName = _r.GetString()!;
                    break;
                case "markers":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.StartArray)
                    {
                        PendingRoot($"wrong-typed value: 'markers' must be an array, got '{Raw()}'.");
                        break;
                    }
                    ParseMarkers();
                    break;
                case "tracks":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.StartArray)
                    {
                        PendingRoot($"wrong-typed value: 'tracks' must be an array, got '{Raw()}'.");
                        break;
                    }
                    seenTracks = true;
                    ParseTracks();
                    break;
                default:
                    PendingRoot(RejectMessage(name, "root"));
                    _r.Read();
                    SkipValueWithDup();
                    break;
            }
        }
        PopScope();
        if (!seenDuration)
            PendingRootPost("Missing required property 'duration'.");
        else if (_doc.Duration > ushort.MaxValue)
            PendingRootPost($"duration {_doc.Duration} exceeds the 65535-tick lane position column; the typed playback lane cannot bind longer timelines.");
        if (!seenTracks)
            PendingRootPost("Missing required property 'tracks'.");
        SkipRest();
    }

    private void ParseTracks()
    {
        var trackIndex = 0;
        while (_r.Read())
        {
            if (_r.TokenType == JsonTokenType.EndArray)
                break;
            if (_r.TokenType != JsonTokenType.StartObject)
            {
                PendingTrackProp(trackIndex, $"Track at index {trackIndex} must be an object.");
                SkipValueWithDup();
                trackIndex++;
                continue;
            }
            ParseTrack(trackIndex);
            trackIndex++;
        }
    }

    private void ParseMarkers()
    {
        var markerIndex = 0;
        while (_r.Read())
        {
            if (_r.TokenType == JsonTokenType.EndArray)
                break;
            if (_r.TokenType != JsonTokenType.StartObject)
            {
                PendingRoot($"Marker at index {markerIndex} must be an object.");
                SkipValueWithDup();
                markerIndex++;
                continue;
            }
            ParseMarker(markerIndex);
            markerIndex++;
        }
    }

    private void ParseMarker(int markerIndex)
    {
        var marker = new FastMarker();
        var ctx = $"marker {markerIndex}";
        var haveTick = false;
        var haveType = false;
        PushScope();
        while (_r.Read())
        {
            if (_r.TokenType == JsonTokenType.EndObject)
                break;
            DupCheck.Do(ref _r, _scopes, _utf8, 0);
            var name = NameToken();
            switch (name)
            {
                case "type":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.String)
                        PendingRoot($"wrong-typed value: {ctx} 'type' must be a string, got '{Raw()}'.");
                    else if (_r.GetString()!.Length == 0)
                        PendingRoot($"wrong-typed value: {ctx} 'type' must be a non-empty bare name.");
                    else
                    {
                        marker.TypeName = _r.GetString()!;
                        haveType = true;
                    }
                    break;
                case "namespace":
                    marker.Ns = ReadBareName("namespace", ctx, out var nsError);
                    if (nsError != null) PendingRoot(nsError);
                    break;
                case "assembly":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.String)
                        PendingRoot($"wrong-typed value: {ctx} 'assembly' must be a string, got '{Raw()}'.");
                    else
                        marker.Asm = _r.GetString()!;
                    break;
                case "tick":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.Number || !_r.TryGetUInt32(out marker.Tick))
                        PendingRoot($"wrong-typed value: 'tick' must be an unsigned integer, got '{Raw()}'.");
                    else
                        haveTick = true;
                    break;
                case "data":
                    _r.Read();
                    marker.DataStart = (int)_r.TokenStartIndex;
                    marker.DataCaptured = true;
                    SkipValueWithDup();
                    marker.DataEnd = (int)_r.BytesConsumed;
                    break;
                default:
                    PendingRoot(RejectMessage(name, ctx));
                    _r.Read();
                    SkipValueWithDup();
                    break;
            }
        }
        PopScope();
        if (!haveType) PendingRoot($"missing required property: {ctx} needs 'type'.");
        if (!haveTick) PendingRoot($"missing required property: {ctx} needs 'tick'.");
        _doc.Markers.Add(marker);
    }

    private void ParseTrack(int trackIndex)
    {
        var trackId = _doc.Tracks.Count;
        var info = new FastTrackInfo();
        PushScope();
        var hasClips = false;
        while (_r.Read())
        {
            if (_r.TokenType == JsonTokenType.EndObject)
                break;
            DupCheck.Do(ref _r, _scopes, _utf8, 0);
            var name = NameToken();
            switch (name)
            {
                case "name":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.String)
                        PendingTrackProp(trackIndex, $"wrong-typed value: track {trackIndex} 'name' must be a string, got '{Raw()}'.");
                    else
                        info.Name = _r.GetString()!;
                    break;
                case "namespace":
                    info.Ns = ReadBareName("namespace", $"track {trackIndex}", out var nsError);
                    if (nsError != null)
                        PendingTrackProp(trackIndex, nsError);
                    else
                    {
                        info.HaveNs = true;
                        if (info.TrackType != null || info.TrackTypeFailed)
                            info.ResolveNeedsRefresh = true;
                    }
                    break;
                case "type":
                    info.TypeName = ReadBareName("type", $"track {trackIndex}", out var typeError);
                    if (typeError != null)
                        PendingTrackProp(trackIndex, typeError);
                    else
                    {
                        info.HaveType = true;
                        if (info.TrackType != null || info.TrackTypeFailed)
                            info.ResolveNeedsRefresh = true;
                        else
                            TryResolveTrackType(info, $"track {trackIndex}");
                    }
                    break;
                case "assembly":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.String)
                        PendingTrackProp(trackIndex, $"wrong-typed value: track {trackIndex} 'assembly' must be a string, got '{Raw()}'.");
                    else
                    {
                        info.Asm = _r.GetString()!;
                        if (info.TrackType != null || info.TrackTypeFailed)
                            info.ResolveNeedsRefresh = true;
                    }
                    break;
                case "data":
                    _r.Read();
                    info.DataStart = (int)_r.TokenStartIndex;
                    info.DataCaptured = true;
                    SkipValueWithDup();
                    info.DataEnd = (int)_r.BytesConsumed;
                    break;
                case "clips":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.StartArray)
                    {
                        info.ClipArrayError = $"wrong-typed value: 'clips' must be an array, got '{Raw()}'.";
                        break;
                    }
                    hasClips = true;
                    if (info is { HaveType: true, TrackType: null, TrackTypeFailed: false })
                        TryResolveTrackType(info, $"track {trackIndex}");
                    ParseClips(trackIndex, trackId, info);
                    if (info.ResolveNeedsRefresh)
                        RefreshTrackResolve(trackId, info);
                    break;
                default:
                    PendingTrackProp(trackIndex, RejectMessage(name, $"track {trackIndex}"));
                    _r.Read();
                    SkipValueWithDup();
                    break;
            }
        }
        PopScope();
        if (info.ClipArrayError != null)
            PendingTrackProp(trackIndex, info.ClipArrayError);
        if (!_auto && !info.HaveNs)
            PendingTrackPost(trackIndex, $"missing required property: track {trackIndex} needs 'namespace'.");
        if (!info.HaveType)
            PendingTrackPost(trackIndex, $"missing required property: track {trackIndex} needs 'type'.");
        if (!hasClips)
            PendingTrackPost(trackIndex, $"Track at index {trackIndex} missing required 'clips' array.");
        if (info is { HaveType: true, TrackType: null, TrackTypeFailed: false })
            TryResolveTrackType(info, $"track {trackIndex}");
        if (info.ResolveNeedsRefresh)
            RefreshTrackResolve(trackId, info);
        RevisitDeferredClips(trackIndex, info);
        _doc.Tracks.Add(info);
    }

    private void RevisitDeferredClips(int trackIndex, FastTrackInfo info)
    {
        if (info.TrackType == null)
            return;
        foreach (var clipId in info.ClipIds)
        {
            var clip = _doc.Clips[clipId];
            if (!clip.TypeSet || clip.TypeName.Length == 0 || clip.PairId >= 0 || clip.ClipType != null || clip.ClipTypeFailed)
                continue;
            var ctx = $"clip {clip.ClipIndex} on track {trackIndex}";
            TryResolveClipPair(clip, info.TrackType, ctx);
            if (clip is { DataCaptured: true, PairId: >= 0, PopulateDone: false })
                PopulateDeferred(clip, ctx);
        }
    }

    private void RefreshTrackResolve(int trackId, FastTrackInfo info)
    {
        var previousType = info.TrackType;
        info.TrackType = null;
        info.TrackTypeFailed = false;
        info.ResolveNeedsRefresh = false;
        info.InferError = null;
        TryResolveTrackType(info, $"track {trackId}");
        if (info.TrackTypeFailed || !ReferenceEquals(info.TrackType, previousType))
            foreach (var clipId in info.ClipIds)
            {
                var clip = _doc.Clips[clipId];
                clip.ClipType = null;
                clip.ClipTypeFailed = false;
                clip.PairId = -1;
                clip.PayloadOffset = -1;
                clip.PopulateError = null;
                clip.PopulateDone = false;
                clip.InferError = null;
            }
    }

    private void TryResolveTrackType(FastTrackInfo info, string context)
    {
        if (_auto && info is { HaveNs: false, TypeName.Length: > 0, TrackTypeFailed: false, TrackType: null })
        {
            try
            {
                info.Ns = _resolver.ResolveInferredType(info.TypeName, info.Asm, context).Namespace ?? "";
            }
            catch (BakeDiagnosticException ex)
            {
                info.InferError = ex.Message;
                info.TrackTypeFailed = true;
                return;
            }
        }
        var key = (info.Ns, info.TypeName, info.Asm);
        if (_doc.ResolveCache.TryGetValue(key, out var cached))
        {
            info.TrackType = cached;
            return;
        }
        try
        {
            var t = _resolver.ResolveType(info.Ns, info.TypeName, info.Asm, context);
            _doc.ResolveCache[key] = t;
            info.TrackType = t;
        }
        catch (BakeDiagnosticException)
        {
            info.TrackTypeFailed = true;
        }
    }

    private void ParseClips(int trackIndex, int trackId, FastTrackInfo info)
    {
        var clipIndex = 0;
        while (_r.Read())
        {
            if (_r.TokenType == JsonTokenType.EndArray)
                break;
            if (_r.TokenType != JsonTokenType.StartObject)
            {
                PendingClip(trackIndex, $"Clip {clipIndex} on track {trackIndex} must be an object.");
                SkipValueWithDup();
                clipIndex++;
                continue;
            }
            ParseClip(trackIndex, trackId, clipIndex, info);
            clipIndex++;
        }
    }

    private void ParseClip(int trackIndex, int trackId, int clipIndex, FastTrackInfo info)
    {
        var clip = new FastClip
        {
                        ClipIndex = clipIndex,
            AuthoredIndex = _doc.Clips.Count,
        };
        var ctx = $"clip {clipIndex} on track {trackIndex}";
        var haveStart = false;
        var haveEnd = false;
        PushScope();
        while (_r.Read())
        {
            if (_r.TokenType == JsonTokenType.EndObject)
                break;
            DupCheck.Do(ref _r, _scopes, _utf8, 0);
            var name = NameToken();
            switch (name)
            {
                case "name":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.String)
                        PendingClip(trackIndex, $"wrong-typed value: {ctx} 'name' must be a string, got '{Raw()}'.");
                    else
                        clip.Name = _r.GetString()!;
                    break;
                case "namespace":
                    clip.Ns = ReadBareName("namespace", ctx, out var nsError);
                    clip.NsSet = true;
                    if (nsError != null)
                        PendingClip(trackIndex, nsError);
                    else if (clip.ClipType != null || clip.ClipTypeFailed)
                        clip.ResolveNeedsRefresh = true;
                    break;
                case "type":
                    clip.TypeName = ReadBareName("type", ctx, out var typeError);
                    clip.TypeSet = true;
                    if (typeError != null)
                        PendingClip(trackIndex, typeError);
                    else if (clip.ClipType != null || clip.ClipTypeFailed)
                        clip.ResolveNeedsRefresh = true;
                    else if (info.TrackType != null)
                        TryResolveClipPair(clip, info.TrackType, ctx);
                    break;
                case "assembly":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.String)
                        PendingClip(trackIndex, $"wrong-typed value: {ctx} 'assembly' must be a string, got '{Raw()}'.");
                    else
                    {
                        clip.Asm = _r.GetString()!;
                        if (clip.ClipType != null || clip.ClipTypeFailed)
                            clip.ResolveNeedsRefresh = true;
                    }
                    break;
                case "start":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.Number || !_r.TryGetUInt32(out clip.Start))
                        PendingClip(trackIndex, $"wrong-typed value: 'start' must be an unsigned integer, got '{Raw()}'.");
                    else
                        haveStart = true;
                    break;
                case "end":
                    _r.Read();
                    if (_r.TokenType != JsonTokenType.Number || !_r.TryGetUInt32(out clip.End))
                        PendingClip(trackIndex, $"wrong-typed value: 'end' must be an unsigned integer, got '{Raw()}'.");
                    else
                        haveEnd = true;
                    break;
                case "data":
                    _r.Read();
                    clip.DataStart = (int)_r.TokenStartIndex;
                    clip.DataCaptured = true;
                    if (clip.PairId >= 0)
                        PopulateInline(clip, ctx);
                    else
                        SkipValueWithDup();
                    clip.DataEnd = (int)_r.BytesConsumed;
                    break;
                default:
                    PendingClip(trackIndex, RejectMessage(name, ctx));
                    _r.Read();
                    SkipValueWithDup();
                    break;
            }
        }
        PopScope();
        if (!_auto && !clip.NsSet)
            PendingClip(trackIndex, $"missing required property: {ctx} needs 'namespace' (type identity is never inherited from the track).");
        if (!clip.TypeSet)
            PendingClip(trackIndex, $"missing required property: {ctx} needs 'type' (type identity is never inherited from the track).");
        if (!haveStart || !haveEnd)
            PendingClip(trackIndex, $"Clip {clipIndex} on track {trackIndex} missing 'start' or 'end'.");
        if (clip is { TypeSet: true, TypeName.Length: > 0, PairId: < 0, ClipType: null, ClipTypeFailed: false } && info.TrackType != null)
            TryResolveClipPair(clip, info.TrackType, ctx);
        if (clip.ResolveNeedsRefresh)
            RefreshClipResolve(clip, info.TrackType, ctx);
        if (clip is { DataCaptured: true, PairId: >= 0 })
            PopulateDeferred(clip, ctx);
        info.ClipIds.Add(_doc.Clips.Count);
        _doc.Clips.Add(clip);
    }

    private void RefreshClipResolve(FastClip clip, Type? trackType, string context)
    {
        clip.ResolveNeedsRefresh = false;
        clip.InferError = null;
        if (trackType == null)
            return;
        var previousType = clip.ClipType;
        var previousFailed = clip.ClipTypeFailed;
        clip.ClipType = null;
        clip.ClipTypeFailed = false;
        clip.PairId = -1;
        clip.PayloadOffset = -1;
        clip.PopulateError = null;
        clip.PopulateDone = false;
        TryResolveClipPair(clip, trackType, context);
        if (previousFailed != clip.ClipTypeFailed || !ReferenceEquals(previousType, clip.ClipType))
        {
            clip.PairId = -1;
            clip.PayloadOffset = -1;
            clip.PopulateError = null;
            clip.PopulateDone = false;
            clip.ClipType = null;
            clip.ClipTypeFailed = false;
        }
    }

    private void TryResolveClipPair(FastClip clip, Type trackType, string context)
    {
        if (_auto && !clip.NsSet && clip.TypeName.Length > 0 && !clip.ClipTypeFailed && clip.ClipType == null)
        {
            try
            {
                clip.Ns = _resolver.ResolveInferredType(clip.TypeName, clip.Asm, context).Namespace ?? "";
            }
            catch (BakeDiagnosticException ex)
            {
                clip.InferError = ex.Message;
                clip.ClipTypeFailed = true;
                return;
            }
        }
        var key = (clip.Ns, clip.TypeName, clip.Asm);
        if (!_doc.ResolveCache.TryGetValue(key, out var clipType))
        {
            try
            {
                clipType = _resolver.ResolveType(clip.Ns, clip.TypeName, clip.Asm, context);
                _doc.ResolveCache[key] = clipType;
            }
            catch (BakeDiagnosticException)
            {
                clip.ClipTypeFailed = true;
                return;
            }
        }
        clip.ClipType = clipType;
        if (TimelineBakerFast.IsUnmanagedCached(trackType)
            && TimelineBakerFast.IsUnmanagedCached(clipType)
            && TimelineBakerFast.IsBlendPairCached(trackType, clipType))
            clip.PairId = TimelineBakerFast.EnsurePair(_doc, trackType, clipType);
    }

    private void PopulateInline(FastClip clip, string ctx)
    {
        if (_r.TokenType != JsonTokenType.StartObject)
        {
            var kind = TimelineBakerFast.KindNameOfValue(_r.TokenType);
            clip.PopulateError = $"wrong-typed value for {ctx} ({_doc.Pairs[clip.PairId].ClipType.Name}): expected object, got {kind}.";
            SkipValueWithDup();
            return;
        }
        PopulateAt(clip, ctx, deferred: false);
    }

    private void PopulateDeferred(FastClip clip, string ctx) => PopulateAt(clip, ctx, deferred: true);

    private unsafe void PopulateAt(FastClip clip, string ctx, bool deferred)
    {
        var pair = _doc.Pairs[clip.PairId];
        if (!TimelineBakerFast.IsUnmanagedCached(pair.ClipType))
            return;
        var contextName = $"{ctx} ({pair.ClipType.Name})";
        pair.EnsurePool(pair.ClipSize);
        clip.PayloadOffset = pair.PoolLength;
        pair.PoolLength += pair.ClipSize;
        var box = Activator.CreateInstance(pair.ClipType)!;
        var err = deferred
            ? TimelineBakerFast.PopulateSliceUtf8(_utf8, clip.DataStart, clip.DataEnd, pair.Fields, box, pair.ClipType, contextName, _doc.Manifest)
            : TimelineBakerFast.PopulateObject(ref _r, pair.Fields, box, pair.ClipType, contextName, _utf8, 0, checkDup: true, _scopes, _doc.Manifest);
        if (err == null)
        {
            fixed (byte* dst = pair.Pool)
                pair.Helper.CopyPayload(dst + clip.PayloadOffset, box);
        }
        clip.PopulateError = err;
        clip.PopulateDone = true;
    }

    private string ReadBareName(string field, string context, out string? error)
    {
        error = null;
        _r.Read();
        if (_r.TokenType != JsonTokenType.String)
        {
            error = $"wrong-typed value: {context} '{field}' must be a string, got '{Raw()}'.";
            return "";
        }
        var text = _r.GetString()!;
        if (!(field == "type" && text.Contains('<')) && (field != "namespace" && text.Contains('.') || text.Contains(',') || text.Contains('+') || text.Contains('=')))
            error = $"dotted name rejected: '{field}' in {context} must be a bare name without '.', ',', '+' or '='; got '{text}'. Empty selects the global namespace.";
        return text;
    }

    private static string RejectMessage(string name, string context)
    {
        if (name is "trackType" or "clipType" or "track" or "payload")
            return $"removed property '{name}' in {context}: schema v1 declares 'namespace', 'type' and optional 'assembly' on every track and clip, and 'data' for payloads; type identity is never inherited between levels. See docs/data-authored-api.md.";
        if (name is "loops" or "looping")
            return $"renamed property '{name}' in {context}: use 'loop'.";
        return $"unknown field name in track/payload: unknown {context} property '{name}'.";
    }

    private void SkipRest()
    {
        while (_r.Read())
        {
            switch (_r.TokenType)
            {
                case JsonTokenType.PropertyName:
                    DupCheck.Do(ref _r, _scopes, _utf8, 0);
                    _r.Read();
                    SkipValueWithDup();
                    break;
                case JsonTokenType.StartObject:
                    PushScope();
                    break;
                case JsonTokenType.EndObject:
                    if (_scopes.Count == 0)
                        throw new InvalidOperationException("Stack empty.");
                    PopScope();
                    break;
            }
        }
    }

    private void SkipValueWithDup() => TimelineBakerFast.SkipValueWithDupScanning(ref _r, _scopes, _utf8, 0);

    private void PushScope() => _scopes.Add(DupScopePool.Rent());

    private void PopScope()
    {
        var s = _scopes[^1];
        _scopes.RemoveAt(_scopes.Count - 1);
        s.Reset();
        DupScopePool.Return(s);
    }

    private string NameToken()
    {
        if (_r.HasValueSequence)
            return _r.GetString()!;
        var span = _r.ValueSpan;
        return NameBytes.HasEscape(span) ? _r.GetString()! : Encoding.UTF8.GetString(span);
    }
}


internal static class TimelineBakerFastCore
{
    internal static byte[] BakeFast(FastDoc doc, BakerAssemblyResolver resolver) => new Replayer(doc, resolver).Run();

    private sealed class Lane
    {
        public FastPairInfo Pair = null!;
        public int TrackEntry;
        public byte[] TrackBytes = null!;
        public ushort TrackValueIndex;
        public readonly List<int> ClipIds = new();
    }

    private sealed class Replayer(FastDoc doc, BakerAssemblyResolver resolver)
    {
        private readonly Dictionary<(Type, Type), bool> _unmanagedChecks = new();
        private readonly Dictionary<(Type, Type), bool> _blendChecks = new();



        public byte[] Run()
        {
            if (doc.Tracks.Count > 256)
                throw new BakeDiagnosticException($"asset exceeds 256 authored tracks: {doc.Tracks.Count} track entries is above the TLB1 TrackIndex capacity.");
            if (doc.Loops && doc.NextName != null)
                throw new BakeDiagnosticException("next is incompatible with loop:true; a looping timeline never hands over.");
            if (doc.Loops && doc.Hold)
                throw new BakeDiagnosticException("hold is incompatible with loop:true; a looping timeline never ends.");
            if (doc.Manifest == null)
            {
                if (doc.NextName != null) throw new BakeDiagnosticException($"timeline reference without manifest: root 'next' names timeline '{doc.NextName}' but no library manifest is available; bake via `tlb library`.");
                if (doc.ChannelName != null) throw new BakeDiagnosticException($"channel without manifest: root 'channel' names channel '{doc.ChannelName}' but no library manifest is available; bake via `tlb library`.");
            }
            else
            {
                if (doc.NextName != null && !doc.Manifest.Timelines.TryGetValue(doc.NextName, out var nid)) throw new BakeDiagnosticException($"unknown timeline: root 'next' names unknown timeline '{doc.NextName}'.");
                if (doc.NextName != null) doc.NextId = doc.Manifest.Timelines[doc.NextName];
                if (doc.ChannelName != null && !doc.Manifest.Channels.TryGetValue(doc.ChannelName, out var cid)) throw new BakeDiagnosticException($"unknown channel: root 'channel' names unknown channel '{doc.ChannelName}'.");
                if (doc.ChannelName != null) doc.ChannelId = doc.Manifest.Channels[doc.ChannelName];
            }
            ResolveMarkers();
            var lanes = new List<Lane>();
            var labels = new List<(int Track, int Clip, string Name)>();
            if (doc.RootName != null)
                labels.Add((-1, -1, doc.RootName));
            if (doc.ChannelName != null)
                labels.Add((-2, -1, doc.ChannelName));
            if (doc.NextName != null)
                labels.Add((-3, -1, doc.NextName));

            for (var ti = 0; ti < doc.Tracks.Count; ti++)
            {
                var info = doc.Tracks[ti];
                if (info.ClipIds.Count == 0 && doc.Duration > 0)
                    throw new BakeDiagnosticException($"empty tracks with duration > 0 (stages must cover duration): track {ti} has 0 clips.");

                var trackType = ResolveTrack(info, ti);
                PopulateTrack(info, ti, trackType);

                if (info.Name != null)
                    labels.Add((ti, -1, info.Name));

                foreach (var clipId in info.ClipIds)
                {
                    var clip = doc.Clips[clipId];
                    if (clip.Start >= clip.End)
                        throw new BakeDiagnosticException($"start >= end: clip [{clip.Start}, {clip.End}) on track {ti} is empty or reversed.");
                    if (clip.End > doc.Duration)
                        throw new BakeDiagnosticException($"clips outside [0, duration]: clip [{clip.Start}, {clip.End}) on track {ti} exceeds timeline duration {doc.Duration}.");

                    var clipType = ResolveClip(clip, ti);
                    CheckUnmanaged(trackType, clipType);

                    if (clip.PopulateError != null)
                        throw new BakeDiagnosticException(clip.PopulateError);
                    if (!clip.PopulateDone)
                    {
                        if (clip.PairId < 0)
                            clip.PairId = TimelineBakerFast.EnsurePair(doc, trackType, clipType);
                        if (clip.PairId >= 0)
                            PopulateClip(clip, ti, clip.DataCaptured);
                        else
                            clip.PopulateDone = true;
                    }

                    if (clip.Name != null)
                        labels.Add((ti, clip.ClipIndex, clip.Name));
                }

                var groupOrder = new List<Type>();
                foreach (var clipId in info.ClipIds)
                {
                    var clipType = doc.Clips[clipId].ClipType!;
                    if (!groupOrder.Contains(clipType))
                        groupOrder.Add(clipType);
                }

                foreach (var clipType in groupOrder)
                    ValidateBlendType(trackType, clipType, ti);

                foreach (var clipType in groupOrder)
                {
                    var pair = doc.Pairs[doc.PairIds[(trackType, clipType)]];
                    var lane = new Lane { Pair = pair, TrackEntry = ti, TrackBytes = info.TrackBytes! };
                    foreach (var clipId in info.ClipIds)
                        if (doc.Clips[clipId].ClipType == clipType)
                            lane.ClipIds.Add(clipId);

                    var sortedClips = new List<FastClip>(lane.ClipIds.Count);
                    foreach (var clipId in lane.ClipIds)
                        sortedClips.Add(doc.Clips[clipId]);
                    sortedClips.Sort((a, b) =>
                    {
                        var byStart = a.Start.CompareTo(b.Start);
                        return byStart != 0 ? byStart : a.End.CompareTo(b.End);
                    });
                    for (var i = 1; i < sortedClips.Count; i++)
                        if (sortedClips[i].Start == sortedClips[i - 1].Start)
                            throw new BakeDiagnosticException($"overlapping clips on one track: track {ti} ({trackType.Name}/{pair.ClipType.Name}) has multiple clips starting at tick {sortedClips[i].Start}.");

                    var events = new List<(uint Tick, int Delta)>(sortedClips.Count * 2);
                    foreach (var c in sortedClips)
                    {
                        events.Add((c.Start, 1));
                        events.Add((c.End, -1));
                    }
                    events.Sort(static (a, b) =>
                    {
                        var byTick = a.Tick.CompareTo(b.Tick);
                        return byTick != 0 ? byTick : a.Delta.CompareTo(b.Delta);
                    });
                    var active = 0;
                    var i0 = 0;
                    while (i0 < events.Count)
                    {
                        var tick = events[i0].Tick;
                        while (i0 < events.Count && events[i0].Tick == tick)
                        {
                            active += events[i0].Delta;
                            i0++;
                        }
                        if (active > 2)
                            throw new BakeDiagnosticException($"overlapping clips on one track: track {ti} ({trackType.Name}/{pair.ClipType.Name}) has more than two overlapping clips at tick {tick}.");
                    }

                    lanes.Add(lane);
                }
            }

            return Core(lanes, labels);
        }

        private Type ResolveTrack(FastTrackInfo info, int ti)
        {
            if (info.InferError != null)
                throw new BakeDiagnosticException(info.InferError);
            if (!info.TrackTypeFailed)
                return info.TrackType!;
            resolver.ResolveType(info.Ns, info.TypeName, info.Asm, $"track {ti}");
            throw new InvalidOperationException("unreachable: failed resolve must throw");
        }

        private void PopulateTrack(FastTrackInfo info, int ti, Type trackType)
        {
            var contextName = $"track {ti} ({trackType.Name})";
            var unmanaged = TimelineBakerFast.IsUnmanagedCached(trackType);
            if (info.PopulateDone)
                return;
            if (!info.DataCaptured)
            {
                if (unmanaged)
                {
                    var helper = TimelineBakerFast.HelperFor(trackType);
                    info.TrackBytes = new byte[helper.Size];
                }
                else
                {
                    Activator.CreateInstance(trackType);
                }
            }
            else if (unmanaged)
            {
                var helper = TimelineBakerFast.HelperFor(trackType);
                info.TrackBytes = new byte[helper.Size];
                var box = Activator.CreateInstance(trackType)!;
                var err = TimelineBakerFast.PopulateSliceUtf8(doc.Utf8, info.DataStart, info.DataEnd, TimelineBakerFast.TableFor(trackType), box, trackType, contextName, doc.Manifest);
                if (err != null)
                    throw new BakeDiagnosticException(err);
                unsafe
                {
                    fixed (byte* dst = info.TrackBytes)
                        helper.CopyTo(dst, box);
                }
            }
            else
            {
                try
                {
                    using var slice = JsonDocument.Parse(doc.Utf8.AsMemory(info.DataStart, info.DataEnd - info.DataStart), new JsonDocumentOptions { AllowTrailingCommas = true });
                    BakerAssemblyResolver.PopulateStruct(trackType, slice.RootElement, contextName);
                }
                catch (BakeDiagnosticException ex)
                {
                    throw new BakeDiagnosticException(ex.Message);
                }
            }
            info.PopulateDone = true;
        }

        private Type ResolveClip(FastClip clip, int ti)
        {
            if (clip.InferError != null)
                throw new BakeDiagnosticException(clip.InferError);
            if (clip.ClipTypeFailed)
            {
                resolver.ResolveType(clip.Ns, clip.TypeName, clip.Asm, $"clip {clip.ClipIndex} on track {ti}");
                throw new InvalidOperationException("unreachable: failed resolve must throw");
            }
            if (clip.ClipType == null)
            {
                var key = (clip.Ns, clip.TypeName, clip.Asm);
                if (!doc.ResolveCache.TryGetValue(key, out var resolved))
                {
                    resolved = resolver.ResolveType(clip.Ns, clip.TypeName, clip.Asm, $"clip {clip.ClipIndex} on track {ti}");
                    doc.ResolveCache[key] = resolved;
                }
                clip.ClipType = resolved;
            }
            return clip.ClipType!;
        }

        private void CheckUnmanaged(Type trackType, Type clipType)
        {
            if (_unmanagedChecks.TryGetValue((trackType, clipType), out var ok) && ok)
                return;
            BakerAssemblyResolver.ValidateUnmanaged(trackType, clipType);
            _unmanagedChecks[(trackType, clipType)] = true;
        }

        private void ValidateBlendType(Type trackType, Type clipType, int ti)
        {
            var key = (trackType, clipType);
            if (_blendChecks.TryGetValue(key, out var ok) && ok)
                return;
            ValidateBlendPairing(trackType, clipType, ti);
            _blendChecks[key] = true;
        }

        private static void ValidateBlendPairing(Type trackType, Type clipType, int entryIndex)
        {
            var instantiations = new List<Type>();
            foreach (var iface in trackType.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IBlend<>))
                    instantiations.Add(iface.GetGenericArguments()[0]);
            }

            if (instantiations.Count == 0)
                throw new BakeDiagnosticException($"missing IBlend<>: track type '{trackType.FullName}' on track {entryIndex} must implement Tl.IBlend<TClip>.");

            if (!instantiations.Contains(clipType))
            {
                var names = string.Join(", ", instantiations.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal));
                throw new BakeDiagnosticException($"clip type not blendable by track: track {entryIndex} resolves clip type '{clipType.FullName}' but track type '{trackType.FullName}' implements only these Tl.IBlend<TClip> pairings: {names}.");
            }
        }

        private unsafe void PopulateClip(FastClip clip, int ti, bool authored)
        {
            var pair = doc.Pairs[clip.PairId];
            if (!TimelineBakerFast.IsUnmanagedCached(pair.ClipType))
            {
                clip.PopulateDone = true;
                return;
            }
            pair.EnsurePool(pair.ClipSize);
            clip.PayloadOffset = pair.PoolLength;
            pair.PoolLength += pair.ClipSize;
            if (authored)
            {
                var contextName = $"clip {clip.ClipIndex} on track {ti} ({pair.ClipType.Name})";
                var box = Activator.CreateInstance(pair.ClipType)!;
                clip.PopulateError = TimelineBakerFast.PopulateSliceUtf8(doc.Utf8, clip.DataStart, clip.DataEnd, pair.Fields, box, pair.ClipType, contextName, doc.Manifest);
                if (clip.PopulateError != null)
                    throw new BakeDiagnosticException(clip.PopulateError);
                fixed (byte* dst = pair.Pool)
                    pair.Helper.CopyPayload(dst + clip.PayloadOffset, box);
            }
            else
                Array.Clear(pair.Pool, clip.PayloadOffset, pair.ClipSize);
            clip.PopulateDone = true;
        }

        private void ResolveMarkers()
        {
            for (var mi = 0; mi < doc.Markers.Count; mi++)
            {
                var marker = doc.Markers[mi];
                var ctx = $"marker {mi}";
                Type resolved;
                var key = (marker.Ns, marker.TypeName, marker.Asm);
                if (marker.MarkerType != null)
                    resolved = marker.MarkerType;
                else if (!doc.ResolveCache.TryGetValue(key, out resolved!))
                {
                    try { resolved = resolver.ResolveType(marker.Ns, marker.TypeName, marker.Asm, ctx); }
                    catch (BakeDiagnosticException ex) { throw new BakeDiagnosticException(ex.Message); }
                    doc.ResolveCache[key] = resolved;
                }
                marker.MarkerType = resolved;
                if (!TimelineBakerFast.IsUnmanagedCached(resolved))
                    throw new BakeDiagnosticException($"unsupported marker type: {ctx} resolves '{resolved.FullName}' which is not unmanaged.");
                if (marker.Tick >= doc.Duration && doc.Duration != 0)
                    throw new BakeDiagnosticException($"marker outside [0, duration]: {ctx} tick {marker.Tick} exceeds timeline duration {doc.Duration}.");
                if (doc.Duration == 0)
                    throw new BakeDiagnosticException($"marker outside [0, duration]: {ctx} tick {marker.Tick} exceeds timeline duration 0.");
                var helper = TimelineBakerFast.HelperFor(resolved);
                if (marker.Payload.Length != helper.Size)
                    marker.Payload = new byte[helper.Size];
                if (marker.DataCaptured)
                {
                    var box = Activator.CreateInstance(resolved)!;
                    var err = TimelineBakerFast.PopulateSliceUtf8(doc.Utf8, marker.DataStart, marker.DataEnd, TimelineBakerFast.TableFor(resolved), box, resolved, $"{ctx} ({resolved.Name})", doc.Manifest);
                    if (err != null) throw new BakeDiagnosticException(err);
                    unsafe { fixed (byte* dst = marker.Payload) helper.CopyTo(dst, box); }
                }
                marker.Key = MarkerKey(resolved);
            }
            doc.Markers.Sort((a, b) =>
            {
                var byTick = a.Tick.CompareTo(b.Tick);
                if (byTick != 0) return byTick;
                return a.Key.CompareTo(b.Key);
            });
        }

        internal static ulong MarkerKey(Type type)
        {
            var name = Keying.StableName(type);
            ulong h = 14695981039346656037ul;
            foreach (var c in name) h = (h ^ c) * 1099511628211ul;
            return h | 1ul;
        }

        private byte[] Core(List<Lane> lanes, List<(int Track, int Clip, string Name)> labels)
        {
            var duration = doc.Duration;
            var cuts = new List<uint>();
            if (duration != 0)
            {
                cuts.Add(0u);
                cuts.Add(duration);
                foreach (var lane in lanes)
                    foreach (var clipId in lane.ClipIds)
                    {
                        var clip = doc.Clips[clipId];
                        cuts.Add(clip.Start);
                        cuts.Add(clip.End);
                    }
            }
            cuts.Sort();
            var boundaries = new List<uint>(cuts.Count);
            foreach (var c in cuts)
                if (boundaries.Count == 0 || boundaries[^1] != c)
                    boundaries.Add(c);

            var pairKeys = new List<ulong>();
            var pairSeen = new HashSet<ulong>();
            foreach (var lane in lanes)
                if (pairSeen.Add(lane.Pair.Key))
                    pairKeys.Add(lane.Pair.Key);
            pairKeys.Sort();
            var pairIndex = new Dictionary<ulong, int>();
            for (var i = 0; i < pairKeys.Count; i++)
                pairIndex[pairKeys[i]] = i;
            var pairCount = pairKeys.Count;

            var pairTypes = new (Type Track, Type Clip)[pairCount];
            foreach (var lane in lanes)
                pairTypes[pairIndex[lane.Pair.Key]] = (lane.Pair.TrackType, lane.Pair.ClipType);

            var membersByPair = new List<Lane>[pairCount];
            var pairByPair = new FastPairInfo[pairCount];
            var trackValueBytes = new int[pairCount];
            var clipValueBytes = new int[pairCount];
            var trackSortedByPair = new int[pairCount][];
            var trackSlotsByPair = new int[pairCount][];
            var trackUniques = new int[pairCount];
            var clipSortedByPair = new int[pairCount][];
            var clipSlotsByPair = new int[pairCount][];
            var clipUniques = new int[pairCount];
            var clipOffsetsByPair = new int[pairCount][];
            foreach (var lane in lanes)
            {
                var index = pairIndex[lane.Pair.Key];
                if (membersByPair[index] != null) continue;
                var pair = lane.Pair;
                var members = lanes.Where(item => item.Pair.Key == pair.Key).ToList();
                membersByPair[index] = members;
                pairByPair[index] = pair;
                trackValueBytes[index] = pair.TrackSize;
                clipValueBytes[index] = pair.ClipSize;

                var trackName = pair.TrackType.FullName;
                trackSlotsByPair[index] = DedupPool(members.Count, o => members[o].TrackBytes, out trackSortedByPair[index], out trackUniques[index], $"track type '{trackName}'");

                var clipOffsets = new int[members.Sum(item => item.ClipIds.Count)];
                var clipOccurrence = 0;
                foreach (var member in members)
                    foreach (var clipId in member.ClipIds)
                        clipOffsets[clipOccurrence++] = doc.Clips[clipId].PayloadOffset;
                clipOffsetsByPair[index] = clipOffsets;
                var clipName = pair.ClipType.FullName;
                clipSlotsByPair[index] = DedupPool(clipOffsets.Length, o => pair.Pool.AsSpan(clipOffsets[o], pair.ClipSize), out clipSortedByPair[index], out clipUniques[index], $"clip type '{clipName}'");
            }
            var clipValueIndex = new ushort[doc.Clips.Count];
            foreach (var lane in lanes)
            {
                var index = pairIndex[lane.Pair.Key];
                var members = membersByPair[index];
                lane.TrackValueIndex = (ushort)trackSlotsByPair[index][members.IndexOf(lane)];
                var clipSlots = clipSlotsByPair[index];
                var clipOffsets = clipOffsetsByPair[index];
                var clipOccurrence = 0;
                foreach (var clipId in lane.ClipIds)
                {
                    while (clipOffsets[clipOccurrence] != doc.Clips[clipId].PayloadOffset)
                        clipOccurrence++;
                    clipValueIndex[clipId] = (ushort)clipSlots[clipOccurrence];
                    clipOccurrence++;
                }
            }

            foreach (var lane in lanes)
                lane.ClipIds.Sort((a, b) =>
                {
                    var byStart = doc.Clips[a].Start.CompareTo(doc.Clips[b].Start);
                    return byStart != 0 ? byStart : doc.Clips[a].AuthoredIndex.CompareTo(doc.Clips[b].AuthoredIndex);
                });

            var stageCount = Math.Max(boundaries.Count - 1, 0);
            var stageEdges = new (uint Start, uint End)[stageCount];
            var stageActives = new int[stageCount][];
            var stageCoverings = new (int First, int Second)[stageCount][];
            var activeLaneIdx = new List<int>(lanes.Count);
            var activeMin = new List<int>(lanes.Count);
            var coveringFirst = new List<int>(lanes.Count);
            var coveringSecond = new List<int>(lanes.Count);
            var coverTables = BuildCoverTables(lanes);
#if NET
            var vectorScan = Avx2.IsSupported;
#endif
            for (var region = 0; region < stageCount; region++)
            {
                var edge = boundaries[region];
#if NET
                var edgeVector = Vector256.Create((int)(edge ^ 0x8000_0000u));
#endif
                activeLaneIdx.Clear();
                activeMin.Clear();
                coveringFirst.Clear();
                coveringSecond.Clear();
                for (var laneIdx = 0; laneIdx < lanes.Count; laneIdx++)
                {
                    int first = -1, second = -1;
                    var minAuthored = int.MaxValue;
                    var table = coverTables[laneIdx];
#if NET
                    if (vectorScan)
                    {
                        for (var off = 0; off < table.Padded; off += 8)
                        {
                            var laneStarts = table.Starts;
                            var laneEnds = table.Ends;
                            var notLe = Avx.MoveMask(Avx2.CompareGreaterThan(Vector256.LoadUnsafe(ref laneStarts[off]).AsInt32(), edgeVector).AsSingle());
                            var gtEnd = Avx.MoveMask(Avx2.CompareGreaterThan(Vector256.LoadUnsafe(ref laneEnds[off]).AsInt32(), edgeVector).AsSingle());
                            var bits = ~notLe & gtEnd;
                            while (bits != 0)
                            {
                                var bit = BitOperations.TrailingZeroCount(bits);
                                bits &= bits - 1;
                                var pos = off + bit;
                                if (first < 0)
                                    first = table.Ids[pos];
                                else if (second < 0)
                                    second = table.Ids[pos];
                                if (table.Authored[pos] < minAuthored)
                                    minAuthored = table.Authored[pos];
                            }
                        }
                    }
                    else
#endif
                    {
                        var clips = lanes[laneIdx].ClipIds;
                        foreach (var clipIndex in clips)
                        {
                            var clip = doc.Clips[clipIndex];
                            if (clip.Start <= edge && edge < clip.End)
                            {
                                if (first < 0)
                                    first = clipIndex;
                                else
                                    second = clipIndex;
                                if (clip.AuthoredIndex < minAuthored)
                                    minAuthored = clip.AuthoredIndex;
                            }
                        }
                    }
                    if (first >= 0)
                    {
                        activeLaneIdx.Add(laneIdx);
                        activeMin.Add(minAuthored);
                        coveringFirst.Add(first);
                        coveringSecond.Add(second);
                    }
                }
                var idxs = new int[activeLaneIdx.Count];
                for (var i = 0; i < idxs.Length; i++)
                    idxs[i] = i;
                Array.Sort(idxs, (a, b) =>
                {
                    var byTrack = lanes[activeLaneIdx[a]].TrackEntry.CompareTo(lanes[activeLaneIdx[b]].TrackEntry);
                    if (byTrack != 0)
                        return byTrack;
                    return activeMin[a].CompareTo(activeMin[b]);
                });
                var actives = new int[idxs.Length];
                var coverings = new (int First, int Second)[idxs.Length];
                for (var i = 0; i < idxs.Length; i++)
                {
                    actives[i] = activeLaneIdx[idxs[i]];
                    coverings[i] = (coveringFirst[idxs[i]], coveringSecond[idxs[i]]);
                }
                stageEdges[region] = (boundaries[region], boundaries[region + 1]);
                stageActives[region] = actives;
                stageCoverings[region] = coverings;
            }

            var pairOffset = TlbLayout.HeaderBytes;
            var stageOffset = pairOffset + TlbLayout.PairEntryBytes * (uint)pairCount;
            var programBase = stageOffset + TlbLayout.StageEntryBytes * (uint)stageCount;
            var stepCount = 0;
            foreach (var actives in stageActives)
                stepCount += actives.Length;
            var poolOffset = TlbLayout.Align16(programBase + TlbLayout.StepBytes * (uint)stepCount);

            var poolSpans = new (uint TrackRel, uint ClipRel, uint TrackCount, uint ClipCount)[pairCount];
            var poolCursor = poolOffset;
            for (var index = 0; index < pairCount; index++)
            {
                var pairAddress = pairOffset + TlbLayout.PairEntryBytes * (uint)index;
                var trackRel = poolCursor - pairAddress;
                poolCursor = TlbLayout.Align16(poolCursor + (uint)trackUniques[index] * (uint)trackValueBytes[index]);
                var clipRel = poolCursor - pairAddress;
                poolCursor = TlbLayout.Align16(poolCursor + (uint)clipUniques[index] * (uint)clipValueBytes[index]);
                poolSpans[index] = (trackRel, clipRel, (uint)trackUniques[index], (uint)clipUniques[index]);
            }
            var frameOffset = TlbLayout.Align16(poolCursor);

            var totalOccurrences = stepCount;
            var occurrenceOffsets = new uint[totalOccurrences];
            var occurrencePairs = new int[totalOccurrences];
            {
                var oi = 0;
                var cursor = frameOffset;
                for (var stage = 0; stage < stageCount; stage++)
                    for (var i = 0; i < stageActives[stage].Length; i++)
                    {
                        var laneIdx = stageActives[stage][i];
                        var lane = lanes[laneIdx];
                        occurrenceOffsets[oi] = cursor;
                        occurrencePairs[oi] = pairIndex[lane.Pair.Key];
                        oi++;
                        cursor += TlbLayout.SlotRowBytes;
                    }
            }

            var hotSlotsEnd = frameOffset;
            for (var oi = 0; oi < totalOccurrences; oi++)
                hotSlotsEnd += TlbLayout.SlotRowBytes;
            var markerOffset = hotSlotsEnd;
            var markerSize = 0u;
            foreach (var marker in doc.Markers)
                markerSize += 16u + (uint)((marker.Payload.Length + 3) & ~3);
            if (doc.Markers.Count > 0) markerOffset = (markerOffset + 7u) & ~7u;
            else markerOffset = 0u;
            var hotLength = doc.Markers.Count > 0 ? markerOffset + markerSize : hotSlotsEnd;
            var markerTypes = new List<(Type Track, Type Clip)>();
            var seenMarkerTypes = new HashSet<Type>();
            foreach (var marker in doc.Markers)
                if (seenMarkerTypes.Add(marker.MarkerType!))
                    markerTypes.Add((marker.MarkerType!, marker.MarkerType!));
            var trackRows = new List<(Type Type, int Pair, int Value, int Clips)>(doc.Tracks.Count);
            var clipRows = new List<(int Pair, int Value, uint Start, uint End)>(doc.Clips.Count);
            for (var ti = 0; ti < doc.Tracks.Count; ti++)
            {
                var info = doc.Tracks[ti];
                var lane = lanes.FirstOrDefault(l => l.TrackEntry == ti);
                trackRows.Add((info.TrackType!, lane == null ? -1 : pairIndex[lane.Pair.Key], lane?.TrackValueIndex ?? 0, info.ClipIds.Count));
                foreach (var clipId in info.ClipIds)
                {
                    var clip = doc.Clips[clipId];
                    clipRows.Add((pairIndex[doc.Pairs[clip.PairId].Key], clipValueIndex[clipId], clip.Start, clip.End));
                }
            }
            var tail = TlbMetadataBuilder.Build(pairTypes, labels, doc.Markers, trackRows, clipRows);
            var bytes = new byte[hotLength + tail.Length];

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), TlbLayout.Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), TlbLayout.Version);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), doc.Loops ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), duration);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)doc.Tracks.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)stageCount);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)pairCount);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), pairOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), stageOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), poolOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), frameOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), hotLength);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), doc.NextId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), doc.ChannelId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), doc.Hold ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), markerOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68), (uint)doc.Markers.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 0u);

            for (var index = 0; index < pairCount; index++)
            {
                var at = (int)pairOffset + 48 * index;
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), pairKeys[index]);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 8), TlbLayout.SlotRowBytes);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 12), poolSpans[index].TrackRel);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 16), poolSpans[index].TrackCount);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 20), (uint)trackValueBytes[index]);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 24), poolSpans[index].ClipRel);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 28), poolSpans[index].ClipCount);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 32), (uint)clipValueBytes[index]);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 40), TlbLayouting.Of(resolver, pairTypes[index].Item1, pairTypes[index].Item2));
            }

            var programOffset = programBase;
            var occurrenceCursor = 0;
            for (var stage = 0; stage < stageCount; stage++)
            {
                var at = stageOffset + TlbLayout.StageEntryBytes * (uint)stage;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)at), stageEdges[stage].Start);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)at + 4), stageEdges[stage].End);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)at + 8), programOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)at + 12), (uint)stageActives[stage].Length);
                for (var i = 0; i < stageActives[stage].Length; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)programOffset), occurrenceOffsets[occurrenceCursor]);
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)programOffset + 4), (uint)occurrencePairs[occurrenceCursor]);
                    programOffset += TlbLayout.StepBytes;
                    occurrenceCursor++;
                }
            }

            for (var index = 0; index < pairCount; index++)
            {
                var pairAddress = (int)(pairOffset + TlbLayout.PairEntryBytes * (uint)index);
                var trackBytes = trackValueBytes[index];
                var at = pairAddress + (int)poolSpans[index].TrackRel;
                foreach (var occurrence in trackSortedByPair[index])
                {
                    membersByPair[index][occurrence].TrackBytes.CopyTo(bytes.AsSpan(at));
                    at += trackBytes;
                }
                var pair = pairByPair[index];
                var clipBytes = clipValueBytes[index];
                at = pairAddress + (int)poolSpans[index].ClipRel;
                foreach (var occurrence in clipSortedByPair[index])
                {
                    pair.Pool.AsSpan(clipOffsetsByPair[index][occurrence], clipBytes).CopyTo(bytes.AsSpan(at));
                    at += clipBytes;
                }
            }

            {
                var oi = 0;
                for (var stage = 0; stage < stageCount; stage++)
                {
                    for (var i = 0; i < stageActives[stage].Length; i++)
                    {
                        var laneIdx = stageActives[stage][i];
                        var lane = lanes[laneIdx];
                        var covering = stageCoverings[stage][i];
                        var first = doc.Clips[covering.First];
                        var second = covering.Second >= 0 ? doc.Clips[covering.Second] : null;
                        var windowStart = first.Start;
                        var windowEnd = second != null ? Math.Max(first.End, second.End) : first.End;
                        var factorStart = 0u;
                        var factorSpan = 0u;
                        if (second != null)
                        {
                            factorStart = Math.Max(first.Start, second.Start);
                            factorSpan = Math.Min(first.End, second.End) - factorStart;
                        }
                        TlbLayout.WriteRow(
                            bytes,
                            (int)occurrenceOffsets[oi],
                            lane.TrackValueIndex,
                            clipValueIndex[covering.First],
                            covering.Second >= 0 ? clipValueIndex[covering.Second] : TlbLayout.NoClipIndex,
                            windowStart,
                            windowEnd,
                            factorStart,
                            factorSpan,
                            (byte)lane.TrackEntry);
                        oi++;
                    }
                }
            }

            if (doc.Markers.Count > 0)
            {
                var moff = (int)markerOffset;
                foreach (var marker in doc.Markers)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(moff), marker.Tick);
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(moff + 4), (uint)marker.Payload.Length);
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(moff + 8), marker.Key);
                    marker.Payload.CopyTo(bytes.AsSpan(moff + 16));
                    moff += 16 + ((marker.Payload.Length + 3) & ~3);
                }
            }

            tail.CopyTo(bytes, (int)hotLength);

            try
            {
                Tl.TimelineRef.Validate(bytes);
            }
            catch (ArgumentException ex)
            {
                var intern = Tl.Inspection.Tables().Intern;
                throw new BakeDiagnosticException($"TLB validation failed: {ex.Message}; content {NetCompat.Sha256Hex(bytes)[..16]}/{bytes.Length}B, intern live {intern.Live}/{intern.Distinct}, graveyard {intern.GraveyardBlocks}");
            }

            return bytes;
        }

        private delegate ReadOnlySpan<byte> SpanOf(int occurrence);

        private sealed record LaneCover(uint[] Starts, uint[] Ends, int[] Authored, int[] Ids, int Padded);

        private LaneCover[] BuildCoverTables(List<Lane> lanes)
        {
            const int width = 8;
            var tables = new LaneCover[lanes.Count];
            for (var laneIdx = 0; laneIdx < lanes.Count; laneIdx++)
            {
                var clips = lanes[laneIdx].ClipIds;
                var padded = (clips.Count + width - 1) & ~(width - 1);
                var starts = new uint[padded];
                var ends = new uint[padded];
                var authored = new int[padded];
                var ids = new int[padded];
                for (var i = 0; i < clips.Count; i++)
                {
                    var clip = doc.Clips[clips[i]];
                    starts[i] = clip.Start ^ 0x8000_0000u;
                    ends[i] = clip.End ^ 0x8000_0000u;
                    authored[i] = clip.AuthoredIndex;
                    ids[i] = clips[i];
                }
                for (var i = clips.Count; i < padded; i++)
                {
                    starts[i] = 0x7FFF_FFFFu;
                    ends[i] = 0xFFFF_FFFFu;
                }
                tables[laneIdx] = new LaneCover(starts, ends, authored, ids, padded);
            }
            return tables;
        }

        private static int[] DedupPool(int occurrenceCount, SpanOf spanOf, out int[] sortedUnique, out int uniqueCount, string overflowName)
        {
            var first = new Dictionary<ulong, int>(occurrenceCount);
            var uniqueOf = new int[occurrenceCount];
            var uniques = new List<int>(occurrenceCount);
            for (var o = 0; o < occurrenceCount; o++)
            {
                var span = spanOf(o);
                var hash = HashPoolSpan(span);
                if (first.TryGetValue(hash, out var u) && spanOf(uniques[u]).SequenceEqual(span))
                {
                    uniqueOf[o] = uniques[u];
                }
                else
                {
                    first[hash] = uniques.Count;
                    uniqueOf[o] = o;
                    uniques.Add(o);
                }
            }
            uniqueCount = uniques.Count;
            if (uniqueCount > 65535)
                throw new BakeDiagnosticException($"value pool overflow: {overflowName} has {uniqueCount} unique values in one pool; the fixed-width ushort slot index holds at most 65,535 entries.");
            uniques.Sort((x, y) => spanOf(x).SequenceCompareTo(spanOf(y)));
            sortedUnique = [.. uniques];
            var slotOf = new int[occurrenceCount];
            for (var slot = 0; slot < uniqueCount; slot++)
                slotOf[sortedUnique[slot]] = slot;
            for (var o = 0; o < occurrenceCount; o++)
                uniqueOf[o] = slotOf[uniqueOf[o]];
            return uniqueOf;
        }

        private static ulong HashPoolSpan(ReadOnlySpan<byte> span)
        {
            ulong hash = 14695981039346656037;
            while (span.Length >= 8)
            {
                hash = (hash ^ BinaryPrimitives.ReadUInt64LittleEndian(span)) * 1099511628211;
                span = span[8..];
            }
            if (!span.IsEmpty)
            {
                ulong tail = 0;
                for (var i = 0; i < span.Length; i++)
                    tail |= (ulong)span[i] << (8 * i);
                hash = (hash ^ tail) * 1099511628211;
            }
            return hash;
        }
    }
}
