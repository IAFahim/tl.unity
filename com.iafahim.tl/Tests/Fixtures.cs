using Tl;

namespace Tl.Unity.Tests;

public struct AlphaClip
{
    public int Value;
}

public struct AlphaTrack : IBlend<AlphaClip>
{
    public int Code;

    public void Blend(in AlphaClip first, in AlphaClip second, float factor, out AlphaClip result)
        => result = factor < 0.5f ? first : second;
}

public struct BetaClip
{
    public int Value;
}

public struct BetaTrack : IBlend<BetaClip>
{
    public int Code;

    public void Blend(in BetaClip first, in BetaClip second, float factor, out BetaClip result)
        => result = first;
}

public struct BlendClip
{
    public float Amount;
}

public struct BlendTrack : IBlend<BlendClip>
{
    public float Scale;

    public void Blend(in BlendClip first, in BlendClip second, float factor, out BlendClip result)
        => result = new BlendClip { Amount = first.Amount + (second.Amount - first.Amount) * factor };
}

public struct PhiClip
{
    public int Value;
}

public struct PhiTrack : IBlend<PhiClip>
{
    public int Code;

    public void Blend(in PhiClip first, in PhiClip second, float factor, out PhiClip result)
        => result = first;
}
