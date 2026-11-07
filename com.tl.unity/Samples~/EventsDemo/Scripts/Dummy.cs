using Tl;
using Unity.Entities;

public struct DummyHealth : IComponentData
{
    public int Max;
}

public struct Wounds : IComponentData
{
    public int Taken;
}

[TlKind(Accepts = KindEntries.Pulses, GamePulses = 8)]
public struct ArenaDamage
{
    public static void Combine(ref int total, in int next) => total += next;
    public static void Apply(in int total, ref Wounds wounds) => wounds.Taken += total;
}

[TlClip("Arena/Squash")]
public struct SquashClip
{
    public float Amount;
    public TlCurve Shape;
}

public struct Squash : IComponentData
{
    public float Value;
}

[TlKind]
public struct ArenaSquash
{
    public static void Combine(ref float total, in float next) => total += next;
    public static void Apply(in float total, ref Squash squash) => squash.Value += total;
}

public struct SquashTrack : ITlTrack<SquashClip>
{
    [Feeds(typeof(ArenaSquash))]
    public static float Evaluate(in TlFrame<SquashClip> frame) => frame.Clip.Amount * frame.Clip.Shape.Evaluate(frame.Progress);
}
