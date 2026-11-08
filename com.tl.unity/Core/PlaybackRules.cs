using System.Runtime.CompilerServices;

namespace Tl;

public interface IPlaybackSlots
{
    int Length { get; }
    ref Playback this[int index] { get; }
    bool StartsPending { get; }
    bool TryAdd(in Playback playback);
    bool Admit(ushort timeline);
    void Close(int index);
}

public enum AdvanceResult : byte
{
    Kept,
    Removed,
    Unknown,
}

public static unsafe class PlaybackRules
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLive(in Playback slot) => slot.Timeline != 0 && (slot.Flags & PlaybackFlags.Stopping) == 0;

    public static void Apply<TSlots>(ref TSlots slots, LibraryTimeline* timelines, int timelineCount, RequestOp op, ushort timeline, ushort at)
        where TSlots : struct, IPlaybackSlots
    {
        switch (op)
        {
            case RequestOp.Start:
                Start(ref slots, timelines, timelineCount, timeline, at, out _);
                return;
            case RequestOp.Stop:
                Stop(ref slots, timeline);
                return;
            case RequestOp.StopChannel:
                StopChannel(ref slots, (byte)timeline);
                return;
            case RequestOp.Pause:
                Pause(ref slots, timeline, true);
                return;
            case RequestOp.Resume:
                Pause(ref slots, timeline, false);
                return;
            default:
                StopAll(ref slots);
                return;
        }
    }

    public static bool Start<TSlots>(ref TSlots slots, LibraryTimeline* timelines, int timelineCount, ushort timeline, ushort at, out bool replaced)
        where TSlots : struct, IPlaybackSlots
    {
        replaced = false;
        if (timeline == 0 || (uint)(timeline - 1) >= (uint)timelineCount) return false;
        var tl = timelines + (timeline - 1);
        var duration = tl->Duration;
        if (duration == 0) return false;
        var tick = tl->Loop != 0 ? (ushort)(at % duration) : at < duration ? at : (ushort)(duration - 1);
        var channel = (byte)tl->Channel;
        if (channel != 0)
            for (var i = 0; i < slots.Length; i++)
                if (IsLive(in slots[i]) && slots[i].Channel == channel)
                {
                    slots.Close(i);
                    replaced = true;
                    break;
                }
        var count = slots.Length;
        var free = count;
        for (var i = count - 1; i >= 0; i--)
        {
            if (slots[i].Timeline == 0) free = i;
            else if (slots[i].Timeline == timeline && (slots[i].Flags & PlaybackFlags.Stopping) == 0) return false;
        }
        if (!slots.Admit(timeline)) return false;
        var playback = new Playback { Timeline = timeline, Tick = tick, Channel = channel, Flags = tick > 0 || slots.StartsPending ? PlaybackFlags.StartPending : PlaybackFlags.None };
        if (free < count)
        {
            slots[free] = playback;
            return true;
        }
        return slots.TryAdd(in playback);
    }

    public static bool Stop<TSlots>(ref TSlots slots, ushort timeline) where TSlots : struct, IPlaybackSlots
    {
        for (var i = 0; i < slots.Length; i++)
            if (slots[i].Timeline == timeline && IsLive(in slots[i]))
            {
                slots.Close(i);
                return true;
            }
        return false;
    }

    public static bool StopChannel<TSlots>(ref TSlots slots, byte channel) where TSlots : struct, IPlaybackSlots
    {
        for (var i = 0; i < slots.Length; i++)
            if (IsLive(in slots[i]) && slots[i].Channel == channel)
            {
                slots.Close(i);
                return true;
            }
        return false;
    }

    public static void StopAll<TSlots>(ref TSlots slots) where TSlots : struct, IPlaybackSlots
    {
        for (var i = 0; i < slots.Length; i++)
            if (IsLive(in slots[i])) slots.Close(i);
    }

    public static bool Pause<TSlots>(ref TSlots slots, ushort timeline, bool pause) where TSlots : struct, IPlaybackSlots
    {
        for (var i = 0; i < slots.Length; i++)
        {
            ref var slot = ref slots[i];
            if (slot.Timeline != timeline || !IsLive(in slot)) continue;
            slot.Flags = pause ? slot.Flags | PlaybackFlags.Paused : slot.Flags & ~PlaybackFlags.Paused;
            return true;
        }
        return false;
    }

    public static AdvanceResult Advance(ref Playback slot, LibraryTimeline* timelines, int timelineCount, bool forward)
    {
        var flags = slot.Flags;
        if ((flags & (PlaybackFlags.Ended | PlaybackFlags.Paused)) != 0)
        {
            slot.Flags = flags & ~PlaybackFlags.StartPending;
            return AdvanceResult.Kept;
        }
        if (slot.Timeline == 0 || (uint)(slot.Timeline - 1) >= (uint)timelineCount) return AdvanceResult.Unknown;
        var tl = timelines + (slot.Timeline - 1);
        var duration = tl->Duration;
        if (duration == 0)
        {
            slot.Flags = (flags | PlaybackFlags.Ended) & ~PlaybackFlags.StartPending;
            return AdvanceResult.Kept;
        }
        var handover = false;
        if (((flags & PlaybackFlags.Reverse) != 0) == forward)
        {
            if (slot.Tick == 0)
            {
                if (tl->Loop != 0) slot.Tick = (ushort)(duration - 1);
                else if (tl->Hold != 0) slot.Flags = flags | PlaybackFlags.Ended;
                else return AdvanceResult.Removed;
            }
            else slot.Tick--;
        }
        else if ((uint)slot.Tick + 1u >= duration)
        {
            if (tl->Loop != 0) slot.Tick = 0;
            else if (tl->Next != 0 && (uint)(tl->Next - 1) < (uint)timelineCount)
            {
                slot.Timeline = tl->Next;
                slot.Tick = 0;
                slot.Channel = (byte)timelines[tl->Next - 1].Channel;
                handover = true;
            }
            else if (tl->Hold != 0)
            {
                slot.Tick = (ushort)(duration - 1);
                slot.Flags = flags | PlaybackFlags.Ended;
            }
            else return AdvanceResult.Removed;
        }
        else slot.Tick++;
        slot.Flags = handover ? slot.Flags | PlaybackFlags.StartPending : slot.Flags & ~PlaybackFlags.StartPending;
        return AdvanceResult.Kept;
    }

    public static bool IsPlaying<TSlots>(ref TSlots slots, ushort timeline) where TSlots : struct, IPlaybackSlots
    {
        for (var i = 0; i < slots.Length; i++)
            if (slots[i].Timeline == timeline && IsLive(in slots[i])) return true;
        return false;
    }

    public static bool Find<TSlots>(ref TSlots slots, byte channel, out Playback playback) where TSlots : struct, IPlaybackSlots
    {
        if (channel != 0)
            for (var i = 0; i < slots.Length; i++)
                if (IsLive(in slots[i]) && slots[i].Channel == channel)
                {
                    playback = slots[i];
                    return true;
                }
        playback = default;
        return false;
    }
}
