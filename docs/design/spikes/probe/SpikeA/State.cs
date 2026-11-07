using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

[System.AttributeUsage(System.AttributeTargets.Struct)]
public sealed class SpikeStateAttribute : System.Attribute { }

namespace SpikeA
{
    [SpikeState]
    public partial struct SpikeHealth
    {
        public float Value;
    }

    public static class SpikeProbe
    {
        public static int Managed;
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct AddChunkJob : IJobChunk
    {
        public ComponentTypeHandle<SpikeHealth> Health;
        public float Amount;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var values = chunk.GetNativeArray(ref Health);
            for (var i = 0; i < chunk.Count; i++)
            {
                var h = values[i];
                h.Value += Amount;
                values[i] = h;
            }
            Mark();
        }

        [BurstDiscard]
        static void Mark() => SpikeProbe.Managed++;
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct SpikeChunkSystem : ISystem
    {
        EntityQuery _query;
        ComponentTypeHandle<SpikeHealth> _health;

        public void OnCreate(ref SystemState state)
        {
            _health = state.GetComponentTypeHandle<SpikeHealth>();
            _query = new EntityQueryBuilder(Allocator.Temp).WithAllRW<SpikeHealth>().Build(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _health.Update(ref state);
            state.Dependency = new AddChunkJob { Health = _health, Amount = 1f }.Schedule(_query, state.Dependency);
        }
    }
}
