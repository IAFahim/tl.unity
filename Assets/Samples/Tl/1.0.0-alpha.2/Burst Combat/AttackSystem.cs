using Tl;
using Unity.Burst;
using Unity.Entities;

namespace Tl.Samples.BurstCombat
{
    public struct AttackPlayback : IComponentData
    {
        public Playback Value;
        public uint Tick;
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
            var playback = state.Value.Has(PlaybackFlags.Started)
                ? state.Value
                : Timeline.Start(Attack.Id);
            var input = new Attack.Input(in currentPose.Value);
            var output = new Attack.Output(ref nextPose.Value, ref combat.Value);
            Playback next;
            if (!Timeline.TryForward(Attack.Id, in playback, state.Tick, in input, ref output, out next))
                return;
            state.Value = next;
            state.Tick++;
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
