using Tl;
using Unity.Entities;

public struct Attacker : IComponentData
{
    public Entity Target;
    public Entity Spark;
    public TlTimeline Hurt;
}

public struct Score : IComponentData
{
    public int Strikes;
    public int Sparks;
    public int Hurts;
    public int Ticks;
}

public struct Spark : IComponentData
{
    public float Life;
}
