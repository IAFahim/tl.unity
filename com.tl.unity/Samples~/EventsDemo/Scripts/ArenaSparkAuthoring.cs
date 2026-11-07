using Unity.Entities;
using UnityEngine;

public sealed class ArenaSparkAuthoring : MonoBehaviour
{
    public float Life = 0.4f;
}

public sealed class ArenaSparkBaker : Baker<ArenaSparkAuthoring>
{
    public override void Bake(ArenaSparkAuthoring authoring)
    {
        AddComponent(GetEntity(TransformUsageFlags.Dynamic), new Spark { Life = authoring.Life });
    }
}
