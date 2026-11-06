namespace Tl;

public static unsafe class Inspection
{
    public enum FoldState
    {
        Absent,
        Pending,
        Folded,
    }

    public sealed record InternSnapshot(
        int Capacity,
        int Live,
        int Distinct,
        long GraveyardBlocks,
        long GraveyardBytes,
        long AllocBytes,
        long PeakLiveBytes);

    public sealed record PairSnapshot(
        int Slots,
        int Pairs,
        int LoadFactorPermille,
        int Consumers,
        int ConsumerCapacity,
        int SlotRowBytes);

    public sealed record BakeSnapshot(int Slots, int Entries);

    public sealed record TablesSnapshot(InternSnapshot Intern, PairSnapshot Pair, BakeSnapshot Bake);

    public sealed record LaneShape(ulong Key, int Distinct, int LongestRun);

    public sealed record LaneWords(ulong Key, uint[] Forward, uint[] Backward);

    public sealed record LaneCopy(
        ushort Index, ushort Duration, bool Looping, int AbiVersion, long Generation,
        IReadOnlyList<LaneWords> Lanes);

    public sealed record ViewSnapshot(
        int Index,
        FoldState State,
        ushort? Duration,
        bool? Looping,
        int? Ticks,
        int? Lanes,
        int? AbiVersion,
        long? Generation,
        IReadOnlyList<LaneShape>? LaneShapes);

    public sealed record BankSnapshot(
        string Track,
        string Clip,
        ulong PairKey,
        int Count,
        int Holes,
        int Blocks,
        int DedupeHits,
        long Generation,
        long HeaderBytes,
        long TableBytes,
        long DirectoryBytes,
        long RetainedBytes,
        IReadOnlyList<ViewSnapshot> Views);

    public static TablesSnapshot Tables() => new(
        new InternSnapshot(
            TimelineTable.Capacity,
            TimelineTable.LiveEntries,
            TimelineTable.DistinctContents,
            TimelineTable.GraveyardBlocks,
            TimelineTable.GraveyardBytes,
            TimelineTable.AllocBytes,
            TimelineTable.PeakLiveBytes),
        Pair(),
        new BakeSnapshot(0, 0));



    static PairSnapshot Pair()
    {
        var slots = PairTable._slotsBase == 0 ? 0 : *(int*)PairTable._slotsBase;
        return new PairSnapshot(
            slots,
            PairTable._pairs,
            slots > 0 ? PairTable._pairs * 1000 / slots : 0,
            PairTable.ConsumerCount,
            PairTable._consumerCapacity,
            PairTable.SlotRow);
    }

    static List<LaneShape> Shapes(SlotView* slot)
    {
        var ticks = (int)slot->TableTicks;
        var lanes = (int)slot->ResultCount;
        var shapes = new List<LaneShape>(lanes);
        var keys = slot->LaneKeys;
        for (var lane = 0; lane < lanes; lane++)
        {
            var seen = new HashSet<uint>(ticks);
            var longest = 0;
            var run = 0;
            uint prior = 0;
            for (var tick = 0; tick < ticks; tick++)
            {
                var bits = LaneEncoding.Bits(LaneEncoding.Value(slot, (nuint)lane, true, tick));
                seen.Add(bits);
                run = tick > 0 && bits == prior ? run + 1 : 1;
                if (run > longest)
                    longest = run;
                prior = bits;
            }
            shapes.Add(new(keys[lane], seen.Count, longest));
        }
        return shapes;
    }
}
