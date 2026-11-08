using Tl;
using Unity.Entities;
using Unity.Mathematics;

[TlClip("Arena/Strike")]
public struct StrikeClip
{
    public int Amount;
    public float Reach;
}

public struct Lunge : IComponentData
{
    public float Value;
}

public struct Strike
{
    public int Amount;
}

[TlKind]
public struct ArenaLunge
{
    public static void Combine(ref float total, in float next) => total += next;
    public static void Apply(in float total, ref Lunge lunge) => lunge.Value += total;
}

public struct StrikeTrack : ITlTrack<StrikeClip>
{
    [Feeds(typeof(ArenaLunge))]
    public static float Evaluate(in TlFrame<StrikeClip> frame) => frame.Clip.Reach * math.sin(frame.Progress * math.PI);

    public static void OnClipStart(in TlFrame<StrikeClip> frame, ref Events<Strike> strikes)
    {
        if (frame.Direction > 0)
            strikes.Raise(new Strike { Amount = frame.Clip.Amount });
    }
}
