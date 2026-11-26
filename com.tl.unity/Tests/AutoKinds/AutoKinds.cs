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

    [TlClip("AutoKinds/Toll")]
    public struct TollClip
    {
        public int Rings;
    }

    public struct Dong
    {
        public int Rings;
    }

    public struct Hum
    {
        public int Rings;
    }

    public struct TollTrack : ITlTrack<TollClip>
    {
        [Pulse(typeof(ChimeKind), On = ClipEvent.Start)]
        public static int Ring(in TlFrame<TollClip> frame) => frame.Direction * frame.Clip.Rings;

        public static void OnClipStart(in TlFrame<TollClip> frame, ref Events<Ding> dings) => dings.Raise(new Ding { Rings = frame.Clip.Rings });

        public static void OnClipEnd(in TlFrame<TollClip> frame, ref Events<Dong> dongs) => dongs.Raise(new Dong { Rings = frame.Clip.Rings });

        public static void ExecuteActive(in TlFrame<TollClip> frame, ref Events<Hum> hums) => hums.Raise(new Hum { Rings = frame.Clip.Rings });
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

    [TlState]
    public partial struct Lamp
    {
        public float Brightness;
        public float StayingBrightness;
        public int Starts;
        public int Ends;
    }

    [TlClip("AutoKinds/Fade")]
    public struct FadeClip : IBlend<FadeClip>
    {
        public float Brightness;

        public void Blend(in FadeClip first, in FadeClip second, float factor, out FadeClip result)
            => result = new FadeClip { Brightness = first.Brightness + (second.Brightness - first.Brightness) * factor };
    }

    [TlClip("AutoKinds/Stay")]
    public struct StayClip
    {
        public float Brightness;
    }

    [TlKind]
    public struct FadeKind
    {
        public static void Combine(ref float total, in float next) => total += next;
        public static void Apply(in float total, ref Lamp lamp) => lamp.Brightness = total;
    }

    [TlKind]
    public struct StayKind
    {
        public static void Combine(ref float total, in float next) => total += next;
        public static void Apply(in float total, ref Lamp lamp) => lamp.StayingBrightness = total;
    }

    [TlKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
    public struct LampStartKind
    {
        public static void Apply(in int one, ref Lamp lamp) => lamp.Starts += one;
    }

    [TlKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
    public struct LampEndKind
    {
        public static void Apply(in int one, ref Lamp lamp) => lamp.Ends += one;
    }

    public struct FadeTrack : ITlTrack<FadeClip>
    {
        [Feeds(typeof(FadeKind))]
        public static float BrightnessThisTick(in TlFrame<FadeClip> frame) => frame.Clip.Brightness;

        [Pulse(typeof(LampStartKind), On = ClipEvent.Start)]
        public static int CountStart(in TlFrame<FadeClip> frame) => frame.Direction;

        [Pulse(typeof(LampEndKind), On = ClipEvent.End)]
        public static int CountEnd(in TlFrame<FadeClip> frame) => frame.Direction;
    }

    public struct StayTrack : ITlTrack<StayClip>
    {
        [Feeds(typeof(StayKind))]
        public static float BrightnessThisTick(in TlFrame<StayClip> frame) => frame.Clip.Brightness;
    }
}
