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

    public struct Ding
    {
        public int Rings;
    }

    public struct Bell
    {
        public int Rings;
    }

    public struct BellMarker : IMarker<Bell>
    {
        [Pulse(typeof(ChimeKind), On = ClipEvent.Marker)]
        public static int Ring(in MarkerFrame<Bell> marker) => marker.Direction * marker.Marker.Rings;
    }

    public struct ChimeTrack : ITlTrack<ChimeClip>
    {
        [Pulse(typeof(ChimeKind), On = ClipEvent.Start)]
        public static int Ring(in TlFrame<ChimeClip> frame) => frame.Direction * frame.Clip.Rings;

        public static void OnClipStart(in TlFrame<ChimeClip> frame, ref Events<Ding> dings) => dings.Raise(new Ding { Rings = frame.Clip.Rings });
    }

    [TlState]
    public partial struct Abilities
    {
        public bool CanDoubleJumpWithIdle;
        public bool CanDoubleJumpWithoutIdle;
    }

    [TlClip("AutoKinds/DoubleJumpWindow")]
    public struct DoubleJumpClip
    {
        public bool Allowed;
    }

    [TlKind]
    public struct DoubleJumpWithIdleKind
    {
        public static void Combine(ref bool total, in bool next) => total |= next;
        public static void Apply(in bool allowed, ref Abilities player) => player.CanDoubleJumpWithIdle = allowed;
        public static void Idle(ref Abilities player) => player.CanDoubleJumpWithIdle = false;
    }

    [TlKind]
    public struct DoubleJumpWithoutIdleKind
    {
        public static void Combine(ref bool total, in bool next) => total |= next;
        public static void Apply(in bool allowed, ref Abilities player) => player.CanDoubleJumpWithoutIdle = allowed;
    }

    public struct DoubleJumpTrack : ITlTrack<DoubleJumpClip>
    {
        [Feeds(typeof(DoubleJumpWithIdleKind))]
        public static bool WhetherDoubleJumpIsAllowedWithIdle(in TlFrame<DoubleJumpClip> frame) => frame.Clip.Allowed;

        [Feeds(typeof(DoubleJumpWithoutIdleKind))]
        public static bool WhetherDoubleJumpIsAllowedWithoutIdle(in TlFrame<DoubleJumpClip> frame) => frame.Clip.Allowed;
    }
}
