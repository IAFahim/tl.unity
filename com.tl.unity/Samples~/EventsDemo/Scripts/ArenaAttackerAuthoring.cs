using Tl;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Timeline;

public sealed class ArenaAttackerAuthoring : MonoBehaviour
{
    public GameObject Target;
    public GameObject Spark;
    public TimelineAsset Hurt;
}

public sealed class ArenaAttackerBaker : Baker<ArenaAttackerAuthoring>
{
    public override void Bake(ArenaAttackerAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);
        AddComponent(entity, new Attacker
        {
            Target = GetEntity(authoring.Target, TransformUsageFlags.Dynamic),
            Spark = GetEntity(authoring.Spark, TransformUsageFlags.Dynamic),
            Hurt = TlBaking.GetTimeline(this, authoring.Hurt)
        });
        AddComponent(entity, new Score());
        AddComponent(entity, new Home { Value = authoring.transform.position });
    }
}
