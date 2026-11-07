using Unity.Entities;
using UnityEngine;

public sealed class ArenaDummyAuthoring : MonoBehaviour
{
    public int Health = 100;
}

public sealed class ArenaDummyBaker : Baker<ArenaDummyAuthoring>
{
    public override void Bake(ArenaDummyAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic | TransformUsageFlags.NonUniformScale);
        AddComponent(entity, new DummyHealth { Max = authoring.Health });
        foreach (var type in TlUnityGenerated.ArenaDamageKind.Types)
            AddComponent(entity, type);
        SetComponentEnabled<TlUnityGenerated.ArenaDamageActive>(entity, false);
    }
}
