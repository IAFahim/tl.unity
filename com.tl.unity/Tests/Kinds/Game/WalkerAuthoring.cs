using Unity.Entities;
using UnityEngine;

namespace KindGame
{
    public sealed class WalkerAuthoring : MonoBehaviour
    {
    }

    public sealed class WalkerBaker : Baker<WalkerAuthoring>
    {
        public override void Bake(WalkerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent<Movement_MotionData>(entity);
            AddComponent(entity, new Movement_WalkStatData { Value = new WalkStat { Value = 1f } });
            AddComponent<Movement_TargetData>(entity);
        }
    }
}
