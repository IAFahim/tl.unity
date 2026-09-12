using Tl;
namespace Tl.Samples.GeneratedJobs
{
    public struct Bias
    {
        public int Value;
    }

    public struct Scale
    {
        public int Value;
    }

    public struct Trace
    {
        public long Order;
        public long ClipSum;
        public long CycleOrder;
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
            result = new ValueClip((int)(first.Value + (second.Value - first.Value) * factor));
        }
    }

    public readonly struct BeforeAfter : IHook
    {
        public static void Execute(in TimelineFrame frame, ref Trace trace)
        {
            trace.Order = unchecked(trace.Order * 10 + 8);
            trace.Calls++;
            trace.LastGameTick = frame.GameTick;
            trace.Flags |= frame.Flags;
        }
    }

    public readonly struct A : ITimelineJob<ValueTrack, ValueClip>
    {
        public static void Execute(in Frame<ValueTrack, ValueClip> frame, in Bias bias, ref Trace trace)
        {
            trace.Order = unchecked(trace.Order * 10 + frame.Track.Value + bias.Value);
            trace.ClipSum += frame.Clip.Value;
            trace.CycleOrder = unchecked(trace.CycleOrder * 10 + frame.Cycle);
            trace.Calls++;
            trace.LastGameTick = frame.GameTick;
            trace.Flags |= frame.Flags;
        }
    }

    public readonly struct B : ITimelineJob<ValueTrack, ValueClip>
    {
        public static void Execute(in Frame<ValueTrack, ValueClip> frame, in Bias secondary, in Scale scale, ref Trace trace)
        {
            trace.Order = unchecked(trace.Order * 10 + frame.Track.Value * scale.Value + secondary.Value);
            trace.ClipSum += frame.Clip.Value;
            trace.Calls++;
            trace.LastGameTick = frame.GameTick;
            trace.Flags |= frame.Flags;
        }
    }
}
