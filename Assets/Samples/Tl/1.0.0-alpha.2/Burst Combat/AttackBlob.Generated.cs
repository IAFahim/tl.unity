using Tl;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Tl.Samples.BurstCombat
{
    public static unsafe partial class Attack
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
                    + 3 * UnsafeUtility.SizeOf<TimelineRegionBlob>()
                    + 4 * UnsafeUtility.SizeOf<TimelineFrameBlob>()
                    + 2 * sizeof(ulong));
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
                root.TrackKindCount = 2;
                root.ClipKindCount = 2;
                root.Loops = 0;

                var regions = builder.Allocate(ref root.Regions, 3);
                regions[0] = new TimelineRegionBlob { Start = 0, End = 10, FrameStart = 0, FrameCount = 1 };
                regions[1] = new TimelineRegionBlob { Start = 10, End = 11, FrameStart = 1, FrameCount = 2 };
                regions[2] = new TimelineRegionBlob { Start = 11, End = 40, FrameStart = 3, FrameCount = 1 };

                var frames = builder.Allocate(ref root.Frames, 4);
                frames[0] = new TimelineFrameBlob { Start = 0, End = 40, TrackIndex = 0, Operation = 0, TrackKind = 0, ClipKind = 0, DataWord = 0 };
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
