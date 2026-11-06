namespace Tl;

public sealed partial class TimelineWorld
{
    public static TimelineWorld Default { get; } = new TimelineWorld();

    public TimelineWorld()
    {
    }


    public TimelineRef Reference(ushort index) => TimelineTable.Reference(index);

    public bool IsLive(ushort index) => TimelineTable.IsLive(index);
}
