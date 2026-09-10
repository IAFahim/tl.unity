using System.Runtime.CompilerServices;
using Tl;
using Unity.Collections.LowLevel.Unsafe;

namespace Tl.Samples.BurstCombat
{
    public static unsafe partial class Attack
    {
        public const ushort Id = 0;
        public const uint Duration = 40;
        public const ushort TrackCount = 2;
        public const ushort ClipCount = 2;
        public const ushort RegionCount = 3;
        public const uint GeneratedSourceBytes = 8022;
        public const uint StaticDataBytes = 14;
        public const uint RuntimeHeapBytes = 0;

        public readonly struct Input : ITimelineInput<Output>
        {
            internal readonly FighterPose* CurrentPose;

            public Input(in FighterPose currentPose)
            {
                CurrentPose = (FighterPose*)UnsafeUtilityExtensions.AddressOf(in currentPose);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryForward(ushort id, in Playback playback, uint tick, ref Output output, out Playback next)
            {
                return Attack.TryForward(id, in playback, tick, in this, ref output, out next);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryBackward(ushort id, in Playback playback, uint tick, ref Output output, out Playback next)
            {
                return Attack.TryBackward(id, in playback, tick, in this, ref output, out next);
            }
        }

        public struct Output
        {
            internal FighterPose* NextPose;
            internal CombatStats* Combat;

            public Output(ref FighterPose nextPose, ref CombatStats combat)
            {
                NextPose = (FighterPose*)UnsafeUtility.AddressOf(ref nextPose);
                Combat = (CombatStats*)UnsafeUtility.AddressOf(ref combat);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryForward(ushort id, in Playback playback, uint tick, in Input input, ref Output output, out Playback next)
        {
            if (!CanRun(id, in playback, in input, ref output))
            {
                next = playback;
                return false;
            }

            ApplyForward(playback.Tick, tick, in input, ref output);
            var flags = PlaybackFlags.Started;
            if (tick >= Duration - 1)
                flags |= PlaybackFlags.Completed;
            next = new Playback(tick, playback.Cycles, id, flags);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBackward(ushort id, in Playback playback, uint tick, in Input input, ref Output output, out Playback next)
        {
            if (!CanRun(id, in playback, in input, ref output))
            {
                next = playback;
                return false;
            }

            ApplyBackward(playback.Tick, tick, in input, ref output);
            var flags = PlaybackFlags.Started;
            if (tick == 0)
                flags |= PlaybackFlags.Completed;
            next = new Playback(tick, playback.Cycles, id, flags);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CanRun(ushort id, in Playback playback, in Input input, ref Output output)
        {
            return id == Id
                && playback.Owner == id
                && (playback.Flags & (PlaybackFlags.Started | PlaybackFlags.Stopped)) == PlaybackFlags.Started
                && input.CurrentPose != null
                && output.NextPose != null
                && output.Combat != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyForward(uint previous, uint tick, in Input input, ref Output output)
        {
            if (tick < 40)
            {
                var track = default(AnimationTrack);
                var clip = new AnimationClip(2, 1);
                var state = tick == 39 ? ClipState.Exit : ClipState.Stay;
                AnimationTrack.Forward(in track, in clip, state, tick, in *input.CurrentPose, ref *output.NextPose);
            }

            if (tick == 10)
            {
                var track = default(DamageTrack);
                var clip = new DamageClip(10);
                DamageTrack.Forward(in track, in clip, ClipState.Exit, tick, ref *output.Combat);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyBackward(uint previous, uint tick, in Input input, ref Output output)
        {
            if (tick < 40)
            {
                var track = default(AnimationTrack);
                var clip = new AnimationClip(2, 1);
                var state = tick == 0 ? ClipState.Exit : previous >= 40 ? ClipState.Enter : ClipState.Stay;
                AnimationTrack.Backward(in track, in clip, state, tick, in *input.CurrentPose, ref *output.NextPose);
            }

            if (tick == 10)
            {
                var track = default(DamageTrack);
                var clip = new DamageClip(10);
                DamageTrack.Backward(in track, in clip, ClipState.Exit, tick, ref *output.Combat);
            }
        }
    }
}
