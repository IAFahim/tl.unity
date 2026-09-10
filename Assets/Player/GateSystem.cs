using GeneratedJobs = Tl.Samples.GeneratedJobs;
using Unity.Entities;
using UnityEngine;

namespace TlUnity.PlayerProbe
{
    public sealed class GatePlayer : MonoBehaviour
    {
        private void Start()
        {
            using (var world = new World("Tl player gate"))
            {
                var manager = world.EntityManager;
                var clock = manager.CreateEntity(typeof(GeneratedJobs.Clock));
                manager.SetComponentData(clock, new GeneratedJobs.Clock { GameTick = 10u, Delta = 2 });
                var entity = manager.CreateEntity(
                    typeof(GeneratedJobs.Combat.TimelineComponent),
                    typeof(GeneratedJobs.Combat.Rows),
                    typeof(GeneratedJobs.Combat.Stage0),
                    typeof(GeneratedJobs.Bias),
                    typeof(GeneratedJobs.Total));
                manager.SetComponentData(entity, new GeneratedJobs.Combat.TimelineComponent
                {
                    Value = new GeneratedJobs.Combat.State(GeneratedJobs.Combat.Asset.Attack)
                });
                manager.SetComponentData(entity, new GeneratedJobs.Bias { Value = 1 });
                manager.SetComponentEnabled<GeneratedJobs.Combat.Stage0>(entity, false);
                world.CreateSystem<GeneratedJobs.GeneratedTimelineSystem>().Update(world.Unmanaged);
                manager.CompleteAllTrackedJobs();
                var timeline = manager.GetComponentData<GeneratedJobs.Combat.TimelineComponent>(entity).Value;
                var total = manager.GetComponentData<GeneratedJobs.Total>(entity);
                var valid = timeline.Position == 2u
                    && total.Value == 12
                    && total.Calls == 2
                    && total.LastGameTick == 11u;
                Debug.Log(valid ? "TL_UNITY_PLAYER_OK" : "TL_UNITY_PLAYER_FAILED");
                Application.Quit(valid ? 0 : 1);
            }
        }
    }
}
