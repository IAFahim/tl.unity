namespace Tl;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class TlClipAttribute : Attribute
{
    public TlClipAttribute(string menu)
    {
        Menu = menu;
    }

    public string Menu { get; }
}

[AttributeUsage(AttributeTargets.Struct)]
public sealed class TlKindAttribute : Attribute
{
    public KindEntries Accepts { get; set; }
    public KindMode Mode { get; set; }
    public int Order { get; set; }
    public string? Group { get; set; }
    public bool FoldsPulses { get; set; }
    public int Inline { get; set; }
    public int GamePulses { get; set; }
}

public interface ITlTrack<TClip> : ITrack<TlNoSettings<TClip>, TClip>
    where TClip : unmanaged
{
}

public struct TlNoSettings<TClip> : IBlend<TClip>
    where TClip : unmanaged
{
    public void Blend(in TClip first, in TClip second, float factor, out TClip result)
    {
        result = first;
    }
}

public readonly struct TlFrame<TClip>
    where TClip : unmanaged
{
    public TlFrame(in Frame<TlNoSettings<TClip>, TClip> frame)
    {
        Clip = frame.Clip;
        Tick = frame.TimelineTick;
        ClipLength = frame.ClipLength;
        WithinClip = frame.WithinClip;
        TrackIndex = frame.TrackIndex;
        Flags = frame.Flags;
    }

    public readonly TClip Clip;
    public readonly ushort Tick;
    public readonly ushort ClipLength;
    public readonly ushort WithinClip;
    public readonly ushort TrackIndex;
    public readonly FrameFlags Flags;
    public float Progress => ClipLength == 0 ? 0f : WithinClip / (float)ClipLength;
    public int Direction => Has(FrameFlags.Reverse) ? -1 : 1;
    public bool IsBackward => Has(FrameFlags.Reverse);
    public bool Has(FrameFlags flags) => (Flags & flags) == flags;
}

public unsafe struct TlCurve
{
    public const int Samples = 16;

    fixed float _values[Samples];

    public float this[int index]
    {
        get => _values[index];
        set => _values[index] = value;
    }

    public float Evaluate(float progress)
    {
        var x = (progress < 0f ? 0f : progress > 1f ? 1f : progress) * (Samples - 1);
        var i = (int)x;
        var j = i < Samples - 1 ? i + 1 : i;
        return _values[i] + (_values[j] - _values[i]) * (x - i);
    }

    public static TlCurve Constant(float value)
    {
        var curve = default(TlCurve);
        for (var i = 0; i < Samples; i++)
            curve._values[i] = value;
        return curve;
    }
}

[Serializable]
public struct TlAssetId : IEquatable<TlAssetId>
{
    public uint X;
    public uint Y;
    public uint Z;
    public uint W;

    public TlAssetId(uint x, uint y, uint z, uint w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public bool IsValid => (X | Y | Z | W) != 0;

    public bool Equals(TlAssetId other) => X == other.X && Y == other.Y && Z == other.Z && W == other.W;

    public override bool Equals(object? obj) => obj is TlAssetId other && Equals(other);

    public override int GetHashCode() => (int)(X ^ (Y * 16777619u) ^ (Z * 2166136261u) ^ (W * 486187739u));

    public static bool operator ==(TlAssetId a, TlAssetId b) => a.Equals(b);

    public static bool operator !=(TlAssetId a, TlAssetId b) => !a.Equals(b);

    public override string ToString() => X.ToString("x8") + Y.ToString("x8") + Z.ToString("x8") + W.ToString("x8");
}

[Serializable]
public struct TlTimeline : IEquatable<TlTimeline>
{
    public TlAssetId Asset;

    public bool IsValid => Asset.IsValid;

    public bool Equals(TlTimeline other) => Asset.Equals(other.Asset);

    public override bool Equals(object? obj) => obj is TlTimeline other && Equals(other);

    public override int GetHashCode() => Asset.GetHashCode();
}
