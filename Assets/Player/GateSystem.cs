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
                    typeof(GeneratedJobs.Combat.Role0Bias),
                    typeof(GeneratedJobs.Combat.Role1Scale),
                    typeof(GeneratedJobs.Combat.Role2Secondary),
                    typeof(GeneratedJobs.Combat.Role3Trace));
                manager.SetComponentData(entity, new GeneratedJobs.Combat.TimelineComponent
                {
                    Value = new GeneratedJobs.Combat.State(GeneratedJobs.Combat.Asset.Attack)
                });
                manager.SetComponentData(entity, new GeneratedJobs.Combat.Role0Bias(new GeneratedJobs.Bias { Value = 2 }));
                manager.SetComponentData(entity, new GeneratedJobs.Combat.Role1Scale(new GeneratedJobs.Scale { Value = 1 }));
                manager.SetComponentData(entity, new GeneratedJobs.Combat.Role2Secondary(new GeneratedJobs.Bias { Value = 7 }));
                manager.SetComponentData(entity, new GeneratedJobs.Combat.Role3Trace(new GeneratedJobs.Trace()));
                world.CreateSystem<GeneratedJobs.GeneratedTimelineSystem>().Update(world.Unmanaged);
                manager.CompleteAllTrackedJobs();
                var timeline = manager.GetComponentData<GeneratedJobs.Combat.TimelineComponent>(entity).Value;
                var trace = manager.GetComponentData<GeneratedJobs.Combat.Role3Trace>(entity).Value;
                var valid = timeline.Position == 2u
                    && trace.Order == 8395883958L
                    && trace.ClipSum == 54L
                    && trace.Calls == 10
                    && trace.LastGameTick == 11u;
                Debug.Log(valid ? "TL_UNITY_PLAYER_OK" : "TL_UNITY_PLAYER_FAILED");
                Application.Quit(valid ? 0 : 1);
            }
        }
    }
}
