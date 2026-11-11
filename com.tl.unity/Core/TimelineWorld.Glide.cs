using System.Threading;

namespace Tl;

public sealed unsafe partial class TimelineWorld
{
    static long _nextGlideIdentity;
    long _glideIdentity;
    bool _glideViewEscaped;

    static long NextGlideIdentity()
    {
        var identity = Interlocked.Increment(ref _nextGlideIdentity);
        if (identity <= 0) throw new InvalidOperationException("World identity space exhausted.");
        return identity;
    }

    long GlideIdentity()
    {
        var identity = Volatile.Read(ref _glideIdentity);
        if (identity != 0) return identity;
        var next = NextGlideIdentity();
        identity = Interlocked.CompareExchange(ref _glideIdentity, next, 0);
        return identity == 0 ? next : identity;
    }

    void AdvanceGlideRange(PlaybackTable players, LibraryTimeline* timelines, int timelineCount, bool forward, byte* horizons)
    {
        var reverse = SlotWord.WorldReverse(forward);
        var playing = players.Playing;
        var capacity = players.SlotsPerEntity;
        for (var w = 0; w < players.PlayingWords; w++)
        {
            var bits = playing[w];
            var first = w << 6;
            if (bits == ulong.MaxValue && players.SpilledBits[w] == 0 && FullInlineWord(players.Counts + first, capacity) && Horizon.Decrement64(horizons + first))
            {
                GlideKernel.RunScalar((ulong*)(players.InlineSlots + (nuint)first * (nuint)capacity), 64 * capacity, reverse);
#if TL_CHECKED
                for (var i = 0; i < 64; i++) Checked.Visited();
#endif
                continue;
            }
            while (bits != 0)
            {
                var entity = first + KindStore.Tz(bits);
                bits &= bits - 1;
                var horizon = horizons[entity];
                if (horizon != 0)
                {
                    horizons[entity] = (byte)(horizon - 1);
                    GlideKernel.RunScalar((ulong*)players.SlotsOf(entity), players.CountOf(entity), reverse);
#if TL_CHECKED
                    Checked.Visited();
#endif
                }
                else
                {
                    AdvanceEntity(players, entity, timelines, timelineCount, forward);
                    horizons[entity] = Horizon.OfRun(players.SlotsOf(entity), players.CountOf(entity), timelines, timelineCount, reverse, players.HorizonLimit);
                }
            }
        }
    }

    static bool FullInlineWord(int* counts, int capacity)
    {
        for (var i = 0; i < 64; i++)
            if (counts[i] != capacity) return false;
        return true;
    }
}
