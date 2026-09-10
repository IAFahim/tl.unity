using System.Runtime.CompilerServices;
using Tl;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace TlUnity.PlayerProbe
{
    public struct Pose
    {
        public float X;
        public float Y;
    }

    public struct Health
    {
        public float Value;
    }

    public static unsafe class Gate
    {
        public const ushort Id = 7;
        public const uint Duration = 32;

        public readonly struct Input : ITimelineInput<Output>
        {
            internal readonly Pose* CurrentPose;

            public Input(in Pose currentPose)
            {
                CurrentPose = (Pose*)UnsafeUtilityExtensions.AddressOf(in currentPose);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryForward(ushort id, in Playback playback, uint tick, ref Output output, out Playback next)
            {
                return Gate.TryForward(id, in playback, tick, in this, ref output, out next);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryBackward(ushort id, in Playback playback, uint tick, ref Output output, out Playback next)
            {
                return Gate.TryBackward(id, in playback, tick, in this, ref output, out next);
            }
        }

        public struct Output
        {
            internal Pose* NextPose;
            internal Health* TargetHealth;

            public Output(ref Pose nextPose, ref Health targetHealth)
            {
                NextPose = (Pose*)UnsafeUtility.AddressOf(ref nextPose);
                TargetHealth = (Health*)UnsafeUtility.AddressOf(ref targetHealth);
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

            if (tick < Duration)
            {
                output.NextPose->X = input.CurrentPose->X + 2;
                output.NextPose->Y = input.CurrentPose->Y + 1;
            }
            if (tick == 10)
                output.TargetHealth->Value -= 10;

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

            if (tick < Duration)
            {
                output.NextPose->X = input.CurrentPose->X - 2;
                output.NextPose->Y = input.CurrentPose->Y - 1;
            }
            if (tick == 10)
                output.TargetHealth->Value += 10;

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
                && output.TargetHealth != null;
        }

        public static BlobAssetReference<TimelineBlob> CreateBlob(Allocator allocator)
        {
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<TimelineBlob>();
                root.PlanVersion = 1;
                root.TimelineId = Id;
                root.Duration = Duration;
                root.TrackCount = 2;
                root.ClipCount = 2;
                root.TrackKindCount = 2;
                root.ClipKindCount = 2;
                root.Loops = 0;
                var regions = builder.Allocate(ref root.Regions, 3);
                regions[0] = new TimelineRegionBlob { Start = 0, End = 10, FrameStart = 0, FrameCount = 1 };
                regions[1] = new TimelineRegionBlob { Start = 10, End = 11, FrameStart = 1, FrameCount = 2 };
                regions[2] = new TimelineRegionBlob { Start = 11, End = Duration, FrameStart = 3, FrameCount = 1 };
                var frames = builder.Allocate(ref root.Frames, 4);
                frames[0] = new TimelineFrameBlob { Start = 0, End = Duration, TrackIndex = 0, Operation = 0, TrackKind = 0, ClipKind = 0, DataWord = 0 };
                frames[1] = frames[0];
                frames[2] = new TimelineFrameBlob { Start = 10, End = 11, TrackIndex = 1, Operation = 1, TrackKind = 1, ClipKind = 1, DataWord = 1 };
                frames[3] = frames[0];
                var data = builder.Allocate(ref root.StaticData, 2);
                data[0] = 0x3f80000040000000UL;
                data[1] = 0x0000000041200000UL;
                return builder.CreateBlobAssetReference<TimelineBlob>(allocator);
            }
        }
    }
}
