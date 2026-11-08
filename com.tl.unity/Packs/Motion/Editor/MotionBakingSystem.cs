using Tl.Pack.Motion;
using Tl.Unity.Editor;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
#if TL_URP
using Unity.Rendering;
#endif
using Unity.Transforms;

namespace TlPacks.Motion.Editor
{
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [UpdateAfter(typeof(TimelineKindBakingSystem))]
    public partial struct MotionBakingSystem : ISystem
    {
        EntityQuery _scaled;
#if TL_URP
        EntityQuery _tinted;
#endif

        public void OnCreate(ref SystemState state)
        {
            _scaled = new EntityQueryBuilder(Allocator.Temp).WithAll<Pose>().WithAbsent<PostTransformMatrix>()
                .WithOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities).Build(ref state);
#if TL_URP
            _tinted = new EntityQueryBuilder(Allocator.Temp).WithAll<Tint>().WithAbsent<URPMaterialPropertyBaseColor>()
                .WithOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities).Build(ref state);
#endif
        }

        public void OnUpdate(ref SystemState state)
        {
            var manager = state.EntityManager;
            if (!_scaled.IsEmpty)
            {
                using var entities = _scaled.ToEntityArray(Allocator.Temp);
                manager.AddComponent<PostTransformMatrix>(entities);
                foreach (var entity in entities)
                    manager.SetComponentData(entity, new PostTransformMatrix { Value = float4x4.identity });
            }
#if TL_URP
            if (!_tinted.IsEmpty)
            {
                using var entities = _tinted.ToEntityArray(Allocator.Temp);
                manager.AddComponent<URPMaterialPropertyBaseColor>(entities);
                foreach (var entity in entities)
                    manager.SetComponentData(entity, new URPMaterialPropertyBaseColor { Value = new float4(1f, 1f, 1f, 1f) });
            }
#endif
        }
    }
}
