using Unity.Entities;
using UnityEngine;

public sealed class FollowTargetAuthoring : MonoBehaviour
{
    public bool ShowForce = true;
}

public sealed class FollowTargetBaker : Baker<FollowTargetAuthoring>
{
    public override void Bake(FollowTargetAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.Dynamic);
        AddComponent<FollowTarget>(entity);
        if (authoring.ShowForce)
            AddComponent<ForceShown>(entity);
    }
}
