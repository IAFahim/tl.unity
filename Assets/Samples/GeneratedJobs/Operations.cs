using Tl;
using Unity.Entities;

namespace Tl.Samples.GeneratedJobs
{
    public struct Bias : IComponentData
    {
        public int Value;
    }

    public struct Total : IComponentData
    {
        public int Value;
        public int Calls;
        public uint LastGameTick;
        public FrameFlags Flags;
    }

    public readonly struct ValueClip
    {
        public readonly int Value;

        public ValueClip(int value)
        {
            Value = value;
        }
    }

    public readonly struct ValueTrack : IBlend<ValueClip>
    {
        public readonly int Value;

        public ValueTrack(int value)
        {
            Value = value;
        }

        public void Blend(in ValueClip first, in ValueClip second, float factor, out ValueClip result)
        {
            result = first;
        }
    }

    public readonly struct ValueJob : ITimelineJob<ValueTrack, ValueClip>
    {
        public static void Execute(in Frame<ValueTrack, ValueClip> frame, in Bias bias, ref Total total)
        {
            total.Value += frame.Direction * (frame.Track.Value + frame.Clip.Value + bias.Value);
            total.Calls++;
            total.LastGameTick = frame.GameTick;
            total.Flags |= frame.Flags;
        }
    }
}
