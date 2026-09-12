using System.Runtime.InteropServices;
using Unity.Entities;

namespace Tl.Unity;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TimelineComponent : IComponentData
{
    public TimelineRef Reference;
    public uint Position;
    public long Cycle;
}

public struct TimelineClock : IComponentData
{
    public uint GameTick;
    public int Delta;
}
