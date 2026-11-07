using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    static class TlTimelineQueries
    {
        public static EntityQuery Pending(ref SystemState state)
        {
            return new EntityQueryBuilder(Allocator.Temp).WithAllRW<TlTimelineSet>().WithAllRW<TlUnregistered>().Build(ref state);
        }
    }
}
