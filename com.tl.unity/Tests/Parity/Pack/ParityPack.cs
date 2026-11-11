using Tl;

namespace ParityPack
{
    [TlState]
    public partial struct Screen
    {
        public int Caption;
        public int Mood;
        public int Reel;
    }

    [TlKind]
    public struct Caption
    {
        public static void Combine(ref int total, in int next) => total = next;
        public static void Apply(in int line, ref Screen screen) => screen.Caption = line;
        public static void Idle(ref Screen screen) => screen.Caption = -1;
    }

    [TlKind]
    public struct Mood
    {
        public static void Combine(ref int total, in int next) => total = next;
        public static void Apply(in int mood, ref Screen screen) => screen.Mood = mood;
        public static void Idle(ref Screen screen) => screen.Mood = 0;
    }

    [TlKind]
    public struct Reel
    {
        public static void Combine(ref int total, in int next) => total = next;
        public static void Apply(in int reel, ref Screen screen) => screen.Reel = reel;
    }

    [TlClip("Parity/Mood")]
    public struct MoodClip
    {
        public int Mood;
    }

    public struct MoodTrack : ITlTrack<MoodClip>
    {
        [Feeds(typeof(Mood))]
        public static int Which(in TlFrame<MoodClip> frame) => frame.Clip.Mood;
    }
}
