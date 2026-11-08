using Tl.Pack.Stats;
using Unity.Entities;
using UnityEngine;

namespace TlPacks.Stats
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Tl/Stats")]
    public sealed class TlStatsAuthoring : MonoBehaviour
    {
        [Tooltip("Starting health. Stats clips (Hit, Heal, Poison) change it while the timeline plays.")]
        public int Health = 100;
        [Tooltip("Largest health, for read-outs.")]
        public int MaxHealth = 100;
        [Tooltip("Attack without buffs. A Buff clip adds its bonus while it plays.")]
        public int Attack = 10;
    }

    public sealed class TlStatsBaker : Baker<TlStatsAuthoring>
    {
        public override void Bake(TlStatsAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new Health { Value = authoring.Health, Max = authoring.MaxHealth });
            AddComponent(entity, new Attack { Base = authoring.Attack });
        }
    }
}
