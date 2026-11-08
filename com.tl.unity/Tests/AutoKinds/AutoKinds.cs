using Tl;

namespace AutoKinds
{
    [TlState]
    public partial struct Glow
    {
        public float Value;
    }

    [TlState]
    public partial struct Chime
    {
        public int Count;
    }

    [TlClip("AutoKinds/Glow")]
    public struct GlowClip
    {
        public float Amount;
    }

    [TlClip("AutoKinds/Chime")]
    public struct ChimeClip
    {
        public int Rings;
    }

    [TlKind]
    public struct GlowKind
    {
        public static void Combine(ref float total, in float next) => total += next;
        public static void Apply(in float total, ref Glow glow) => glow.Value += total;
    }

    [TlKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
    public struct ChimeKind
    {
        public static void Apply(in int rings, ref Chime chime) => chime.Count += rings;
    }

    public struct GlowTrack : ITlTrack<GlowClip>
    {
        [Feeds(typeof(GlowKind))]
        public static float Evaluate(in TlFrame<GlowClip> frame) => frame.Clip.Amount;
    }

    public struct ChimeTrack : ITlTrack<ChimeClip>
    {
        [Pulse(typeof(ChimeKind), On = ClipEvent.Start)]
        public static int Ring(in TlFrame<ChimeClip> frame) => frame.Direction * frame.Clip.Rings;
    }
}
