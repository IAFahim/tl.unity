using System;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;

namespace Tl.Unity
{
    public static unsafe class TimelineClips
    {
        public static ulong PairKey<TTrack, TClip>() where TTrack : unmanaged where TClip : unmanaged
        {
            return Hash(StableName(typeof(TTrack)) + "\0" + StableName(typeof(TClip)));
        }

        public static ulong MarkerKey<TMarker>() where TMarker : unmanaged
        {
            return Hash(StableName(typeof(TMarker)));
        }

        public static bool TryRead<TTrack, TClip>(in global::Tl.LibraryView view, in Playback playback, ulong pairKey, out TTrack track, out TClip clip)
            where TTrack : unmanaged where TClip : unmanaged
        {
            track = default;
            clip = default;
            var timeline = global::Tl.LibraryViewReader.GetTimeline(in view, new global::Tl.TimelineId(playback.Timeline));
            var stage = global::Tl.LibraryViewReader.StageAt(timeline, playback.Tick);
            if (stage == null)
                return false;
            for (var i = 0; i < stage->StepCount; i++)
            {
                var step = stage->Steps + i;
                if (step->PairKey != pairKey || step->FrameRow == null || playback.Tick < step->WindowStart || playback.Tick >= step->WindowEnd)
                    continue;
                var row = (byte*)step->FrameRow;
                UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref track), row, sizeof(TTrack));
                UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref clip), row + sizeof(TTrack), sizeof(TClip));
                return true;
            }
            return false;
        }

        static ulong Hash(string text)
        {
            var hash = 14695981039346656037UL;
            foreach (var c in text)
                hash = (hash ^ c) * 1099511628211UL;
            return hash | 1UL;
        }

        static string StableName(Type type)
        {
            return Name(type) + ", " + type.Assembly.GetName().Name;
        }

        static string Name(Type type)
        {
            var prefix = type.IsNested ? Name(type.DeclaringType) + "+" : type.Namespace != null ? type.Namespace + "." : string.Empty;
            if (!type.IsConstructedGenericType)
                return prefix + type.Name;
            var text = new StringBuilder(prefix).Append(type.Name).Append('<');
            foreach (var argument in type.GetGenericArguments())
                text.Append(StableName(argument)).Append(',');
            return text.ToString();
        }
    }
}
