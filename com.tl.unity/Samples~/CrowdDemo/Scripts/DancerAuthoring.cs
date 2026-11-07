using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

public sealed class DancerAuthoring : MonoBehaviour
{
    public Color Color = new Color(0.25f, 0.45f, 0.95f);
}

public sealed class DancerBaker : Baker<DancerAuthoring>
{
    public override void Bake(DancerAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);
        AddComponent<Dancer>(entity);
        AddComponent(entity, new URPMaterialPropertyBaseColor { Value = new float4(authoring.Color.r, authoring.Color.g, authoring.Color.b, 1f) });
    }
}
