using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    [BurstCompile]
    [UpdateInGroup(typeof(TimelineSimulationGroup), OrderFirst = true)]
    public partial struct TlRequestResolveSystem : ISystem
    {
        EntityQuery _requests;
        BufferTypeHandle<TimelineRequest> _handle;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _requests = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TimelineRequest>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _requests.SetChangedVersionFilter(ComponentType.ReadWrite<TimelineRequest>());
            _handle = state.GetBufferTypeHandle<TimelineRequest>(false);
            state.RequireForUpdate<TlLibraryIndex>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (_requests.IsEmpty)
                return;
            _handle.Update(ref state);
            state.Dependency = new TlRequestResolveJob { Ids = SystemAPI.GetSingleton<TlLibraryIndex>().Ids, Requests = _handle }.ScheduleParallel(_requests, state.Dependency);
        }
    }

    [BurstCompile]
    public struct TlRequestResolveJob : IJobChunk
    {
        [ReadOnly] public NativeHashMap<Hash128, ushort> Ids;
        public BufferTypeHandle<TimelineRequest> Requests;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var buffers = chunk.GetBufferAccessor(ref Requests);
            for (var row = 0; row < chunk.Count; row++)
            {
                var requests = buffers[row];
                for (var i = 0; i < requests.Length; i++)
                {
                    var request = requests[i];
                    if (request.Timeline != 0 || !request.Asset.IsValid)
                        continue;
                    if (Ids.TryGetValue(request.Asset, out var id))
                        request.Timeline = id;
                    else
                        Missing();
                    request.Asset = default;
                    requests[i] = request;
                }
            }
        }

        static void Missing()
        {
            UnityEngine.Debug.LogWarning("Tl: code asked an object to play a timeline it cannot play. Drag that timeline into Also Plays on the object (or into a clip field of one of its timelines).");
        }
    }
}
