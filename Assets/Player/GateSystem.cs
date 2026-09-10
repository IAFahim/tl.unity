using Tl;
using Unity.Burst;
using Unity.Entities;
using UnityEngine;

namespace TlUnity.PlayerProbe
{
    public struct GatePlayback : IComponentData
    {
        public Playback Value;
        public uint Tick;
    }

    public struct CurrentPose : IComponentData
    {
        public Pose Value;
    }

    public struct NextPose : IComponentData
    {
        public Pose Value;
    }

    public struct TargetHealth : IComponentData
    {
        public Health Value;
    }

    [BurstCompile(CompileSynchronously = true)]
    public partial struct AdvanceGateJob : IJobEntity
    {
        private void Execute(ref GatePlayback state, in CurrentPose currentPose, ref NextPose nextPose, ref TargetHealth health)
        {
            var playback = state.Value.Has(PlaybackFlags.Started) ? state.Value : Timeline.Start(Gate.Id);
            var input = new Gate.Input(in currentPose.Value);
            var output = new Gate.Output(ref nextPose.Value, ref health.Value);
            Playback next;
            if (!Timeline.TryForward(Gate.Id, in playback, state.Tick, in input, ref output, out next))
                return;
            state.Value = next;
            state.Tick++;
        }
    }

    [BurstCompile]
    public partial struct AdvanceGateSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var handle = new AdvanceGateJob().Schedule(state.Dependency);
            handle.Complete();
        }
    }

    public sealed class GatePlayer : MonoBehaviour
    {
        private void Start()
        {
            using (var world = new World("Tl player gate"))
            {
                var manager = world.EntityManager;
                var entity = manager.CreateEntity(typeof(GatePlayback), typeof(CurrentPose), typeof(NextPose), typeof(TargetHealth));
                manager.SetComponentData(entity, new GatePlayback { Tick = 10 });
                manager.SetComponentData(entity, new CurrentPose { Value = new Pose { X = 3, Y = 4 } });
                manager.SetComponentData(entity, new TargetHealth { Value = new Health { Value = 100 } });
                world.CreateSystem<AdvanceGateSystem>().Update(world.Unmanaged);
                var state = manager.GetComponentData<GatePlayback>(entity);
                var pose = manager.GetComponentData<NextPose>(entity);
                var health = manager.GetComponentData<TargetHealth>(entity);
                var valid = state.Value.Tick == 10 && state.Tick == 11 && pose.Value.X == 5 && pose.Value.Y == 5 && health.Value.Value == 90;
                Debug.Log(valid ? "TL_UNITY_PLAYER_OK" : "TL_UNITY_PLAYER_FAILED");
                Application.Quit(valid ? 0 : 1);
            }
        }
    }
}
