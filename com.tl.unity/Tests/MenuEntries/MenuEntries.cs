using Tl;

[assembly: Tl.Unity.TlMenuEntries]

namespace MenuEntries
{
    [TlState]
    public partial struct Beeps
    {
        public int Count;
    }

    [TlClip("MenuEntries/Beep")]
    public struct BeepClip
    {
        public int Pitch;
    }

    [TlKind]
    public struct BeepKind
    {
        public static void Combine(ref int total, in int next) => total += next;
        public static void Apply(in int total, ref Beeps beeps) => beeps.Count += total;
    }

    public struct BeepTrack : ITlTrack<BeepClip>
    {
        [Feeds(typeof(BeepKind))]
        public static int Evaluate(in TlFrame<BeepClip> frame) => frame.Clip.Pitch;
    }
}
