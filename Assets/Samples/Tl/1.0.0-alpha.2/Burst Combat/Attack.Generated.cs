using System.Runtime.CompilerServices;
using Tl;
using Unity.Collections.LowLevel.Unsafe;

namespace Tl.Samples.BurstCombat
{
    public readonly unsafe partial struct Attack : ITimeline
    {
        public const ushort Id = 0;
        public const uint Duration = 40;
        public const ushort TrackCount = 2;
        public const ushort ClipCount = 2;
        public const ushort RegionCount = 3;
        public const uint GeneratedSourceBytes = 8142;
        public const uint StaticDataBytes = 14;
        public const uint RuntimeHeapBytes = 0;

        public ref struct Data
        {
            internal Playback<Attack>* Playback;
            internal FighterPose* CurrentPose;
            internal FighterPose* NextPose;
            internal CombatStats* Combat;

            public Data(
                ref Playback<Attack> playback,
                in FighterPose currentPose,
                ref FighterPose nextPose,
                ref CombatStats combat)
            {
                Playback = (Playback<Attack>*)UnsafeUtility.AddressOf(ref playback);
                CurrentPose = (FighterPose*)UnsafeUtilityExtensions.AddressOf(in currentPose);
                NextPose = (FighterPose*)UnsafeUtility.AddressOf(ref nextPose);
                Combat = (CombatStats*)UnsafeUtility.AddressOf(ref combat);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Playback<Attack> Start(uint gameTick)
        {
            return Timeline.Start<Attack>(gameTick);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryStop(in Playback<Attack> playback, out Playback<Attack> stopped)
        {
            return Timeline.TryStop(in playback, out stopped);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySeek(ref Data data, int delta)
        {
            if (!CanRun(ref data))
                return false;

            var before = *data.Playback;
            var distance = (long)delta;
            if (before.Position < 0L
                || before.Position > Duration
                || distance > 0L && before.Position > long.MaxValue - distance
                || distance < 0L && before.Position < long.MinValue - distance)
                return false;

            var targetPosition = before.Position + distance;
            if (targetPosition < 0L || targetPosition > Duration)
                return false;
            if (delta == 0)
                return true;

            var position = before.Position;
            var gameTick = before.GameTick;
            if (delta > 0)
            {
                while (position < targetPosition)
                {
                    var local = (uint)position;
                    Apply(local, gameTick, position, false, ref data);
                    position++;
                    gameTick++;
                }
            }
            else
            {
                while (position > targetPosition)
                {
                    gameTick--;
                    position--;
                    var local = (uint)position;
                    Apply(local, gameTick, position, true, ref data);
                }
            }

            *data.Playback = Timeline.CreateTypedPlayback<Attack>(
                targetPosition,
                unchecked(before.GameTick + (uint)delta),
                before.Flags);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CanRun(ref Data data)
        {
            if (data.Playback == null
                || data.CurrentPose == null
                || data.NextPose == null
                || data.Combat == null)
                return false;
            var before = *data.Playback;
            return (before.Flags & (PlaybackFlags.Started | PlaybackFlags.Stopped)) == PlaybackFlags.Started;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Apply(uint local, uint gameTick, long position, bool reverse, ref Data data)
        {
            var flags = reverse ? FrameFlags.Reverse : FrameFlags.None;
            if (local == 0u)
                flags |= FrameFlags.TimelineStart;
            if (local == Duration - 1u)
                flags |= FrameFlags.TimelineEnd;
            if (position == Duration - 1u)
                flags |= reverse ? FrameFlags.CompletedBefore : FrameFlags.CompletedAfter;

            if (reverse)
            {
                if (local == 10u)
                    ApplyDamage(local, gameTick, flags, ref data);
                ApplyAnimation(local, gameTick, flags, ref data);
            }
            else
            {
                ApplyAnimation(local, gameTick, flags, ref data);
                if (local == 10u)
                    ApplyDamage(local, gameTick, flags, ref data);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyAnimation(uint local, uint gameTick, FrameFlags flags, ref Data data)
        {
            var track = default(AnimationTrack);
            var clip = new AnimationClip(2f, 1f);
            if (local == 0u)
                flags |= FrameFlags.ClipStart;
            if (local == Duration - 1u)
                flags |= FrameFlags.ClipEnd;
            var frame = new Frame<AnimationTrack, AnimationClip>(in track, in clip, gameTick, local, 0L, 0, flags);
            AnimationTrack.Seek(in frame, in *data.CurrentPose, ref *data.NextPose);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyDamage(uint local, uint gameTick, FrameFlags flags, ref Data data)
        {
            var track = default(DamageTrack);
            var clip = new DamageClip(10f);
            flags |= FrameFlags.ClipStart | FrameFlags.ClipEnd;
            var frame = new Frame<DamageTrack, DamageClip>(in track, in clip, gameTick, local, 0L, 1, flags);
            DamageTrack.Seek(in frame, ref *data.Combat);
        }
    }
}
