using System;
using Tl;

namespace Demo
{
    public struct Motion { public float X; }
    public struct Health { public int Value; }
    public struct WalkStat { public float Value; }
    public struct CueLog { public int Count; public int Last; }

    public struct Speed { public float Value; public byte Priority; }

    [TimelineKind]
    public struct Locomotion
    {
        public static void Combine(ref Speed total, in Speed next) { if (next.Priority >= total.Priority) total = next; }
        public static void Apply(in Speed total, ref Motion motion) => motion.X += total.Value;
    }

    [TimelineKind(Accepts = KindEntries.Pulses)]
    public struct Damage
    {
        public static void Combine(ref int total, in int next) => total += next;
        public static void Apply(in int total, ref Health health) => health.Value -= total;
    }

    [TimelineKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
    public struct Cue
    {
        public static void Apply(in int id, ref CueLog log)
        {
            if (id == 0)
                return;
            log.Count++;
            log.Last = id;
        }
    }

    public struct WalkClip { public float Speed; public byte Priority; }
    public struct WalkTrack : IBlend<WalkClip>
    {
        public float Scale;
        public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result) => result = first;
    }

    public struct HitClip { public int Amount; public int CueId; }
    public struct HitTrack : IBlend<HitClip>
    {
        public float Scale;
        public void Blend(in HitClip first, in HitClip second, float factor, out HitClip result) => result = first;
    }

    public struct Walk : ITrack<WalkTrack, WalkClip>
    {
        [Feeds(typeof(Locomotion))]
        public static Speed Evaluate(in Frame<WalkTrack, WalkClip> f, in WalkStat stat)
            => new Speed { Value = f.Direction * f.Clip.Speed * f.Track.Scale * stat.Value, Priority = f.Clip.Priority };
    }

    [TimelineSystemMember(typeof(Vitals))]
    public struct Hit : ITrack<HitTrack, HitClip>
    {
        [Pulse(typeof(Damage), On = ClipEvent.Start)]
        public static int Start(in Frame<HitTrack, HitClip> f) => f.Direction > 0 ? f.Clip.Amount : 0;

        [Pulse(typeof(Cue), On = ClipEvent.Start)]
        public static int Announce(in Frame<HitTrack, HitClip> f) => f.Direction > 0 ? f.Clip.CueId : 0;
    }

    [TimelineSystem]
    public ref partial struct Movement
    {
        public Span<Motion> Motion;
        public ReadOnlySpan<WalkStat> WalkStat;
    }

    [TimelineSystem]
    public ref partial struct Vitals
    {
        public Span<Health> Health;
        public Span<CueLog> Cues;
    }
}
