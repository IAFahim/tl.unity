namespace Tl;

public enum KindEntries : byte { Spans, Pulses, Both }

public enum KindMode : byte { Combine, Each }

public enum ClipEvent : byte { Start, End, Marker }

[AttributeUsage(AttributeTargets.Struct)]
public sealed class TimelineKindAttribute : Attribute
{
    public KindEntries Accepts { get; set; }
    public KindMode Mode { get; set; }
    public int Order { get; set; }
    public string? Group { get; set; }
    public bool FoldsPulses { get; set; }
}

public interface IPulseFold<T> where T : unmanaged
{
    void Fold(ref T total, in T next);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class FeedsAttribute : Attribute
{
    public FeedsAttribute(Type kind) { Kind = kind; }
    public Type Kind { get; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PulseAttribute : Attribute
{
    public PulseAttribute(Type kind) { Kind = kind; }
    public Type Kind { get; }
    public ClipEvent On { get; set; }
}
