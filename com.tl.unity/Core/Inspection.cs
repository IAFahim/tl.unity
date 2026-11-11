namespace Tl;

public static unsafe class Inspection
{
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

    public static TablesSnapshot Tables() => new(new InternSnapshot(0, 0, 0, 0, 0, 0, 0), new PairSnapshot(0, 0, 0, 0, 0, 0), new BakeSnapshot(0, 0));

}
