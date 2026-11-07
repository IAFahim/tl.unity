namespace Tl
{
    public readonly struct TlFrame<TClip>
        where TClip : unmanaged
    {
        public TlFrame(in Frame<TlNoSettings<TClip>, TClip> frame)
        {
            Clip = frame.Clip;
            Tick = frame.TimelineTick;
            ClipLength = frame.ClipLength;
            WithinClip = frame.WithinClip;
            TrackIndex = frame.TrackIndex;
            Flags = frame.Flags;
        }

        public readonly TClip Clip;
        public readonly ushort Tick;
        public readonly ushort ClipLength;
        public readonly ushort WithinClip;
        public readonly ushort TrackIndex;
        public readonly FrameFlags Flags;
        public float Progress => ClipLength == 0 ? 0f : WithinClip / (float)ClipLength;
        public int Direction => Has(FrameFlags.Reverse) ? -1 : 1;
        public bool IsBackward => Has(FrameFlags.Reverse);
        public bool Has(FrameFlags flags) => (Flags & flags) == flags;
    }
}
