using Tl;

namespace HelloTl
{
    [TlClip("Hello/Teleport")]
    public struct TeleportClip
    {
        public float Distance;
        public bool ComeBack;
    }

    public struct TeleportTrack : ITlTrack<TeleportClip>
    {
        [Pulse(typeof(TeleportKind), On = ClipEvent.Start)]
        public static float JumpAtClipStart(in TlFrame<TeleportClip> frame)
            => frame.Direction * frame.Clip.Distance;

        [Pulse(typeof(TeleportKind), On = ClipEvent.End)]
        public static float ReturnAtClipEnd(in TlFrame<TeleportClip> frame)
            => frame.Clip.ComeBack ? -frame.Direction * frame.Clip.Distance : 0f;
    }

    [TlKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
    public struct TeleportKind
    {
        public static void Apply(in float jumpDistance, ref Teleported player)
            => player.Offset += jumpDistance;
    }

    [TlState]
    public partial struct Teleported
    {
        public float Offset;
    }
}
