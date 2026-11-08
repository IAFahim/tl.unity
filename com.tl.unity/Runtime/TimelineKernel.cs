namespace Tl.Unity
{
    public static unsafe class TimelineKernel
    {
        public const int MaxSlotsPerEntity = 64;

        public static global::Tl.LibraryTimeline* GetTimeline(in global::Tl.LibraryView view, ushort timeline)
        {
            return global::Tl.LibraryViewReader.GetTimeline(in view, new global::Tl.TimelineId(timeline));
        }
    }
}
