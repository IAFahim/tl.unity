namespace Tl;

public unsafe struct KindCalm
{
    public byte* Forward;
    public byte* Backward;
    public int* Start;

    public static int Length(in KindTables tables)
    {
        var total = 0L;
        for (var t = 0; t < tables.TimelineCount; t++)
            if (HasEvents(in tables, t)) total += tables.Timelines[t].Duration;
        return checked((int)total);
    }

    public static KindCalm Build(in KindTables tables, byte* forward, byte* backward, int* start)
    {
        var at = 0;
        for (var t = 0; t < tables.TimelineCount; t++)
        {
            start[t] = -1;
            if (!HasEvents(in tables, t)) continue;
            start[t] = at;
            var events = tables.Sec.Timelines + t;
            var duration = (int)tables.Timelines[t].Duration;
            var ahead = forward + at;
            var behind = backward + at;
            for (var tick = duration - 1; tick >= 0; tick--)
                ahead[tick] = tick + 1 < duration && (tables.Sec.EventBitsAt(events, tick + 1) & 1) == 0 ? Next(ahead[tick + 1]) : (byte)0;
            for (var tick = 0; tick < duration; tick++)
                behind[tick] = tick >= 1 && (tables.Sec.EventBitsAt(events, tick - 1) & 2) == 0 ? Next(behind[tick - 1]) : (byte)0;
            at += duration;
        }
        return new KindCalm { Forward = forward, Backward = backward, Start = start };
    }

    static bool HasEvents(in KindTables tables, int timeline) => tables.KindCount != 0 && tables.Sec.Timelines[timeline].KindMask != 0;

    static byte Next(byte steps) => steps < Horizon.Max ? (byte)(steps + 1) : (byte)Horizon.Max;
}
