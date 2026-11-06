using Tl.Unity;
using Unity.Entities;
using UnityEngine;

namespace KindGame
{
    public sealed class RunnerAuthoring : MonoBehaviour
    {
        public float WalkStat = 1f;
        public float JumpPower = 2f;
        public int Health = 1000;
    }

    public sealed class RunnerBaker : Baker<RunnerAuthoring>
    {
        public override void Bake(RunnerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent<Movement_MotionData>(entity);
            AddComponent(entity, new Movement_WalkStatData { Value = new WalkStat { Value = authoring.WalkStat } });
            AddComponent(entity, new Movement_JumpPowerData { Value = new JumpPower { Value = authoring.JumpPower } });
            AddComponent<Movement_TargetData>(entity);
            AddComponent<Movement_StaminaData>(entity);
            AddComponent(entity, new Vitals_HealthData { Value = new Health { Value = authoring.Health } });
        }
    }
}
