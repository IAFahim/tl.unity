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

public struct Strikes : IComponentData
{
    public int Count;
    public int Amount;
}

[TlKind]
public struct ArenaLunge
{
    public static void Combine(ref float total, in float next) => total += next;
    public static void Apply(in float total, ref Lunge lunge) => lunge.Value += total;
}

[TlKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
public struct ArenaStrike
{
    public static void Apply(in int amount, ref Strikes strikes)
    {
        if (amount <= 0)
            return;
        strikes.Count++;
        strikes.Amount += amount;
    }
}

public struct StrikeTrack : ITlTrack<StrikeClip>
{
    [Feeds(typeof(ArenaLunge))]
    public static float Evaluate(in TlFrame<StrikeClip> frame) => frame.Clip.Reach * math.sin(frame.Progress * math.PI);

    [Pulse(typeof(ArenaStrike), On = ClipEvent.Start)]
    public static int Strike(in TlFrame<StrikeClip> frame) => frame.Direction > 0 ? frame.Clip.Amount : 0;
}
