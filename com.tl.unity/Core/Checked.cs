using System.Runtime.CompilerServices;
using static System.Runtime.CompilerServices.MethodImplOptions;

namespace Tl;

internal static class Checked
{
#if TL_CHECKED
    internal static long VisitedEntities;

    internal static void ResetVisited() => VisitedEntities = 0;

    [MethodImpl(AggressiveInlining)]
    internal static void Visited() => System.Threading.Interlocked.Increment(ref VisitedEntities);
#endif
}
