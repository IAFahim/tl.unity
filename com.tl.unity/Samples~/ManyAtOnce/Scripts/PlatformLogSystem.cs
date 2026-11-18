using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Entities;
using UnityEngine;

namespace ManyAtOnce
{
    [BurstCompile]
    [UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
    public partial struct PlatformLogSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (platform, timelinesOfTheDirector, playbacks) in SystemAPI.Query<RefRO<Platform>, DynamicBuffer<TlTimelineSet>, DynamicBuffer<Tl.Unity.Playback>>())
            {
                var ride = timelinesOfTheDirector[0].Id;
                var rideTick = -1;
                var playing = 0;
                foreach (var playback in playbacks)
                {
                    if (playback.Timeline == 0)
                        continue;
                    playing++;
                    if (playback.Timeline == ride)
                        rideTick = playback.Tick;
                }
                if (rideTick >= 0 && rideTick % 15 == 0)
                    Debug.Log($"Many at once: Ride tick {rideTick}, platform height {platform.ValueRO.Height}, playbacks {playing}");
            }
        }
    }
}
