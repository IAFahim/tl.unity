using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tl;

public enum FrameFlags : ushort
{
    None = 0,
    ClipStart = 1 << 0,
    ClipEnd = 1 << 1,
    TimelineStart = 1 << 2,
    TimelineEnd = 1 << 3,
    CompletedBefore = 1 << 4,
    CompletedAfter = 1 << 5,
    Looping = 1 << 6,
    Reverse = 1 << 7,
    Synthetic = 1 << 8,
    Interrupted = 1 << 9,
}


