using Unity.Entities;
using UnityEngine;

namespace Tl.Samples.BurstCombat
{
    public sealed class AttackAuthoring : MonoBehaviour
    {
        public float PoseX;
        public float PoseY;
        public float Health = 100;

        private sealed class AttackBaker : Baker<AttackAuthoring>
        {
            public override void Bake(AttackAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new AttackPlayback());
                AddComponent(entity, new CurrentPose { Value = new FighterPose { X = authoring.PoseX, Y = authoring.PoseY } });
                AddComponent(entity, new NextPose());
                AddComponent(entity, new FighterCombat { Value = new CombatStats { Health = authoring.Health } });
            }
        }
    }
}
