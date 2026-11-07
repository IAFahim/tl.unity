using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

public sealed class CameraFromEntity : MonoBehaviour
{
    World _world;
    EntityQuery _rigs;

    void LateUpdate()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated)
            return;
        if (_world != world)
        {
            _world = world;
            _rigs = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<FollowCamera>(), ComponentType.ReadOnly<LocalToWorld>());
        }
        if (_rigs.CalculateEntityCount() != 1)
            return;
        _rigs.CompleteDependency();
        var rig = _rigs.GetSingleton<LocalToWorld>();
        transform.SetPositionAndRotation(rig.Position, rig.Rotation);
    }
}
