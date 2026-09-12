using Tl;
using Tl.Unity;
using Unity.Entities;

namespace Tl.Unity.Tests;

public struct Health : IComponentData
{
    public float Value;
}

public struct Resistance : IComponentData
{
    public float Scale;
}

public readonly struct Record(char kind, int code, float value, uint tick, uint game, long cycle, FrameFlags flags)
{
    public readonly char Kind = kind;
    public readonly int Code = code;
    public readonly float Value = value;
    public readonly uint Tick = tick;
    public readonly uint Game = game;
    public readonly long Cycle = cycle;
    public readonly FrameFlags Flags = flags;
}

public static unsafe class TimelineConsumers
{
    public const int Capacity = 256;
    public static readonly Record[] Records = new Record[Capacity];
    public static int Count;

    static TimelineConsumers()
    {
        PairRuntime<AlphaTrack, AlphaClip>.Consume(&AlphaExecute, &AlphaBind);
        PairRuntime<BetaTrack, BetaClip>.Consume(&BetaExecute, &NoBind);
        PairRuntime<PhiTrack, PhiClip>.Consume(&PhiExecute, &NoBind);
        PairRuntime<PhiTrack, PhiClip>.Consume(&PhiExecuteSecond, &NoBind);
        PairRuntime<BlendTrack, BlendClip>.Consume(&BlendExecute, &NoBind);
        TimelineEcs.Column<Health>();
    }

    public static void Reset() => Count = 0;

    public static Record At(int index) => Records[index];

    private static void Append(in Record record)
    {
        if (Count == Capacity) throw new System.InvalidOperationException("Record capacity exhausted.");
        Records[Count++] = record;
    }

    private static void NoBind(ulong* keys, int keyCount, byte* table)
    {
    }

    private static void AlphaBind(ulong* keys, int keyCount, byte* table)
    {
        var health = TimelineEcs.ComponentKey<Health>();
        for (var i = 0; i < keyCount; i++)
            if (keys[i] == health)
            {
                table[0] = (byte)(i + 1);
                break;
            }
    }

    private static void AlphaExecute(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
    {
        AlphaClip scratch = default;
        var current = TickFrame.ToFrame<AlphaTrack, AlphaClip>(slot, gameTick, tick, cycle, flags, ref scratch);
        Append(new Record('A', current.Track.Code, current.Clip.Value, current.TimelineTick, current.GameTick, current.Cycle, current.Flags));
        var health = (Health*)columns[0];
        if (health != null)
            health[row].Value += current.Clip.Value * current.Track.Code;
    }

    private static void BetaExecute(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
        => throw new System.InvalidOperationException("Beta consumer failed.");

    private static void PhiExecute(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
    {
        PhiClip scratch = default;
        var current = TickFrame.ToFrame<PhiTrack, PhiClip>(slot, gameTick, tick, cycle, flags, ref scratch);
        Append(new Record('1', current.Track.Code, current.Clip.Value, current.TimelineTick, current.GameTick, current.Cycle, current.Flags));
    }

    private static void PhiExecuteSecond(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
    {
        PhiClip scratch = default;
        var current = TickFrame.ToFrame<PhiTrack, PhiClip>(slot, gameTick, tick, cycle, flags, ref scratch);
        Append(new Record('2', current.Track.Code, current.Clip.Value, current.TimelineTick, current.GameTick, current.Cycle, current.Flags));
    }

    private static void BlendExecute(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
    {
        BlendClip scratch = default;
        var current = TickFrame.ToFrame<BlendTrack, BlendClip>(slot, gameTick, tick, cycle, flags, ref scratch);
        Append(new Record('B', 0, current.Clip.Amount, current.TimelineTick, current.GameTick, current.Cycle, current.Flags));
    }
}
