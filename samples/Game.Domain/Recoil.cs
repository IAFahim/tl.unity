using Tl;

namespace Rifle;

public struct RecoilTrack : IBlend<RecoilClip>
{
    public float Gain;
    public void Blend(in RecoilClip first, in RecoilClip second, float factor, out RecoilClip result)
        => result = new() { Impulse = first.Impulse + (second.Impulse - first.Impulse) * factor };
}

public struct RecoilClip { public float Impulse; }
public struct RecoilFx { public float Value; }
public struct RecoilStrength { public float Value; }
public struct RiflePosition { public float Value; }

public readonly struct Recoil : ITrack<RecoilTrack, RecoilClip>
{
    public static void Fold(in Frame<RecoilTrack, RecoilClip> frame, out float impulse)
        => impulse = frame.Direction * frame.Clip.Impulse * frame.Track.Gain;

    public static void ExecuteActive(in float impulse, ref RecoilFx effect, in RecoilStrength strength)
        => effect.Value += impulse * strength.Value;
}
