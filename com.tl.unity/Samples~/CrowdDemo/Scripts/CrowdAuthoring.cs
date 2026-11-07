using Tl;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Timeline;

public sealed class CrowdAuthoring : MonoBehaviour
{
    public GameObject Prefab;
    public int Count = 10000;
    public float Spacing = 1.2f;
    public TimelineAsset Dance;
}

public sealed class CrowdBaker : Baker<CrowdAuthoring>
{
    public override void Bake(CrowdAuthoring authoring)
    {
        if (authoring.Prefab == null || authoring.Dance == null)
            return;
        AddComponent(GetEntity(TransformUsageFlags.None), new Crowd
        {
            Prefab = GetEntity(authoring.Prefab, TransformUsageFlags.Dynamic),
            Count = authoring.Count,
            Spacing = authoring.Spacing,
            Dance = TlBaking.GetTimeline(this, authoring.Dance),
            Ticks = TlBaking.Ticks(authoring.Dance.duration)
        });
    }
}
