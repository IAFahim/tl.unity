using Tl;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Tl.Samples.BurstCombat
{
    public readonly unsafe partial struct Attack
    {
        public static TimelineReport Report
        {
            get
            {
                return new TimelineReport(
                    1,
                    Id,
                    Duration,
                    TrackCount,
                    ClipCount,
                    RegionCount,
                    GeneratedSourceBytes,
                    StaticDataBytes,
                    BlobBytes,
                    RuntimeHeapBytes);
            }
        }

        public static uint BlobBytes
        {
            get
            {
                return (uint)(
                    UnsafeUtility.SizeOf<TimelineBlob>()
                    + 2 * UnsafeUtility.SizeOf<TimelineTrackBlob>()
                    + 2 * UnsafeUtility.SizeOf<TimelineClipBlob>());
            }
        }

        public static BlobAssetReference<TimelineBlob> CreateBlob(Allocator allocator)
        {
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<TimelineBlob>();
                root.PlanVersion = 1;
                root.TimelineId = Id;
                root.Duration = Duration;
                root.TrackCount = TrackCount;
                root.ClipCount = ClipCount;
                root.Loops = 0;

                var tracks = builder.Allocate(ref root.Tracks, 2);
                tracks[0] = new TimelineTrackBlob { Index = 0, Operation = 0, Payload = 0 };
                tracks[1] = new TimelineTrackBlob { Index = 1, Operation = 1, Payload = 0 };

                var clips = builder.Allocate(ref root.Clips, 2);
                clips[0] = new TimelineClipBlob { TrackIndex = 0, Payload = 0, Start = 0, End = Duration };
                clips[1] = new TimelineClipBlob { TrackIndex = 1, Payload = 1, Start = 10, End = 11 };
                return builder.CreateBlobAssetReference<TimelineBlob>(allocator);
            }
        }
    }
}
