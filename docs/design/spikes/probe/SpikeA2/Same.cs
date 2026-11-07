using Unity.Burst;
using Unity.Entities;

namespace SpikeA2
{
    [SpikeState]
    public partial struct SpikeMana
    {
        public float Value;
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct ManaSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var mana in SystemAPI.Query<RefRW<SpikeMana>>())
                mana.ValueRW.Value += 1f;
        }
    }
}
