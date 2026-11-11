using System.Runtime.InteropServices;

namespace Tl;

[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct PlaybackHorizon
{
    internal long World;
    internal ulong Generation;
    internal byte Limit;
    internal byte Forward;
}
