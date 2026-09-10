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

    public struct Receipt
    {
        public int Calls;
        public int Starts;
        public int Interiors;
        public int Ends;
        public int ReverseCalls;
        public int DirectionTotal;
        public int TimelineStarts;
        public int TimelineEnds;
        public int CompletedBefore;
        public int CompletedAfter;
        public uint LastGameTick;
        public uint LastTimelineTick;
        public long LastCycle;
        public FrameFlags LastFlags;
    }

    public readonly struct PoseClip
    {
        public readonly float X;
        public readonly float Y;

        public PoseClip(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    public readonly struct DamageClip
    {
        public readonly float Amount;

        public DamageClip(float amount)
        {
            Amount = amount;
        }
    }

    public readonly struct PoseTrack
    {
        public static void Seek(in Frame<PoseTrack, PoseClip> frame, in Pose current, ref Pose next, ref Receipt receipt)
        {
            next.X = current.X + frame.Direction * frame.Clip.X;
            next.Y = current.Y + frame.Direction * frame.Clip.Y;
            Gate.Observe(frame.GameTick, frame.TimelineTick, frame.Cycle, frame.Flags, frame.Direction, ref receipt);
        }
    }

    public readonly struct DamageTrack
    {
        public static void Seek(in Frame<DamageTrack, DamageClip> frame, ref Health health, ref Receipt receipt)
        {
            health.Value -= frame.Direction * frame.Clip.Amount;
            Gate.Observe(frame.GameTick, frame.TimelineTick, frame.Cycle, frame.Flags, frame.Direction, ref receipt);
        }
    }

    public readonly unsafe struct Gate : ITimeline
    {
        public const ushort Id = 7;
        public const uint Duration = 4;

        public ref struct Data
        {
            internal Playback<Gate>* Playback;
            internal Pose* Current;
            internal Pose* Next;
            internal Health* Health;
            internal Receipt* Receipt;

            public Data(ref Playback<Gate> playback, in Pose current, ref Pose next, ref Health health, ref Receipt receipt)
            {
                Playback = (Playback<Gate>*)UnsafeUtility.AddressOf(ref playback);
                Current = (Pose*)UnsafeUtilityExtensions.AddressOf(in current);
                Next = (Pose*)UnsafeUtility.AddressOf(ref next);
                Health = (Health*)UnsafeUtility.AddressOf(ref health);
                Receipt = (Receipt*)UnsafeUtility.AddressOf(ref receipt);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Playback<Gate> Start(uint gameTick)
        {
            return Timeline.Start<Gate>(gameTick);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryStop(in Playback<Gate> playback, out Playback<Gate> stopped)
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
                    Apply((uint)position, gameTick, position, false, ref data);
                    position++;
                    gameTick = unchecked(gameTick + 1u);
                }
            }
            else
            {
                while (position > targetPosition)
                {
                    gameTick = unchecked(gameTick - 1u);
                    position--;
                    Apply((uint)position, gameTick, position, true, ref data);
                }
            }

            *data.Playback = Timeline.CreateTypedPlayback<Gate>(
                targetPosition,
                unchecked(before.GameTick + (uint)delta),
                before.Flags);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CanRun(ref Data data)
        {
            if (data.Playback == null
                || data.Current == null
                || data.Next == null
                || data.Health == null
                || data.Receipt == null)
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
                if (local == 1u)
                    ApplyDamage(local, gameTick, flags, ref data);
                ApplyPose(local, gameTick, flags, ref data);
            }
            else
            {
                ApplyPose(local, gameTick, flags, ref data);
                if (local == 1u)
                    ApplyDamage(local, gameTick, flags, ref data);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyPose(uint local, uint gameTick, FrameFlags flags, ref Data data)
        {
            var track = default(PoseTrack);
            var clip = new PoseClip(2f, 1f);
            if (local == 0u)
                flags |= FrameFlags.ClipStart;
            if (local == Duration - 1u)
                flags |= FrameFlags.ClipEnd;
            var frame = new Frame<PoseTrack, PoseClip>(in track, in clip, gameTick, local, 0L, 0, flags);
            PoseTrack.Seek(in frame, in *data.Current, ref *data.Next, ref *data.Receipt);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyDamage(uint local, uint gameTick, FrameFlags flags, ref Data data)
        {
            var track = default(DamageTrack);
            var clip = new DamageClip(10f);
            flags |= FrameFlags.ClipStart | FrameFlags.ClipEnd;
            var frame = new Frame<DamageTrack, DamageClip>(in track, in clip, gameTick, local, 0L, 1, flags);
            DamageTrack.Seek(in frame, ref *data.Health, ref *data.Receipt);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Observe(
            uint gameTick,
            uint timelineTick,
            long cycle,
            FrameFlags flags,
            int direction,
            ref Receipt receipt)
        {
            receipt.Calls++;
            receipt.DirectionTotal += direction;
            if ((flags & FrameFlags.Reverse) != 0)
                receipt.ReverseCalls++;
            var boundaries = flags & (FrameFlags.ClipStart | FrameFlags.ClipEnd);
            if ((boundaries & FrameFlags.ClipStart) != 0)
                receipt.Starts++;
            if (boundaries == 0)
                receipt.Interiors++;
            if ((boundaries & FrameFlags.ClipEnd) != 0)
                receipt.Ends++;
            if ((flags & FrameFlags.TimelineStart) != 0)
                receipt.TimelineStarts++;
            if ((flags & FrameFlags.TimelineEnd) != 0)
                receipt.TimelineEnds++;
            if ((flags & FrameFlags.CompletedBefore) != 0)
                receipt.CompletedBefore++;
            if ((flags & FrameFlags.CompletedAfter) != 0)
                receipt.CompletedAfter++;
            receipt.LastGameTick = gameTick;
            receipt.LastTimelineTick = timelineTick;
            receipt.LastCycle = cycle;
            receipt.LastFlags = flags;
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
                root.Loops = 0;
                var tracks = builder.Allocate(ref root.Tracks, 2);
                tracks[0] = new TimelineTrackBlob { Index = 0, Operation = 0, Payload = 0 };
                tracks[1] = new TimelineTrackBlob { Index = 1, Operation = 1, Payload = 0 };
                var clips = builder.Allocate(ref root.Clips, 2);
                clips[0] = new TimelineClipBlob { TrackIndex = 0, Payload = 0, Start = 0, End = Duration };
                clips[1] = new TimelineClipBlob { TrackIndex = 1, Payload = 1, Start = 1, End = 2 };
                return builder.CreateBlobAssetReference<TimelineBlob>(allocator);
            }
        }
    }

    public readonly struct LoopClip
    {
        public readonly int Value;

        public LoopClip(int value)
        {
            Value = value;
        }
    }

    public readonly struct LoopTrack
    {
        public static void Seek(in Frame<LoopTrack, LoopClip> frame, ref Receipt receipt)
        {
            Gate.Observe(frame.GameTick, frame.TimelineTick, frame.Cycle, frame.Flags, frame.Direction, ref receipt);
        }
    }

    public readonly unsafe struct LoopGate : ITimeline
    {
        public const ushort Id = 8;
        public const uint Duration = 3;

        public ref struct Data
        {
            internal Playback<LoopGate>* Playback;
            internal Receipt* Receipt;

            public Data(ref Playback<LoopGate> playback, ref Receipt receipt)
            {
                Playback = (Playback<LoopGate>*)UnsafeUtility.AddressOf(ref playback);
                Receipt = (Receipt*)UnsafeUtility.AddressOf(ref receipt);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Playback<LoopGate> Start(uint gameTick)
        {
            return Timeline.Start<LoopGate>(gameTick);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySeek(ref Data data, int delta)
        {
            if (data.Playback == null || data.Receipt == null)
                return false;
            var before = *data.Playback;
            if ((before.Flags & (PlaybackFlags.Started | PlaybackFlags.Stopped)) != PlaybackFlags.Started)
                return false;

            var distance = (long)delta;
            if (distance > 0L && before.Position > long.MaxValue - distance
                || distance < 0L && before.Position < long.MinValue - distance)
                return false;
            var targetPosition = before.Position + distance;
            if (delta == 0)
                return true;

            var cycle = before.Position / Duration;
            var remainder = before.Position - cycle * Duration;
            if (remainder < 0L)
            {
                remainder += Duration;
                cycle--;
            }
            var local = (uint)remainder;
            var position = before.Position;
            var gameTick = before.GameTick;
            if (delta > 0)
            {
                while (position < targetPosition)
                {
                    Apply(local, gameTick, cycle, false, ref data);
                    position++;
                    gameTick = unchecked(gameTick + 1u);
                    if (local == Duration - 1u)
                    {
                        local = 0u;
                        cycle++;
                    }
                    else
                        local++;
                }
            }
            else
            {
                while (position > targetPosition)
                {
                    gameTick = unchecked(gameTick - 1u);
                    if (local == 0u)
                    {
                        local = Duration - 1u;
                        cycle--;
                    }
                    else
                        local--;
                    position--;
                    Apply(local, gameTick, cycle, true, ref data);
                }
            }

            *data.Playback = Timeline.CreateTypedPlayback<LoopGate>(
                targetPosition,
                unchecked(before.GameTick + (uint)delta),
                before.Flags);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Apply(uint local, uint gameTick, long cycle, bool reverse, ref Data data)
        {
            var flags = FrameFlags.Looping;
            if (reverse)
                flags |= FrameFlags.Reverse;
            if (local == 0u)
                flags |= FrameFlags.TimelineStart | FrameFlags.ClipStart;
            if (local == Duration - 1u)
                flags |= FrameFlags.TimelineEnd | FrameFlags.ClipEnd;
            var track = default(LoopTrack);
            var clip = new LoopClip(1);
            var frame = new Frame<LoopTrack, LoopClip>(in track, in clip, gameTick, local, cycle, 0, flags);
            LoopTrack.Seek(in frame, ref *data.Receipt);
        }
    }
}
