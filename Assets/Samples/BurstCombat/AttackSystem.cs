using Tl;
using Unity.Burst;
using Unity.Entities;

namespace Tl.Samples.BurstCombat
{
    public struct AttackPlayback : IComponentData
    {
        public Playback<Attack> Value;
        public uint StartGameTick;
        public int Delta;
    }

    public struct CurrentPose : IComponentData
    {
        public FighterPose Value;
    }

    public struct NextPose : IComponentData
    {
        public FighterPose Value;
    }

    public struct FighterCombat : IComponentData
    {
        public CombatStats Value;
    }

    [BurstCompile]
    public partial struct AdvanceAttackJob : IJobEntity
    {
        private void Execute(ref AttackPlayback state, in CurrentPose currentPose, ref NextPose nextPose, ref FighterCombat combat)
        {
            if (!state.Value.Has(PlaybackFlags.Started))
                state.Value = Attack.Start(state.StartGameTick);
            var data = new Attack.Data(ref state.Value, in currentPose.Value, ref nextPose.Value, ref combat.Value);
            Attack.TrySeek(ref data, state.Delta);
        }
    }

    [BurstCompile]
    public partial struct AdvanceAttackSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency = new AdvanceAttackJob().Schedule(state.Dependency);
        }
    }
}
