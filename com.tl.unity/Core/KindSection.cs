using System.Runtime.InteropServices;
using System.Text;

namespace Tl;

public enum KindEventOp : byte { Remove, Insert, PulseStart, PulseEnd, PulseMarker }

[StructLayout(LayoutKind.Sequential)]
public struct LibraryKind
{
    public uint NameOffset;
    public uint NameLength;
    public uint ValueBytes;
    public uint EntryBytes;
    public uint Capacity;
    public byte Accepts;
    public byte Mode;
    public ushort Flags;
    public int Order;
    public uint GroupNameOffset;
    public uint GroupNameLength;
    public uint GroupCapacity;
}

[StructLayout(LayoutKind.Sequential)]
public struct LibraryFeeder
{
    public ushort Kind;
    public byte Trigger;
    public byte Ordinal;
    public uint _pad;
    public ulong SourceKey;
    public uint ConsumerOffset;
    public uint ConsumerLength;
    public uint MethodOffset;
    public uint MethodLength;
}

[StructLayout(LayoutKind.Sequential)]
public struct LibraryKindTimeline
{
    public ulong KindMask;
    public uint Windows;
    public uint WindowCount;
    public uint EventBits;
    public uint EventIndex;
    public uint Events;
    public uint EventCount;
    public uint WrapForward;
    public uint WrapForwardCount;
    public uint WrapBackward;
    public uint WrapBackwardCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct LibraryWindow
{
    public ushort Track;
    public ushort Pair;
    public ushort Start;
    public ushort End;
    public uint Slot;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct LibraryKindEvent
{
    public ushort Window;
    public ushort Feeder;
    public byte Op;
    public byte _p0;
    public ushort _p1;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct KindSectionView
{
    public byte* Base;
    public uint Bytes;
    public int KindCount;
    public int FeederCount;
    public int TimelineCount;
    public LibraryKind* Kinds;
    public LibraryFeeder* Feeders;
    public LibraryKindTimeline* Timelines;
    public byte* Strings;
    public uint StringsLength;

    public bool IsEmpty => Base == null;

    public string NameOf(int kind) => Text(Kinds[kind].NameOffset, Kinds[kind].NameLength);

    public string GroupOf(int kind) => Text(Kinds[kind].GroupNameOffset, Kinds[kind].GroupNameLength);

    public string Text(uint offset, uint length) => length == 0 ? string.Empty : Encoding.UTF8.GetString(Strings + offset, (int)length);

    public int IndexOf(string name)
    {
        for (var k = 0; k < KindCount; k++) if (NameOf(k) == name) return k;
        return -1;
    }

    public LibraryKindTimeline* TimelineOf(ushort timeline) => timeline == 0 || timeline > TimelineCount ? null : Timelines + (timeline - 1);

    public LibraryWindow* Windows(LibraryKindTimeline* t) => (LibraryWindow*)(Base + t->Windows);

    public LibraryKindEvent* Events(LibraryKindTimeline* t) => (LibraryKindEvent*)(Base + t->Events);

    public byte EventBitsAt(LibraryKindTimeline* t, int tick) => (Base + t->EventBits)[tick];

    public uint EventStart(LibraryKindTimeline* t, int tick, int direction) => ((uint*)(Base + t->EventIndex))[tick * 2 + direction];

    public uint EventEnd(LibraryKindTimeline* t, int tick, int direction) => ((uint*)(Base + t->EventIndex))[tick * 2 + direction + 1];
}

internal static unsafe class KindSectionReader
{
    internal const uint Magic = LibraryLayout.SectionMagic;

    internal static KindSectionView Create(byte* section, uint length, int timelineCount, LibraryTimeline* timelines)
    {
        if (length < 64) throw Fail("truncated");
        var u = (uint*)section;
        if (u[0] != Magic) throw Fail("magic invalid");
        if (u[1] != 1u) throw Fail("version unknown");
        var kinds = u[2];
        var feeders = u[3];
        if (kinds > 64) throw Fail("more than 64 kinds");
        if (u[4] != (uint)timelineCount) throw Fail("timeline count differs from the manifest");
        var feederAt = 64u + 40u * kinds;
        var timelineAt = feederAt + 32u * feeders;
        var dataAt = timelineAt + 48u * (uint)timelineCount;
        if (u[5] != 64u || u[6] != feederAt || u[7] != timelineAt) throw Fail("table offsets invalid");
        if (feederAt < 64u || dataAt < timelineAt || dataAt > length || u[8] < dataAt || (ulong)u[8] + u[9] > length) throw Fail("tables out of bounds");
        var view = new KindSectionView
        {
            Base = section,
            Bytes = length,
            KindCount = (int)kinds,
            FeederCount = (int)feeders,
            TimelineCount = timelineCount,
            Kinds = (LibraryKind*)(section + 64),
            Feeders = (LibraryFeeder*)(section + feederAt),
            Timelines = (LibraryKindTimeline*)(section + timelineAt),
            Strings = section + u[8],
            StringsLength = u[9],
        };
        for (var k = 0; k < view.KindCount; k++)
        {
            var kind = view.Kinds[k];
            if ((ulong)kind.NameOffset + kind.NameLength > view.StringsLength || (ulong)kind.GroupNameOffset + kind.GroupNameLength > view.StringsLength) throw Fail($"kind {k} name out of bounds");
            if (kind.Accepts > 2 || kind.Mode > 1 || kind.Capacity > ushort.MaxValue || kind.Flags > 1 || (kind.Flags != 0 && (kind.Mode != 0 || kind.Accepts == 0))) throw Fail($"kind {k} fields out of range");
            if (kind.EntryBytes != (kind.Accepts == 0 ? 8u : 8u + ((kind.ValueBytes + 3u) & ~3u))) throw Fail($"kind {k} entry size does not follow its value size");
        }
        for (var f = 0; f < view.FeederCount; f++)
        {
            var feeder = view.Feeders[f];
            if (feeder.Kind >= view.KindCount || feeder.Trigger > 3) throw Fail($"feeder {f} fields out of range");
            if ((ulong)feeder.ConsumerOffset + feeder.ConsumerLength > view.StringsLength || (ulong)feeder.MethodOffset + feeder.MethodLength > view.StringsLength) throw Fail($"feeder {f} name out of bounds");
        }
        for (var i = 0; i < timelineCount; i++) CheckTimeline(view, view.Timelines + i, timelines[i].Duration, i);
        return view;
    }

    static void CheckTimeline(in KindSectionView view, LibraryKindTimeline* t, uint duration, int index)
    {
        if (t->KindMask == 0) return;
        var where = $"timeline {index + 1}";
        if (view.KindCount < 64 && (t->KindMask >> view.KindCount) != 0) throw Fail($"{where} names a kind past the table");
        var total = (ulong)t->EventCount + t->WrapForwardCount + t->WrapBackwardCount;
        if (t->Windows % 8 != 0 || t->EventBits % 8 != 0 || t->EventIndex % 8 != 0 || t->Events % 8 != 0) throw Fail($"{where} offsets must be 8-aligned");
        if ((ulong)t->Windows + 16ul * t->WindowCount > view.Bytes
            || (ulong)t->EventBits + duration > view.Bytes
            || (ulong)t->EventIndex + 4ul * (2ul * duration + 1) > view.Bytes
            || (ulong)t->Events + 8ul * total > view.Bytes) throw Fail($"{where} data out of bounds");
        if (t->WrapForward != t->EventCount || t->WrapBackward != t->EventCount + t->WrapForwardCount) throw Fail($"{where} wrap lists must follow the tick events");
        var starts = (uint*)(view.Base + t->EventIndex);
        var last = 0u;
        for (var i = 0; i <= 2 * (int)duration; i++)
        {
            if (starts[i] < last) throw Fail($"{where} event index not ascending");
            last = starts[i];
        }
        if (starts[0] != 0 || last != t->EventCount) throw Fail($"{where} event index does not cover the events");
        var events = (LibraryKindEvent*)(view.Base + t->Events);
        for (ulong e = 0; e < total; e++)
        {
            var ev = events[e];
            if (ev.Window >= t->WindowCount || ev.Feeder >= view.FeederCount || ev.Op > (byte)KindEventOp.PulseMarker) throw Fail($"{where} event {e} fields out of range");
            if ((t->KindMask & (1ul << view.Feeders[ev.Feeder].Kind)) == 0) throw Fail($"{where} event {e} feeds a kind outside the kind mask");
        }
    }

    static ArgumentException Fail(string reason) => new($"game.tlib kind section {reason}; rebake with current tlb.");
}
