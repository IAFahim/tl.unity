using System.Globalization;
using Tl;
using Unity.Entities;
using UnityEngine;

namespace ManyAtOnce
{
    [UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
    public partial struct PlatformLogSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (platform, playbacks) in SystemAPI.Query<RefRO<Platform>, DynamicBuffer<Tl.Unity.Playback>>())
            {
                var playing = 0;
                var rideTick = -1;
                foreach (var playback in playbacks)
                    if (playback.Timeline != 0)
                    {
                        playing++;
                        if (rideTick < 0)
                            rideTick = playback.Tick;
                    }
                if (rideTick >= 0 && rideTick % 15 == 0)
                    Debug.Log("Many at once: tick " + rideTick + ", platform height " + platform.ValueRO.Height.ToString(CultureInfo.InvariantCulture) + ", playbacks " + playing);
            }
        }
    }
}
