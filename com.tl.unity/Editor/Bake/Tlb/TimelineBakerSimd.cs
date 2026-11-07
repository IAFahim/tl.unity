using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace Tl.Gen.Tlb;

internal static class TimelineBakerSimd
{
    internal static bool TryParseFast(byte[] utf8, BakerAssemblyResolver resolver, BakeWorkspace? workspace, out FastDoc doc, bool autoNamespace = false)
    {
        doc = null!;
        if (autoNamespace)
            return false;
        if (!JsonStructuralIndex.TryScan(utf8, workspace, out var index))
            return false;
        var walker = new SimdWalker(index, resolver, workspace);
        try
        {
            return walker.TryParse(out doc);
        }
        catch (SimdBailException)
        {
            return false;
        }
    }

    internal static JsonStructuralIndex? Scan(byte[] utf8) =>
        JsonStructuralIndex.TryScan(utf8, null, out var index) ? index : null;
}

internal sealed class SimdCursor
{
    private readonly ulong[] _structural;
    private readonly int _blocks;
    private readonly int _end;
    private int _block;
    private ulong _rest;
    private int _pending = -2;

    private SimdCursor(ulong[] structural, int blocks, int end, int block, ulong rest)
    {
        _structural = structural;
        _blocks = blocks;
        _end = end;
        _block = block;
        _rest = rest;
    }

    internal static SimdCursor FromBeginning(ulong[] structural, int blocks) =>
        new(structural, blocks, int.MaxValue, 0, blocks > 0 ? structural[0] : 0);

    internal static SimdCursor After(ulong[] structural, int blocks, int start, int end)
    {
        var block = start >> 6;
        if (block >= blocks)
            return new(structural, blocks, end, blocks, 0);
        var bit = start & 63;
        var rest = bit == 63 ? 0 : structural[block] & ~((2UL << bit) - 1);
        return new(structural, blocks, end, block, rest);
    }

    internal static SimdCursor From(ulong[] structural, int blocks, int start, int end)
    {
        var block = start >> 6;
        if (block >= blocks)
            return new(structural, blocks, end, blocks, 0);
        var bit = start & 63;
        var rest = structural[block] & ~((1UL << bit) - 1);
        return new(structural, blocks, end, block, rest);
    }

    internal int Peek()
    {
        if (_pending != -2)
            return _pending;
        _pending = Advance();
        return _pending;
    }

    internal int Take()
    {
        var pos = Peek();
        _pending = -2;
        return pos;
    }

    private int Advance()
    {
        while (true)
        {
            while (_rest == 0)
            {
                _block++;
                if (_block >= _blocks)
                    return -1;
                _rest = _structural[_block];
            }
            var bit = BitOperations.TrailingZeroCount(_rest);
            _rest &= _rest - 1;
            var pos = (_block << 6) + bit;
            return pos >= _end ? -1 : pos;
        }
    }
}

internal sealed unsafe class SimdWalker
{
    private const byte OpenBrace = (byte)'{';
    private const byte CloseBrace = (byte)'}';
    private const byte OpenBracket = (byte)'[';
    private const byte CloseBracket = (byte)']';
    private const byte Colon = (byte)':';
    private const byte Comma = (byte)',';
    private const byte Quote = (byte)'"';

    private readonly byte[] _utf8;
    private readonly ulong[] _structural;
    private readonly ulong[] _quotes;
    private readonly int _blocks;
    private readonly BakeWorkspace? _workspace;
    private readonly BakerAssemblyResolver _resolver;
    private SimdCursor _cursor;
    private ulong[] _seenBits = new ulong[8];
    private int _seenWords;

    internal SimdWalker(JsonStructuralIndex index, BakerAssemblyResolver resolver, BakeWorkspace? workspace = null)
    {
        _utf8 = index.Utf8;
        _structural = index.Structural;
        _quotes = index.Quotes;
        _blocks = index.Blocks;
        _workspace = workspace;
        _resolver = resolver;
        _cursor = SimdCursor.FromBeginning(_structural, _blocks);
    }

    internal bool TryParse(out FastDoc doc)
    {
        doc = new FastDoc { Utf8 = _utf8, Resolver = _resolver, Workspace = _workspace };
        if (_workspace != null)
        {
            _workspace.Warm(doc);
            doc.LoanStructural = _structural;
            doc.LoanQuotes = _quotes;
        }
        ParseRoot(doc);
        return true;
    }

    [DoesNotReturn]
    private void Bail() => throw new SimdBailException();

    private int Take()
    {
        var pos = _cursor.Take();
        if (pos < 0)
            Bail();
        return pos;
    }

    private int NextQuote(int from)
    {
        var block = from >> 6;
        var bit = from & 63;
        var rest = bit == 63 ? 0 : _quotes[block] & ~((2UL << bit) - 1);
        while (true)
        {
            if (rest != 0)
                return (block << 6) + BitOperations.TrailingZeroCount(rest);
            block++;
            if (block >= _blocks)
                return -1;
            rest = _quotes[block];
        }
    }

    private void CheckGap(int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            var b = _utf8[i];
            if (b != 0x20 && b != 0x09 && b != 0x0A && b != 0x0D)
                Bail();
        }
    }

    private int StringClose(int openQuote)
    {
        var close = NextQuote(openQuote + 1);
        if (close < 0)
            Bail();
        return close;
    }

    private string StringText(int openQuote, int closeQuote) =>
        Encoding.UTF8.GetString(_utf8, openQuote + 1, closeQuote - openQuote - 1);

    private static bool NameIs(ReadOnlySpan<byte> span, ReadOnlySpan<byte> name) => span.SequenceEqual(name);

    private void ParseRoot(FastDoc doc)
    {
        var open = Take();
        if (_utf8[open] != OpenBrace)
            Bail();
        CheckGap(0, open);
        var seenDuration = false;
        var seenTracks = false;
        var seenName = false;
        var seenLoop = false;
        var first = Take();
        if (_utf8[first] != Quote)
            Bail();
        CheckGap(open + 1, first);
        var valueEnd = RootMember(doc, first, ref seenDuration, ref seenTracks, ref seenName, ref seenLoop);
        int close;
        while (true)
        {
            var separator = Take();
            CheckGap(valueEnd, separator);
            var b = _utf8[separator];
            if (b == Comma)
            {
                var nameOpen = Take();
                if (_utf8[nameOpen] != Quote)
                    Bail();
                CheckGap(separator + 1, nameOpen);
                valueEnd = RootMember(doc, nameOpen, ref seenDuration, ref seenTracks, ref seenName, ref seenLoop);
                continue;
            }
            if (b == CloseBrace)
            {
                close = separator;
                break;
            }
            Bail();
        }
        if (!seenDuration || !seenTracks)
            Bail();
        if (doc.Duration > ushort.MaxValue)
            Bail();
        var trailing = _cursor.Peek();
        if (trailing >= 0)
            Bail();
        CheckGap(close + 1, _utf8.Length);
    }

    private int RootMember(FastDoc doc, int nameOpen, ref bool seenDuration, ref bool seenTracks, ref bool seenName, ref bool seenLoop)
    {
        var nameClose = StringClose(nameOpen);
        var colon = ExpectColon(nameClose);
        var bound = PeekValueBound();
        var valueStart = SkipWhitespaceTo(colon + 1, bound);
        var isStructuralValue = valueStart == bound;
        var firstByte = isStructuralValue ? _utf8[bound] : _utf8[valueStart];
        var name = _utf8.AsSpan(nameOpen + 1, nameClose - nameOpen - 1);
        int valueEnd;
        if (NameIs(name, "duration"u8))
        {
            if (seenDuration)
                Bail();
            seenDuration = true;
            if (isStructuralValue)
                Bail();
            if (!FastNumber.ScanUnsigned32(_utf8, colon + 1, bound, out var tokenEnd, out var duration))
                Bail();
            doc.Duration = duration;
            valueEnd = tokenEnd;
        }
        else if (NameIs(name, "tracks"u8))
        {
            if (seenTracks)
                Bail();
            seenTracks = true;
            if (!isStructuralValue || firstByte != OpenBracket)
                Bail();
            valueEnd = TracksValue(doc, Take());
        }
        else if (NameIs(name, "name"u8))
        {
            if (seenName)
                Bail();
            seenName = true;
            if (!isStructuralValue || firstByte != Quote)
                Bail();
            valueEnd = LabelValue(Take(), out var text);
            doc.RootName = text;
        }
        else if (NameIs(name, "loop"u8))
        {
            if (seenLoop)
                Bail();
            seenLoop = true;
            if (isStructuralValue)
                Bail();
            valueEnd = LiteralValue(colon + 1, bound, out doc.Loops);
        }
        else
        {
            Bail();
            valueEnd = 0;
        }
        return valueEnd;
    }

    private int SkipWhitespaceTo(int from, int bound)
    {
        var i = from;
        while (i < bound && IsWhitespace(_utf8[i]))
            i++;
        return i;
    }

    private int ExpectColon(int nameClose)
    {
        var colon = Take();
        if (_utf8[colon] != Colon)
            Bail();
        CheckGap(nameClose + 1, colon);
        return colon;
    }

    private int PeekValueBound()
    {
        var bound = _cursor.Peek();
        if (bound < 0)
            Bail();
        return bound;
    }

    private int LabelValue(int value, out string text)
    {
        if (_utf8[value] != Quote)
            Bail();
        var close = StringClose(value);
        text = StringText(value, close);
        return close + 1;
    }

    private int LiteralValue(int from, int bound, out bool flag)
    {
        var i = from;
        while (i < bound && IsWhitespace(_utf8[i]))
            i++;
        var tokenStart = i;
        while (i < bound && !IsWhitespace(_utf8[i]) && !IsStructuralByte(_utf8[i]))
            i++;
        var token = _utf8.AsSpan(tokenStart, i - tokenStart);
        if (!ScalarTailWhitespace(i, bound))
            Bail();
        if (token.Length == 4 && token.SequenceEqual("true"u8))
        {
            flag = true;
            return i;
        }
        if (token.Length == 5 && token.SequenceEqual("false"u8))
        {
            flag = false;
            return i;
        }
        Bail();
        flag = false;
        return 0;
    }

    private int TracksValue(FastDoc doc, int value)
    {
        if (_utf8[value] != OpenBracket)
            Bail();
        var element = Take();
        if (_utf8[element] == CloseBracket)
        {
            CheckGap(value + 1, element);
            return element + 1;
        }
        if (PartitionDegree() >= 2
            && TrackElements(value, out var opens, out var arrayClose)
            && opens.Length >= MinPartitionTracks
            && arrayClose - value >= MinPartitionBytes)
            return PartitionedTracks(doc, opens, arrayClose, value);
        var clipValue = 0;
        while (true)
        {
            if (_utf8[element] != OpenBrace)
                Bail();
            CheckGap(clipValue == 0 ? value + 1 : clipValue + 1, element);
            var trackClose = TrackObject(doc, element);
            var separator = Take();
            CheckGap(trackClose + 1, separator);
            var b = _utf8[separator];
            if (b == CloseBracket)
                return separator + 1;
            if (b != Comma)
                Bail();
            clipValue = separator;
            element = Take();
            if (_utf8[element] == CloseBracket)
                Bail();
        }
    }

    private const int MinPartitionTracks = 8;
    private const int MinPartitionBytes = 1 << 20;

    [SuppressMessage("ReSharper", "EmptyGeneralCatchClause", Justification = "affinity probe may fail on locked-down hosts; fall back to processor count")]
    private static int PartitionDegree()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("TL_BAKE_PARTITIONS"), out var forced) && forced >= 1)
            return forced;
        var available = Environment.ProcessorCount;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            try
            {
                var affinity = BitOperations.PopCount((ulong)Process.GetCurrentProcess().ProcessorAffinity);
                if (affinity > 0)
                    available = Math.Min(available, affinity);
            }
            catch (Exception) { }
        }
        return Math.Min(16, Math.Max(1, available / 2));
    }

    private bool TrackElements(int openBracket, out int[] opens, out int arrayClose)
    {
        opens = [];
        arrayClose = -1;
        var depth = 1;
        var i = openBracket + 1;
        var block = i >> 6;
        var bit = i & 63;
        var rest = _structural[block] & ~((1UL << bit) - 1);
        var list = new List<int>();
        while (true)
        {
            while (rest == 0)
            {
                block++;
                if (block >= _blocks)
                    return false;
                rest = _structural[block];
            }
            var pos = (block << 6) + BitOperations.TrailingZeroCount(rest);
            rest &= rest - 1;
            var c = _utf8[pos];
            if (depth == 1)
            {
                if (c == OpenBrace) { list.Add(pos); depth = 2; continue; }
                if (c == CloseBracket) { arrayClose = pos; opens = [.. list]; return true; }
                if (c != Comma) return false;
            }
            else if (c is OpenBrace or OpenBracket) depth++;
            else if (c is CloseBrace or CloseBracket) depth--;
        }
    }

    [SuppressMessage("ReSharper", "AccessToModifiedClosure", Justification = "live capture consumed inside the invoked body")]
    private int PartitionedTracks(FastDoc doc, int[] opens, int arrayClose, int value)
    {
        var degree = Math.Min(PartitionDegree(), opens.Length);
        var sliceStarts = new int[degree];
        var sliceEnds = new int[degree];
        var per = opens.Length / degree;
        var extra = opens.Length % degree;
        var at = 0;
        for (var k = 0; k < degree; k++)
        {
            var take = per + (k < extra ? 1 : 0);
            sliceStarts[k] = k == 0 ? value + 1 : opens[at];
            at += take;
            sliceEnds[k] = k == degree - 1 ? arrayClose : opens[at];
        }

        var fragments = new FastDoc[degree];
        var bailed = 0;
        var fault = (Exception?)null;
        if (degree == 1)
        {
            var frag = new FastDoc { Utf8 = _utf8, Resolver = _resolver };
            var walker = new SimdWalker(_structural, _quotes, _blocks, _utf8, _resolver);
            walker.ParseTrackSlice(frag, sliceStarts[0], sliceEnds[0], true);
            fragments[0] = frag;
        }
        else
        {
            var workers = new Thread[degree - 1];
            for (var k = 1; k < degree; k++)
            {
                var slice = k;
                workers[k - 1] = new Thread(() => RunSlice(fragments, sliceStarts, sliceEnds, slice, degree, ref bailed, ref fault)) { IsBackground = true };
            }
            for (var k = 1; k < degree; k++)
                workers[k - 1].Start();
            RunSlice(fragments, sliceStarts, sliceEnds, 0, degree, ref bailed, ref fault);
            for (var k = 1; k < degree; k++)
                workers[k - 1].Join();
        }
        if (bailed != 0)
            Bail();
        if (fault != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();
        MergeTracks(doc, fragments);
        _cursor = SimdCursor.From(_structural, _blocks, arrayClose + 1, int.MaxValue);
        return arrayClose + 1;
    }

    private void RunSlice(FastDoc[] fragments, int[] sliceStarts, int[] sliceEnds, int k, int degree, ref int bailed, ref Exception? fault)
    {
        var frag = new FastDoc { Utf8 = _utf8, Resolver = _resolver };
        try
        {
            var walker = new SimdWalker(_structural, _quotes, _blocks, _utf8, _resolver);
            walker.ParseTrackSlice(frag, sliceStarts[k], sliceEnds[k], k == degree - 1);
        }
        catch (SimdBailException)
        {
            Volatile.Write(ref bailed, 1);
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref fault, e, null);
        }
        fragments[k] = frag;
    }

    private SimdWalker(ulong[] structural, ulong[] quotes, int blocks, byte[] utf8, BakerAssemblyResolver resolver)
    {
        _utf8 = utf8;
        _structural = structural;
        _quotes = quotes;
        _blocks = blocks;
        _workspace = null;
        _resolver = resolver;
        _cursor = null!;
    }

    private void ParseTrackSlice(FastDoc doc, int start, int end, bool last)
    {
        _cursor = SimdCursor.From(_structural, _blocks, start, end);
        var expectOpen = true;
        var boundary = start;
        while (true)
        {
            var pos = _cursor.Take();
            if (pos < 0)
            {
                if (expectOpen && last) Bail();
                CheckGap(boundary, end);
                return;
            }
            var b = _utf8[pos];
            if (b == OpenBrace)
            {
                if (!expectOpen) Bail();
                CheckGap(boundary, pos);
                var close = TrackObject(doc, pos);
                boundary = close + 1;
                expectOpen = false;
            }
            else if (b == Comma)
            {
                if (expectOpen) Bail();
                CheckGap(boundary, pos);
                boundary = pos + 1;
                expectOpen = true;
            }
            else
            {
                Bail();
            }
        }
    }

    private static void MergeTracks(FastDoc doc, FastDoc[] fragments)
    {
        var remaps = new int[fragments.Length][];
        var bases = new int[fragments.Length][];
        var poolLength = new List<int>();
        for (var p = 0; p < fragments.Length; p++)
        {
            var frag = fragments[p];
            var remap = new int[frag.Pairs.Count];
            var localBases = new int[frag.Pairs.Count];
            for (var q = 0; q < frag.Pairs.Count; q++)
            {
                var pair = frag.Pairs[q];
                var key = (pair.TrackType, pair.ClipType);
                if (!doc.PairIds.TryGetValue(key, out var gid))
                {
                    gid = doc.Pairs.Count;
                    doc.Pairs.Add(new FastPairInfo
                    {
                        TrackType = pair.TrackType,
                        ClipType = pair.ClipType,
                        Key = pair.Key,
                        ClipSize = pair.ClipSize,
                        TrackSize = pair.TrackSize,
                        Helper = pair.Helper,
                        Fields = pair.Fields,
                    });
                    doc.PairIds[key] = gid;
                    poolLength.Add(0);
                }
                remap[q] = gid;
                localBases[q] = poolLength[gid];
                poolLength[gid] += pair.PoolLength;
            }
            remaps[p] = remap;
            bases[p] = localBases;
        }

        for (var gid = 0; gid < doc.Pairs.Count; gid++)
        {
            doc.Pairs[gid].Pool = new byte[poolLength[gid]];
            doc.Pairs[gid].PoolLength = poolLength[gid];
        }
        for (var p = 0; p < fragments.Length; p++)
        {
            var frag = fragments[p];
            for (var q = 0; q < frag.Pairs.Count; q++)
            {
                var pair = frag.Pairs[q];
                if (pair.PoolLength > 0)
                    Buffer.BlockCopy(pair.Pool, 0, doc.Pairs[remaps[p][q]].Pool, bases[p][q], pair.PoolLength);
            }
        }

        for (var p = 0; p < fragments.Length; p++)
        {
            var frag = fragments[p];
            var clipBase = doc.Clips.Count;
            foreach (var info in frag.Tracks)
            {
                for (var c = 0; c < info.ClipIds.Count; c++)
                    info.ClipIds[c] += clipBase;
                doc.Tracks.Add(info);
            }
            foreach (var clip in frag.Clips)
            {
                if (clip.PairId >= 0)
                {
                    var local = clip.PairId;
                    clip.PairId = remaps[p][local];
                    if (clip.PayloadOffset >= 0)
                        clip.PayloadOffset += bases[p][local];
                }
                doc.Clips.Add(clip);
            }
            foreach (var entry in frag.ResolveCache)
                doc.ResolveCache[entry.Key] = entry.Value;
        }
    }

    private int TrackObject(FastDoc doc, int open)
    {
        var info = new FastTrackInfo();
        var seenName = false;
        var seenNs = false;
        var seenType = false;
        var seenClips = false;
        var seenData = false;
        var nameOpen = Take();
        if (_utf8[nameOpen] != Quote)
            Bail();
        CheckGap(open + 1, nameOpen);
        var valueEnd = TrackMember(doc, info, nameOpen, ref seenName, ref seenNs, ref seenType, ref seenClips, ref seenData);
        int close;
        while (true)
        {
            var separator = Take();
            CheckGap(valueEnd, separator);
            var b = _utf8[separator];
            if (b == Comma)
            {
                nameOpen = Take();
                if (_utf8[nameOpen] != Quote)
                    Bail();
                CheckGap(separator + 1, nameOpen);
                valueEnd = TrackMember(doc, info, nameOpen, ref seenName, ref seenNs, ref seenType, ref seenClips, ref seenData);
                continue;
            }
            if (b == CloseBrace)
            {
                close = separator;
                break;
            }
            Bail();
        }
        if (!info.HaveNs || !info.HaveType || !seenClips || info.TrackType == null)
            Bail();
        doc.Tracks.Add(info);
        return close;
    }

    private int TrackMember(FastDoc doc, FastTrackInfo info, int nameOpen, ref bool seenName, ref bool seenNs, ref bool seenType, ref bool seenClips, ref bool seenData)
    {
        var nameClose = StringClose(nameOpen);
        var colon = ExpectColon(nameClose);
        var bound = PeekValueBound();
        var valueStart = SkipWhitespaceTo(colon + 1, bound);
        var isStructuralValue = valueStart == bound;
        var firstByte = isStructuralValue ? _utf8[bound] : _utf8[valueStart];
        var name = _utf8.AsSpan(nameOpen + 1, nameClose - nameOpen - 1);
        int valueEnd;
        if (NameIs(name, "namespace"u8))
        {
            if (seenNs || seenType)
                Bail();
            seenNs = true;
            if (!isStructuralValue || firstByte != Quote)
                Bail();
            valueEnd = BareValue(Take(), out var text, out var dotted);
            if (dotted)
                Bail();
            info.Ns = text;
            info.HaveNs = true;
        }
        else if (NameIs(name, "type"u8))
        {
            if (seenType || !seenNs)
                Bail();
            seenType = true;
            if (!isStructuralValue || firstByte != Quote)
                Bail();
            valueEnd = BareValue(Take(), out var text, out var dotted);
            if (dotted)
                Bail();
            info.TypeName = text;
            info.HaveType = true;
            if (!ResolveTrackType(doc, info))
                Bail();
        }
        else if (NameIs(name, "name"u8))
        {
            if (seenName)
                Bail();
            seenName = true;
            if (!isStructuralValue || firstByte != Quote)
                Bail();
            valueEnd = LabelValue(Take(), out var text);
            info.Name = text;
        }
        else if (NameIs(name, "clips"u8))
        {
            if (seenClips)
                Bail();
            seenClips = true;
            if (info.TrackType == null || !isStructuralValue || firstByte != OpenBracket)
                Bail();
            valueEnd = ClipsValue(doc, info, Take());
        }
        else if (NameIs(name, "data"u8))
        {
            if (seenData)
                Bail();
            seenData = true;
            if (info.TrackType == null || !TimelineBakerFast.IsUnmanagedCached(info.TrackType) || !isStructuralValue || firstByte != OpenBrace)
                Bail();
            valueEnd = TrackDataValue(doc, info, Take());
        }
        else
        {
            Bail();
            valueEnd = 0;
        }
        return valueEnd;
    }

    private bool ResolveTrackType(FastDoc doc, FastTrackInfo info)
    {
        var key = (info.Ns, info.TypeName, info.Asm);
        if (doc.ResolveCache.TryGetValue(key, out var cached))
        {
            info.TrackType = cached;
            return TimelineBakerFast.IsUnmanagedCached(cached);
        }
        Type resolved;
        try
        {
            resolved = _resolver.ResolveType(info.Ns, info.TypeName, info.Asm, "track ?");
        }
        catch (BakeDiagnosticException)
        {
            return false;
        }
        doc.ResolveCache[key] = resolved;
        info.TrackType = resolved;
        return TimelineBakerFast.IsUnmanagedCached(resolved);
    }

    private int BareValue(int value, out string text, out bool dotted)
    {
        if (_utf8[value] != Quote)
            Bail();
        var close = StringClose(value);
        text = StringText(value, close);
        dotted = false;
        for (var i = value + 1; i < close; i++)
        {
            var b = _utf8[i];
            if (b is (byte)'.' or (byte)',' or (byte)'+' or (byte)'=')
            {
                dotted = true;
                break;
            }
        }
        return close + 1;
    }

    private int ClipsValue(FastDoc doc, FastTrackInfo info, int value)
    {
        if (_utf8[value] != OpenBracket)
            Bail();
        var trackIndex = doc.Tracks.Count;
        var element = Take();
        if (_utf8[element] == CloseBracket)
        {
            CheckGap(value + 1, element);
            return element + 1;
        }
        var clipIndex = 0;
        var previous = value;
        while (true)
        {
            if (_utf8[element] != OpenBrace)
                Bail();
            CheckGap(previous + 1, element);
            var clipClose = ClipObject(doc, info, trackIndex, clipIndex, element);
            clipIndex++;
            var separator = Take();
            CheckGap(clipClose + 1, separator);
            var b = _utf8[separator];
            if (b == CloseBracket)
                return separator + 1;
            if (b != Comma)
                Bail();
            previous = separator;
            element = Take();
            if (_utf8[element] == CloseBracket)
                Bail();
        }
    }

    private int ClipObject(FastDoc doc, FastTrackInfo info, int _, int clipIndex, int open)
    {
        var clip = new FastClip
        {
            ClipIndex = clipIndex,
            AuthoredIndex = doc.Clips.Count,
        };
        var seenName = false;
        var seenNs = false;
        var seenType = false;
        var seenStart = false;
        var seenEnd = false;
        var seenData = false;
        var nameOpen = Take();
        if (_utf8[nameOpen] != Quote)
            Bail();
        CheckGap(open + 1, nameOpen);
        var valueEnd = ClipMember(doc, info, clip, nameOpen, ref seenName, ref seenNs, ref seenType, ref seenStart, ref seenEnd, ref seenData);
        int close;
        while (true)
        {
            var separator = Take();
            CheckGap(valueEnd, separator);
            var b = _utf8[separator];
            if (b == Comma)
            {
                nameOpen = Take();
                if (_utf8[nameOpen] != Quote)
                    Bail();
                CheckGap(separator + 1, nameOpen);
                valueEnd = ClipMember(doc, info, clip, nameOpen, ref seenName, ref seenNs, ref seenType, ref seenStart, ref seenEnd, ref seenData);
                continue;
            }
            if (b == CloseBrace)
            {
                close = separator;
                break;
            }
            Bail();
        }
        if (!clip.NsSet || !clip.TypeSet || !seenStart || !seenEnd)
            Bail();
        if (clip.PairId < 0)
            Bail();
        doc.Clips.Add(clip);
        info.ClipIds.Add(doc.Clips.Count - 1);
        return close;
    }

    private int ClipMember(FastDoc doc, FastTrackInfo info, FastClip clip, int nameOpen, ref bool seenName, ref bool seenNs, ref bool seenType, ref bool seenStart, ref bool seenEnd, ref bool seenData)
    {
        var nameClose = StringClose(nameOpen);
        var colon = ExpectColon(nameClose);
        var bound = PeekValueBound();
        var valueStart = SkipWhitespaceTo(colon + 1, bound);
        var isStructuralValue = valueStart == bound;
        var firstByte = isStructuralValue ? _utf8[bound] : _utf8[valueStart];
        var name = _utf8.AsSpan(nameOpen + 1, nameClose - nameOpen - 1);
        int valueEnd;
        if (NameIs(name, "namespace"u8))
        {
            if (seenNs || seenType || !isStructuralValue || firstByte != Quote)
                Bail();
            seenNs = true;
            valueEnd = BareValue(Take(), out var text, out var dotted);
            if (dotted)
                Bail();
            clip.Ns = text;
            clip.NsSet = true;
        }
        else if (NameIs(name, "type"u8))
        {
            if (seenType || !seenNs)
                Bail();
            seenType = true;
            if (!isStructuralValue || firstByte != Quote)
                Bail();
            valueEnd = BareValue(Take(), out var text, out var dotted);
            if (dotted)
                Bail();
            clip.TypeName = text;
            clip.TypeSet = true;
            if (info.TrackType == null || !ResolveClipPair(doc, clip, info.TrackType))
                Bail();
        }
        else if (NameIs(name, "name"u8))
        {
            if (seenName)
                Bail();
            seenName = true;
            if (!isStructuralValue || firstByte != Quote)
                Bail();
            valueEnd = LabelValue(Take(), out var text);
            clip.Name = text;
        }
        else if (NameIs(name, "start"u8))
        {
            if (seenStart)
                Bail();
            seenStart = true;
            if (isStructuralValue)
                Bail();
            if (!FastNumber.ScanUnsigned32(_utf8, colon + 1, bound, out var tokenEnd, out var start))
                Bail();
            clip.Start = start;
            valueEnd = tokenEnd;
        }
        else if (NameIs(name, "end"u8))
        {
            if (seenEnd)
                Bail();
            seenEnd = true;
            if (isStructuralValue)
                Bail();
            if (!FastNumber.ScanUnsigned32(_utf8, colon + 1, bound, out var tokenEnd, out var end))
                Bail();
            clip.End = end;
            valueEnd = tokenEnd;
        }
        else if (NameIs(name, "data"u8))
        {
            if (seenData)
                Bail();
            seenData = true;
            if (clip.PairId < 0 || !isStructuralValue || firstByte != OpenBrace)
                Bail();
            valueEnd = ClipDataValue(doc, clip, Take());
        }
        else
        {
            Bail();
            valueEnd = 0;
        }
        return valueEnd;
    }

    private bool ResolveClipPair(FastDoc doc, FastClip clip, Type trackType)
    {
        var key = (clip.Ns, clip.TypeName, clip.Asm);
        if (!doc.ResolveCache.TryGetValue(key, out var clipType))
        {
            Type resolved;
            try
            {
                resolved = _resolver.ResolveType(clip.Ns, clip.TypeName, clip.Asm, "clip ?");
            }
            catch (BakeDiagnosticException)
            {
                return false;
            }
            doc.ResolveCache[key] = resolved;
            clipType = resolved;
        }
        clip.ClipType = clipType;
        if (!TimelineBakerFast.IsUnmanagedCached(trackType)
            || !TimelineBakerFast.IsUnmanagedCached(clipType)
            || !TimelineBakerFast.IsBlendPairCached(trackType, clipType))
            return false;
        clip.PairId = TimelineBakerFast.EnsurePair(doc, trackType, clipType);
        return clip.PairId >= 0;
    }

    private int TrackDataValue(FastDoc _, FastTrackInfo info, int value)
    {
        if (_utf8[value] != OpenBrace)
            Bail();
        var close = SkipContainer(value);
        var table = TimelineBakerFast.TableFor(info.TrackType!);
        PrepareSeenBits(table);
        DupWalkRegion(value, close, table);
        info.DataStart = value;
        info.DataEnd = close + 1;
        info.DataCaptured = true;
        return close + 1;
    }

    private int SkipContainer(int _)
    {
        var depth = 1;
        while (true)
        {
            var pos = Take();
            var b = _utf8[pos];
            if (b is OpenBrace or OpenBracket)
            {
                depth++;
            }
            else if (b is CloseBrace or CloseBracket)
            {
                depth--;
                if (depth == 0)
                    return pos;
            }
        }
    }

    private void DupWalkRegion(int open, int close, FieldTable table)
    {
        var cursor = SimdCursor.After(_structural, _blocks, open, close + 1);
        var first = cursor.Take();
        if (_utf8[first] == CloseBrace)
        {
            CheckGap(open + 1, first);
            return;
        }
        if (_utf8[first] != Quote)
            Bail();
        CheckGap(open + 1, first);
        RegionMember(cursor, table, first, open, close + 1);
        while (true)
        {
            var separator = cursor.Take();
            if (separator < 0)
                Bail();
            CheckGap(_lastValueEnd, separator);
            var b = _utf8[separator];
            if (b == Comma)
            {
                var nameOpen = cursor.Take();
                if (nameOpen < 0 || _utf8[nameOpen] != Quote)
                    Bail();
                CheckGap(separator + 1, nameOpen);
                RegionMember(cursor, table, nameOpen, separator, close + 1);
                continue;
            }
            if (b == CloseBrace)
                break;
            Bail();
        }
    }

    private int _lastValueEnd;

    private void RegionMember(SimdCursor cursor, FieldTable table, int nameOpen, int _, int regionEnd)
    {
        var nameClose = NextQuote(nameOpen + 1);
        if (nameClose < 0)
            Bail();
        var colon = cursor.Take();
        if (colon < 0 || _utf8[colon] != Colon)
            Bail();
        CheckGap(nameClose + 1, colon);
        var bound = cursor.Peek();
        var end = bound < 0 ? regionEnd : bound;
        var valueStart = SkipWhitespaceTo(colon + 1, end);
        if (valueStart == bound)
            Bail();
        var name = _utf8.AsSpan(nameOpen + 1, nameClose - nameOpen - 1);
        var entry = table.Lookup(name);
        if (entry == null)
            Bail();
        MarkSeen(entry.Ordinal);
        int valueEnd;
        var first = _utf8[valueStart];
        if (IsStructuralValueAt(valueStart, bound))
        {
            if (first == Quote)
            {
                var close = NextQuote(valueStart + 1);
                if (close < 0)
                    Bail();
                valueEnd = close + 1;
            }
            else
            {
                Bail();
                valueEnd = 0;
            }
        }
        else if (first is (byte)'t' or (byte)'f' or (byte)'n')
        {
            var i = valueStart;
            while (i < end && !IsWhitespace(_utf8[i]) && !IsStructuralByte(_utf8[i]))
                i++;
            var token = _utf8.AsSpan(valueStart, i - valueStart);
            var literalOk = (token.Length == 4 && token.SequenceEqual("true"u8))
                || (token.Length == 5 && token.SequenceEqual("false"u8))
                || (token.Length == 4 && token.SequenceEqual("null"u8));
            if (!literalOk || !ScalarTailWhitespace(i, end))
                Bail();
            valueEnd = i;
        }
        else if (!GrammarValid(colon + 1, bound, out valueEnd))
        {
            Bail();
        }
        _lastValueEnd = valueEnd;
    }

    private bool IsStructuralValueAt(int valueStart, int bound) => valueStart == bound;

    private void MarkSeen(int ordinal)
    {
        if (ordinal < 0 || (_seenBits[ordinal >> 6] & (1UL << (ordinal & 63))) != 0)
            Bail();
        _seenBits[ordinal >> 6] |= 1UL << (ordinal & 63);
    }

    private void PrepareSeenBits(FieldTable table)
    {
        _seenWords = Math.Max(1, (table.Entries.Length + 63) / 64);
        if (_seenBits.Length < _seenWords)
            _seenBits = new ulong[_seenWords];
        Array.Clear(_seenBits, 0, _seenWords);
    }

    private int ClipDataValue(FastDoc doc, FastClip clip, int value)
    {
        if (_utf8[value] != OpenBrace)
            Bail();
        var pair = doc.Pairs[clip.PairId];
        var table = pair.Fields;
        PrepareFieldOffsets(table, pair.ClipType);
        PrepareSeenBits(table);
        pair.EnsurePool(pair.ClipSize);
        clip.PayloadOffset = pair.PoolLength;
        pair.PoolLength += pair.ClipSize;
        Array.Clear(pair.Pool, clip.PayloadOffset, pair.ClipSize);
        fixed (byte* pool = pair.Pool)
        {
            var slot = pool + clip.PayloadOffset;
            var nameOpen = Take();
            if (_utf8[nameOpen] == CloseBrace)
            {
                CheckGap(value + 1, nameOpen);
                return CloseDataObject(doc, clip, value, nameOpen);
            }
            if (_utf8[nameOpen] != Quote)
                Bail();
            CheckGap(value + 1, nameOpen);
            var valueEnd = DataMember(table, slot, nameOpen);
            while (true)
            {
                var separator = Take();
                CheckGap(valueEnd, separator);
                var b = _utf8[separator];
                if (b == Comma)
                {
                    nameOpen = Take();
                    if (_utf8[nameOpen] != Quote)
                        Bail();
                    CheckGap(separator + 1, nameOpen);
                    valueEnd = DataMember(table, slot, nameOpen);
                    continue;
                }
                if (b == CloseBrace)
                    return CloseDataObject(doc, clip, value, separator);
                Bail();
            }
        }
    }

    private int CloseDataObject(FastDoc _, FastClip clip, int open, int close)
    {
        clip.DataStart = open;
        clip.DataEnd = close + 1;
        clip.DataCaptured = true;
        clip.PopulateError = null;
        clip.PopulateDone = true;
        return close + 1;
    }

    private int DataMember(FieldTable table, byte* slot, int nameOpen)
    {
        var nameClose = StringClose(nameOpen);
        var colon = ExpectColon(nameClose);
        var bound = PeekValueBound();
        var valueStart = SkipWhitespaceTo(colon + 1, bound);
        if (valueStart == bound)
            Bail();
        var name = _utf8.AsSpan(nameOpen + 1, nameClose - nameOpen - 1);
        var entry = table.Lookup(name);
        if (entry == null)
            Bail();
        MarkSeen(entry.Ordinal);
        if (entry.Kind == FieldKind.Unsupported || entry.ByteOffset < 0)
            Bail();
        var target = slot + entry.ByteOffset;
        var first = _utf8[valueStart];
        if (first == (byte)'t' || first == (byte)'f')
        {
            if (entry.Kind != FieldKind.Bool)
                Bail();
            var literalEnd = LiteralValue(colon + 1, bound, out var flag);
            *(bool*)target = flag;
            return literalEnd;
        }
        switch (entry.Kind)
        {
            case FieldKind.Bool:
                Bail();
                return 0;
            case FieldKind.Float:
                if (!FastNumber.ScanNumberParts(_utf8, colon + 1, bound, out var floatEnd, out var fm, out var fe, out var fneg, out var fhard))
                    Bail();
                var fval = FastNumber.PartsToFloat(fm, fe, fneg, fhard);
                if (float.IsNaN(fval))
                    Bail();
                *(float*)target = fval;
                return floatEnd;
            case FieldKind.Double:
                if (!FastNumber.ScanNumberParts(_utf8, colon + 1, bound, out var doubleEnd, out var dm, out var de, out var dneg, out var dhard))
                    Bail();
                var dval = FastNumber.PartsToDouble(dm, de, dneg, dhard);
                if (double.IsNaN(dval))
                    Bail();
                *(double*)target = dval;
                return doubleEnd;
            case FieldKind.ULong:
                if (!FastNumber.ScanUnsigned64(_utf8, colon + 1, bound, out var ulongEnd, out var ulongValue))
                    Bail();
                *(ulong*)target = ulongValue;
                return ulongEnd;
            default:
                if (!FastNumber.ScanSignedInteger(_utf8, colon + 1, bound, entry.Kind is not FieldKind.Byte and not FieldKind.UShort and not FieldKind.UInt, MinOf(entry.Kind), MaxOf(entry.Kind), out var intEnd, out var intValue))
                    Bail();
                WriteSigned(target, entry.Kind, intValue);
                return intEnd;
        }
    }

    private static readonly long[] KindMin =
        [0, 0, sbyte.MinValue, short.MinValue, 0, int.MinValue, 0, long.MinValue, long.MinValue, 0, 0, 0];

    private static readonly long[] KindMax =
        [0, byte.MaxValue, sbyte.MaxValue, short.MaxValue, ushort.MaxValue, int.MaxValue, uint.MaxValue, long.MaxValue, long.MaxValue, 0, 0, 0];

    private static long MinOf(FieldKind kind) => KindMin[(int)kind];

    private static long MaxOf(FieldKind kind) => KindMax[(int)kind];

    private static void WriteSigned(byte* target, FieldKind kind, long value)
    {
        switch (kind)
        {
            case FieldKind.Byte:
                *target = (byte)value;
                break;
            case FieldKind.SByte:
                *(sbyte*)target = (sbyte)value;
                break;
            case FieldKind.Short:
                *(short*)target = (short)value;
                break;
            case FieldKind.UShort:
                *(ushort*)target = (ushort)value;
                break;
            case FieldKind.Int:
                *(int*)target = (int)value;
                break;
            case FieldKind.UInt:
                *(uint*)target = (uint)value;
                break;
            default:
                *(long*)target = value;
                break;
        }
    }

    private bool ScalarTailWhitespace(int from, int to)
    {
        for (var i = from; i < to; i++)
            if (!IsWhitespace(_utf8[i]))
                return false;
        return true;
    }

    private bool GrammarValid(int value, int end, out int tokenEnd)
    {
        tokenEnd = 0;
        var i = value;
        while (i < end && IsWhitespace(_utf8[i]))
            i++;
        if (i >= end)
            return false;
        var first = _utf8[i];
        if (first == (byte)'-')
            i++;
        var digits = 0;
        while (i < end && _utf8[i] >= (byte)'0' && _utf8[i] <= (byte)'9')
        {
            i++;
            digits++;
        }
        if (digits == 0)
            return false;
        if (digits > 1 && _utf8[i - digits] == (byte)'0')
            return false;
        if (i < end && _utf8[i] == (byte)'.')
        {
            i++;
            var frac = 0;
            while (i < end && _utf8[i] >= (byte)'0' && _utf8[i] <= (byte)'9')
            {
                i++;
                frac++;
            }
            if (frac == 0)
                return false;
        }
        if (i < end && (_utf8[i] == (byte)'e' || _utf8[i] == (byte)'E'))
        {
            i++;
            if (i < end && (_utf8[i] == (byte)'+' || _utf8[i] == (byte)'-'))
                i++;
            var exp = 0;
            while (i < end && _utf8[i] >= (byte)'0' && _utf8[i] <= (byte)'9')
            {
                i++;
                exp++;
            }
            if (exp == 0)
                return false;
        }
        tokenEnd = i;
        return ScalarTailWhitespace(i, end);
    }

    private static bool IsWhitespace(byte b) => b is 0x20 or 0x09 or 0x0A or 0x0D;

    private static bool IsStructuralByte(byte b) =>
        b is OpenBrace or CloseBrace or OpenBracket or CloseBracket or Colon or Comma or Quote;

    private static readonly ConcurrentDictionary<Type, object> OffsetReady = new();

    private static void PrepareFieldOffsets(FieldTable table, Type structType)
    {
        OffsetReady.GetOrAdd(structType, static (_, state) =>
        {
            foreach (var entry in state.Table.Entries)
                if (entry.ByteOffset < 0)
                    entry.ByteOffset = FieldOffsetOf(entry.Field, state.Type);
            return state;
        }, (Table: table, Type: structType));
    }

    private static int FieldOffsetOf(FieldInfo field, Type declaring)
    {
        var method = new DynamicMethod(
            "simd_fld_" + field.Name,
            typeof(IntPtr),
            [typeof(object)],
            declaring.Module,
            skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox, declaring);
        il.Emit(OpCodes.Ldflda, field);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox, declaring);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Ret);
        var offsetOf = (FieldOffsetDelegate)method.CreateDelegate(typeof(FieldOffsetDelegate));
        object probe = Activator.CreateInstance(declaring)!;
        return (int)offsetOf(probe);
    }

    private delegate IntPtr FieldOffsetDelegate(object box);
}

internal sealed class SimdBailException : Exception;
