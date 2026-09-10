using Tl;
using Unity.Burst;
using Unity.Entities;
using UnityEngine;

namespace TlUnity.PlayerProbe
{
    public struct GatePlayback : IComponentData
    {
        public Playback Value;
        public uint StartGameTick;
        public int Delta;
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

    public struct GateReceipt : IComponentData
    {
        public Receipt Value;
    }

    [BurstCompile(CompileSynchronously = true)]
    public partial struct AdvanceGateJob : IJobEntity
    {
        private void Execute(
            ref GatePlayback state,
            in CurrentPose currentPose,
            ref NextPose nextPose,
            ref TargetHealth health,
            ref GateReceipt receipt)
        {
            if (!state.Value.Has(PlaybackFlags.Started))
                state.Value = Gate.Start(state.StartGameTick);
            var data = new Gate.Data(ref state.Value, in currentPose.Value, ref nextPose.Value, ref health.Value, ref receipt.Value);
            Gate.TrySeek(ref data, state.Delta);
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
                var entity = manager.CreateEntity(
                    typeof(GatePlayback),
                    typeof(CurrentPose),
                    typeof(NextPose),
                    typeof(TargetHealth),
                    typeof(GateReceipt));
                manager.SetComponentData(entity, new GatePlayback { StartGameTick = 10, Delta = 2 });
                manager.SetComponentData(entity, new CurrentPose { Value = new Pose { X = 3, Y = 4 } });
                manager.SetComponentData(entity, new TargetHealth { Value = new Health { Value = 100 } });
                world.CreateSystem<AdvanceGateSystem>().Update(world.Unmanaged);
                var state = manager.GetComponentData<GatePlayback>(entity);
                var pose = manager.GetComponentData<NextPose>(entity);
                var health = manager.GetComponentData<TargetHealth>(entity);
                var receipt = manager.GetComponentData<GateReceipt>(entity);
                var valid = state.Value.Position == 2
                    && state.Value.GameTick == 12
                    && pose.Value.X == 5
                    && pose.Value.Y == 5
                    && health.Value.Value == 90
                    && receipt.Value.Calls == 3;
                Debug.Log(valid ? "TL_UNITY_PLAYER_OK" : "TL_UNITY_PLAYER_FAILED");
                Application.Quit(valid ? 0 : 1);
            }
        }
    }
}
