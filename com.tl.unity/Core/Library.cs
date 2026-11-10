using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Tl;

public enum LibraryError : byte
{
    None = 0,
    Truncated = 1,
    BadMagic = 2,
    BadVersion = 3,
    LayoutMismatch = 4,
    HashMismatch = 5,
    OffsetOutOfRange = 6,
    CountMismatch = 7,
    ValueOutOfRange = 8,
    BadRow = 9,
}

public enum LaneClass : byte
{
    Constant = 0,
    Dense = 1,
}

public enum LaneDirection : byte
{
    Forward = 0,
    Backward = 1,
}

[StructLayout(LayoutKind.Sequential)]
public struct Tlib3Timeline
{
    public uint Duration;
    public uint StageIndex;
    public uint StageCount;
    public uint StepIndex;
    public uint StepCount;
    public byte Loop;
    public byte Flags;
    public ushort NameIndex;
}

[StructLayout(LayoutKind.Sequential)]
public struct Tlib3Stage
{
    public uint Start;
    public uint End;
    public uint StepIndex;
    public uint StepCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct Tlib3Step
{
    public uint WindowStart;
    public uint WindowEnd;
    public uint KindId;
    public ulong PairKey;
    public uint ForwardOffset;
    public uint ForwardCount;
    public uint BackwardOffset;
    public uint BackwardCount;
    public byte ForwardClass;
    public byte BackwardClass;
    public ushort Reserved;
    public uint Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
public struct Tlib3StagePlan
{
    public uint StageIndex;
    public uint StepIndex;
    public uint StepCount;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct Tlib3Edge
{
    public uint From;
    public uint To;
    public uint Kind;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct Tlib3Marker
{
    public uint Tick;
    public uint StepIndex;
    public uint TimelineIndex;
    public byte Kind;
    public byte Flags;
    public ushort Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct Tlib3Kind
{
    public uint Id;
    public uint Order;
    public uint Flags;
    public uint NameIndex;
}

[StructLayout(LayoutKind.Sequential)]
public struct Tlib3Feeder
{
    public uint KindId;
    public uint TimelineIndex;
    public uint StepIndex;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct Tlib3Name
{
    public uint Offset;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Tlib3Header
{
    public uint Magic;
    public uint Version;
    public uint TotalBytes;
    public uint Flags;
    public uint TimelineOffset;
    public uint TimelineCount;
    public uint StageOffset;
    public uint StageCount;
    public uint StepOffset;
    public uint StepCount;
    public uint StagePlanOffset;
    public uint StagePlanCount;
    public uint EdgeOffset;
    public uint EdgeCount;
    public uint ValueOffset;
    public uint ValueCount;
    public uint MarkerOffset;
    public uint MarkerCount;
    public uint KindOffset;
    public uint KindCount;
    public uint FeederOffset;
    public uint FeederCount;
    public uint NameOffset;
    public uint NameCount;
    public uint NameBytes;
    public uint ShaOffset;
    public uint Reserved;
    public uint Reserved2;
    public uint Reserved3;
    public uint Reserved4;
    public uint Reserved5;
    public uint Reserved6;
}

internal static class Tlib3Layout
{
    internal const uint Magic = 0x42494C54u;
    internal const uint Version = 3u;
    internal const uint HeaderBytes = 128u;
    internal const uint Alignment = 64u;
    internal const uint TrailerBytes = 32u;

    internal static unsafe uint Size<T>() where T : unmanaged => (uint)sizeof(T);

    internal static ulong Align(ulong value) => (value + Alignment - 1ul) & ~(Alignment - 1ul);
}

public readonly struct LibraryResult
{
    public bool Ok { get; }
    public Library? Library { get; }
    public LibraryError Error { get; }
    public int ErrorOffset { get; }

    internal LibraryResult(Library? library, LibraryError error, int errorOffset)
    {
        Library = library;
        Ok = library != null;
        Error = library != null ? LibraryError.None : error;
        ErrorOffset = library != null ? -1 : errorOffset;
    }
}

public sealed unsafe class Library : IDisposable
{
    static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    readonly byte[] _bytes;
    readonly GCHandle _pin;
    readonly byte* _base;
    readonly Tlib3Header* _header;
    bool _disposed;

    internal Library(byte[] bytes, Tlib3Header header)
    {
        _bytes = bytes;
        _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        _base = (byte*)_pin.AddrOfPinnedObject();
        _header = (Tlib3Header*)_base;
    }

    ~Library()
    {
        Dispose();
    }

    public static LibraryResult Verify(ReadOnlySpan<byte> blob)
    {
        try
        {
            return VerifyCore(blob);
        }
        catch (Exception)
        {
            return new LibraryResult(null, LibraryError.LayoutMismatch, 0);
        }
    }

    public void Dispose()
    {
        if (_pin.IsAllocated) _pin.Free();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public ReadOnlySpan<byte> Blob => _bytes.AsSpan();

    public uint Version => _header->Version;

    public int TimelineCount => (int)_header->TimelineCount;

    public int StageCount => (int)_header->StageCount;

    public int StepCount => (int)_header->StepCount;

    public int StagePlanCount => (int)_header->StagePlanCount;

    public int EdgeCount => (int)_header->EdgeCount;

    public int ValueCount => (int)_header->ValueCount;

    public int MarkerCount => (int)_header->MarkerCount;

    public int KindCount => (int)_header->KindCount;

    public int FeederCount => (int)_header->FeederCount;

    public int NameCount => (int)_header->NameCount;

    public uint Duration(int timeline)
    {
        if (_disposed || (uint)timeline >= _header->TimelineCount) return 0;
        return TimelinePtr(timeline)->Duration;
    }

    public bool Loops(int timeline)
    {
        if (_disposed || (uint)timeline >= _header->TimelineCount) return false;
        return TimelinePtr(timeline)->Loop != 0;
    }

    public string TimelineName(int timeline)
    {
        if (_disposed || (uint)timeline >= _header->TimelineCount) return string.Empty;
        return NameText(TimelinePtr(timeline)->NameIndex);
    }

    public Tlib3Timeline Timeline(int timeline)
    {
        if (_disposed || (uint)timeline >= _header->TimelineCount) return default;
        return *TimelinePtr(timeline);
    }

    public Tlib3Stage Stage(int stage)
    {
        if (_disposed || (uint)stage >= _header->StageCount) return default;
        return *(StagePtr(stage));
    }

    public Tlib3Step Step(int step)
    {
        if (_disposed || (uint)step >= _header->StepCount) return default;
        return *StepPtr(step);
    }

    public Tlib3StagePlan StagePlan(int plan)
    {
        if (_disposed || (uint)plan >= _header->StagePlanCount) return default;
        return *(StagePlanPtr(plan));
    }

    public Tlib3Edge Edge(int edge)
    {
        if (_disposed || (uint)edge >= _header->EdgeCount) return default;
        return *EdgePtr(edge);
    }

    public Tlib3Marker Marker(int marker)
    {
        if (_disposed || (uint)marker >= _header->MarkerCount) return default;
        return *MarkerPtr(marker);
    }

    public Tlib3Kind Kind(int kind)
    {
        if (_disposed || (uint)kind >= _header->KindCount) return default;
        return *KindPtr(kind);
    }

    public Tlib3Feeder Feeder(int feeder)
    {
        if (_disposed || (uint)feeder >= _header->FeederCount) return default;
        return *FeederPtr(feeder);
    }

    public Tlib3Name Name(int name)
    {
        if (_disposed || (uint)name >= _header->NameCount) return default;
        return *NamePtr(name);
    }

    public string NameText(int name)
    {
        if (_disposed || (uint)name >= _header->NameCount) return string.Empty;
        var row = NamePtr(name);
        if (row->Length == 0) return string.Empty;
        return Encoding.UTF8.GetString(_base + _header->NameOffset + _header->NameCount * 8u + row->Offset, (int)row->Length);
    }

    public ReadOnlySpan<float> ForwardPlane(int step) => Plane(step, LaneDirection.Forward);

    public ReadOnlySpan<float> BackwardPlane(int step) => Plane(step, LaneDirection.Backward);

    public LaneClass LaneClassOf(int step, LaneDirection direction)
    {
        if (_disposed || (uint)step >= _header->StepCount) return LaneClass.Constant;
        var row = StepPtr(step);
        return direction == LaneDirection.Forward ? (LaneClass)row->ForwardClass : (LaneClass)row->BackwardClass;
    }

    public float LaneValue(int step, LaneDirection direction, int tick)
    {
        if (_disposed || (uint)step >= _header->StepCount) return 0f;
        var row = StepPtr(step);
        if ((uint)tick < row->WindowStart || (uint)tick >= row->WindowEnd) return 0f;
        var plane = Plane(step, direction);
        if (plane.Length == 1) return plane[0];
        return plane[(int)((uint)tick - row->WindowStart)];
    }

    ReadOnlySpan<float> Plane(int step, LaneDirection direction)
    {
        if (_disposed || (uint)step >= _header->StepCount) return default;
        var row = StepPtr(step);
        uint offset, count;
        if (direction == LaneDirection.Forward)
        {
            offset = row->ForwardOffset;
            count = row->ForwardCount;
        }
        else
        {
            offset = row->BackwardOffset;
            count = row->BackwardCount;
        }
        return new ReadOnlySpan<float>((float*)(_base + _header->ValueOffset + offset * 4u), (int)count);
    }

    Tlib3Timeline* TimelinePtr(int index) => (Tlib3Timeline*)(_base + _header->TimelineOffset) + index;

    Tlib3Stage* StagePtr(int index) => (Tlib3Stage*)(_base + _header->StageOffset) + index;

    Tlib3Step* StepPtr(int index) => (Tlib3Step*)(_base + _header->StepOffset) + index;

    Tlib3StagePlan* StagePlanPtr(int index) => (Tlib3StagePlan*)(_base + _header->StagePlanOffset) + index;

    Tlib3Edge* EdgePtr(int index) => (Tlib3Edge*)(_base + _header->EdgeOffset) + index;

    Tlib3Marker* MarkerPtr(int index) => (Tlib3Marker*)(_base + _header->MarkerOffset) + index;

    Tlib3Kind* KindPtr(int index) => (Tlib3Kind*)(_base + _header->KindOffset) + index;

    Tlib3Feeder* FeederPtr(int index) => (Tlib3Feeder*)(_base + _header->FeederOffset) + index;

    Tlib3Name* NamePtr(int index) => (Tlib3Name*)(_base + _header->NameOffset) + index;

    static LibraryResult Fail(LibraryError error, int offset) => new LibraryResult(null, error, offset);

    static LibraryResult VerifyCore(ReadOnlySpan<byte> blob)
    {
        var length = blob.Length;
        if (length < (int)(Tlib3Layout.HeaderBytes + Tlib3Layout.TrailerBytes))
            return Fail(LibraryError.Truncated, length);
        var header = MemoryMarshal.Read<Tlib3Header>(blob);
        if (header.Magic != Tlib3Layout.Magic)
            return Fail(LibraryError.BadMagic, 0);
        if (header.Version != Tlib3Layout.Version)
            return Fail(LibraryError.BadVersion, 4);
        if (header.TotalBytes != (uint)length)
            return Fail(LibraryError.Truncated, length);
        if (header.Flags != 1u || header.Reserved != 0 || header.Reserved2 != 0 || header.Reserved3 != 0 || header.Reserved4 != 0 || header.Reserved5 != 0 || header.Reserved6 != 0)
            return Fail(LibraryError.LayoutMismatch, 12);
        if (header.ShaOffset != (uint)(length - 32))
            return Fail(LibraryError.LayoutMismatch, 100);
        Span<uint> actualOffsets = stackalloc uint[10];
        Span<uint> counts = stackalloc uint[10];
        Span<ulong> sizes = stackalloc ulong[10];
        actualOffsets[0] = header.TimelineOffset; counts[0] = header.TimelineCount; sizes[0] = Tlib3Layout.Size<Tlib3Timeline>();
        actualOffsets[1] = header.StageOffset; counts[1] = header.StageCount; sizes[1] = Tlib3Layout.Size<Tlib3Stage>();
        actualOffsets[2] = header.StepOffset; counts[2] = header.StepCount; sizes[2] = Tlib3Layout.Size<Tlib3Step>();
        actualOffsets[3] = header.StagePlanOffset; counts[3] = header.StagePlanCount; sizes[3] = Tlib3Layout.Size<Tlib3StagePlan>();
        actualOffsets[4] = header.EdgeOffset; counts[4] = header.EdgeCount; sizes[4] = Tlib3Layout.Size<Tlib3Edge>();
        actualOffsets[5] = header.ValueOffset; counts[5] = header.ValueCount; sizes[5] = Tlib3Layout.Size<float>();
        actualOffsets[6] = header.MarkerOffset; counts[6] = header.MarkerCount; sizes[6] = Tlib3Layout.Size<Tlib3Marker>();
        actualOffsets[7] = header.KindOffset; counts[7] = header.KindCount; sizes[7] = Tlib3Layout.Size<Tlib3Kind>();
        actualOffsets[8] = header.FeederOffset; counts[8] = header.FeederCount; sizes[8] = Tlib3Layout.Size<Tlib3Feeder>();
        actualOffsets[9] = header.NameOffset; counts[9] = header.NameCount; sizes[9] = Tlib3Layout.Size<Tlib3Name>();
        var pos = (ulong)Tlib3Layout.HeaderBytes;
        for (var i = 0; i < 10; i++)
        {
            pos = Tlib3Layout.Align(pos);
            if ((ulong)actualOffsets[i] != pos)
                return Fail(LibraryError.LayoutMismatch, 16 + i * 8);
            pos += (ulong)counts[i] * sizes[i];
        }
        pos += header.NameBytes;
        pos = Tlib3Layout.Align(pos);
        if ((ulong)header.ShaOffset != pos)
            return Fail(LibraryError.LayoutMismatch, 100);
        var bytes = blob.ToArray();
        using (var sha = SHA256.Create())
        {
            var digest = sha.ComputeHash(bytes, 0, bytes.Length - 32);
            if (!digest.AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 32)))
                return Fail(LibraryError.HashMismatch, bytes.Length - 32);
        }
        var failure = VerifyRows(bytes, header);
        if (failure is (var error, var offset))
            return Fail(error, offset);
        return new LibraryResult(new Library(bytes, header), LibraryError.None, -1);
    }

    static (LibraryError Error, int Offset)? VerifyRows(byte[] bytes, Tlib3Header header)
    {
        var span = bytes.AsSpan();
        var kindOffset = (int)header.KindOffset;
        var previousId = 0u;
        var kindRow = kindOffset;
        for (uint i = 0; i < header.KindCount; i++, kindRow += 16)
        {
            var kind = MemoryMarshal.Read<Tlib3Kind>(span.Slice(kindRow));
            if (i > 0 && kind.Id <= previousId)
                return (LibraryError.BadRow, kindRow);
            if (kind.Order != i)
                return (LibraryError.BadRow, kindRow);
            if (kind.NameIndex >= header.NameCount)
                return (LibraryError.ValueOutOfRange, kindRow);
            previousId = kind.Id;
        }
        var nameRow = (int)header.NameOffset;
        var namesData = (int)(header.NameOffset + header.NameCount * 8u);
        for (uint i = 0; i < header.NameCount; i++, nameRow += 8)
        {
            var name = MemoryMarshal.Read<Tlib3Name>(span.Slice(nameRow));
            if ((ulong)name.Offset + name.Length > header.NameBytes)
                return (LibraryError.ValueOutOfRange, nameRow);
            try
            {
                StrictUtf8.GetString(span.Slice(namesData + (int)name.Offset, (int)name.Length));
            }
            catch (DecoderFallbackException)
            {
                return (LibraryError.BadRow, nameRow);
            }
        }
        uint stageCursor = 0, stepCursor = 0;
        var timelineRow = (int)header.TimelineOffset;
        for (uint t = 0; t < header.TimelineCount; t++, timelineRow += 24)
        {
            var timeline = MemoryMarshal.Read<Tlib3Timeline>(span.Slice(timelineRow));
            if (timeline.Duration == 0)
                return (LibraryError.ValueOutOfRange, timelineRow);
            if (timeline.Loop > 1)
                return (LibraryError.BadRow, timelineRow);
            if (timeline.StageIndex != stageCursor)
                return (LibraryError.CountMismatch, timelineRow);
            stageCursor += timeline.StageCount;
            if (timeline.StepIndex != stepCursor)
                return (LibraryError.CountMismatch, timelineRow);
            stepCursor += timeline.StepCount;
            if (timeline.NameIndex >= header.NameCount)
                return (LibraryError.ValueOutOfRange, timelineRow);
            var stageEnd = 0u;
            var stageRow = (int)header.StageOffset + (int)(timeline.StageIndex * 16u);
            for (uint s = 0; s < timeline.StageCount; s++, stageRow += 16)
            {
                var stage = MemoryMarshal.Read<Tlib3Stage>(span.Slice(stageRow));
                if (stage.Start >= stage.End || stage.End > timeline.Duration)
                    return (LibraryError.ValueOutOfRange, stageRow);
                if (stage.Start != stageEnd)
                    return (LibraryError.CountMismatch, stageRow);
                stageEnd = stage.End;
            }
            if (stageEnd != timeline.Duration)
                return (LibraryError.CountMismatch, timelineRow);
            var stepRow = (int)header.StepOffset + (int)(timeline.StepIndex * 48u);
            for (uint s = 0; s < timeline.StepCount; s++, stepRow += 48)
            {
                var step = MemoryMarshal.Read<Tlib3Step>(span.Slice(stepRow));
                if (step.WindowStart >= step.WindowEnd || step.WindowEnd > timeline.Duration)
                    return (LibraryError.ValueOutOfRange, stepRow);
                if (step.ForwardClass > 1 || step.BackwardClass > 1)
                    return (LibraryError.BadRow, stepRow);
                if (!KindExists(span, kindOffset, header.KindCount, step.KindId))
                    return (LibraryError.ValueOutOfRange, stepRow);
                if (step.ForwardClass == (byte)LaneClass.Constant && step.ForwardCount != 1)
                    return (LibraryError.CountMismatch, stepRow);
                if (step.ForwardClass == (byte)LaneClass.Dense && (ulong)step.ForwardCount != (ulong)(step.WindowEnd - step.WindowStart))
                    return (LibraryError.CountMismatch, stepRow);
                if (step.BackwardClass == (byte)LaneClass.Constant && step.BackwardCount != 1)
                    return (LibraryError.CountMismatch, stepRow);
                if (step.BackwardClass == (byte)LaneClass.Dense && (ulong)step.BackwardCount != (ulong)(step.WindowEnd - step.WindowStart))
                    return (LibraryError.CountMismatch, stepRow);
                if ((ulong)step.ForwardOffset + step.ForwardCount > header.ValueCount)
                    return (LibraryError.ValueOutOfRange, stepRow);
                if ((ulong)step.BackwardOffset + step.BackwardCount > header.ValueCount)
                    return (LibraryError.ValueOutOfRange, stepRow);
            }
        }
        if (stageCursor != header.StageCount)
            return (LibraryError.CountMismatch, 28);
        if (stepCursor != header.StepCount)
            return (LibraryError.CountMismatch, 36);
        if (header.StagePlanCount != header.StageCount)
            return (LibraryError.CountMismatch, 44);
        var planRow = (int)header.StagePlanOffset;
        for (uint i = 0; i < header.StagePlanCount; i++, planRow += 16)
        {
            var plan = MemoryMarshal.Read<Tlib3StagePlan>(span.Slice(planRow));
            if (plan.StageIndex != i)
                return (LibraryError.BadRow, planRow);
            var stage = MemoryMarshal.Read<Tlib3Stage>(span.Slice((int)header.StageOffset + (int)(i * 16u)));
            if (plan.StepIndex != stage.StepIndex || plan.StepCount != stage.StepCount)
                return (LibraryError.BadRow, planRow);
        }
        var edgeRow = (int)header.EdgeOffset;
        for (uint i = 0; i < header.EdgeCount; i++, edgeRow += 16)
        {
            var edge = MemoryMarshal.Read<Tlib3Edge>(span.Slice(edgeRow));
            if (edge.From >= header.StepCount || edge.To >= header.StepCount || edge.From >= edge.To)
                return (LibraryError.BadRow, edgeRow);
            if (edge.Kind > 1)
                return (LibraryError.BadRow, edgeRow);
        }
        var markerRow = (int)header.MarkerOffset;
        for (uint i = 0; i < header.MarkerCount; i++, markerRow += 16)
        {
            var marker = MemoryMarshal.Read<Tlib3Marker>(span.Slice(markerRow));
            if (marker.TimelineIndex >= header.TimelineCount)
                return (LibraryError.BadRow, markerRow);
            var timeline = MemoryMarshal.Read<Tlib3Timeline>(span.Slice((int)header.TimelineOffset + (int)(marker.TimelineIndex * 24u)));
            if (marker.StepIndex < timeline.StepIndex || marker.StepIndex >= timeline.StepIndex + timeline.StepCount)
                return (LibraryError.BadRow, markerRow);
            if (marker.Tick >= timeline.Duration)
                return (LibraryError.ValueOutOfRange, markerRow);
        }
        var feederRow = (int)header.FeederOffset;
        for (uint i = 0; i < header.FeederCount; i++, feederRow += 16)
        {
            var feeder = MemoryMarshal.Read<Tlib3Feeder>(span.Slice(feederRow));
            if (!KindExists(span, kindOffset, header.KindCount, feeder.KindId))
                return (LibraryError.ValueOutOfRange, feederRow);
            if (feeder.TimelineIndex >= header.TimelineCount)
                return (LibraryError.BadRow, feederRow);
            var timeline = MemoryMarshal.Read<Tlib3Timeline>(span.Slice((int)header.TimelineOffset + (int)(feeder.TimelineIndex * 24u)));
            if (feeder.StepIndex < timeline.StepIndex || feeder.StepIndex >= timeline.StepIndex + timeline.StepCount)
                return (LibraryError.BadRow, feederRow);
        }
        return null;
    }

    static bool KindExists(ReadOnlySpan<byte> span, int kindOffset, uint kindCount, uint id)
    {
        var lo = 0u;
        var hi = kindCount;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var midId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(kindOffset + (int)(mid * 16u)));
            if (midId == id)
                return true;
            if (midId < id)
                lo = mid + 1;
            else
                hi = mid;
        }
        return false;
    }
}
