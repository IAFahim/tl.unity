using Unity.Entities;
using UnityEngine;

public sealed class FollowCameraAuthoring : MonoBehaviour
{
    public Vector3 Offset = new Vector3(0f, 3f, -7f);
    public float Sharpness = 4f;
}

public sealed class FollowCameraBaker : Baker<FollowCameraAuthoring>
{
    public override void Bake(FollowCameraAuthoring authoring)
    {
        AddComponent(GetEntity(TransformUsageFlags.Dynamic), new FollowCamera { Offset = authoring.Offset, Sharpness = authoring.Sharpness });
    }
}
