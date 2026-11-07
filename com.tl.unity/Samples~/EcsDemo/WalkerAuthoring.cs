using Unity.Entities;
using UnityEngine;

namespace Demo
{
    public sealed class WalkerAuthoring : MonoBehaviour
    {
        public float WalkStat = 1f;
        public int Health = 100;
    }

    public sealed class WalkerBaker : Baker<WalkerAuthoring>
    {
        public override void Bake(WalkerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent<Movement_MotionData>(entity);
            AddComponent(entity, new Movement_WalkStatData { Value = new WalkStat { Value = authoring.WalkStat } });
            AddComponent(entity, new Vitals_HealthData { Value = new Health { Value = authoring.Health } });
            AddComponent<Vitals_CuesData>(entity);
        }
    }
}
