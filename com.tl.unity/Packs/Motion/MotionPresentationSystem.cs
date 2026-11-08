using Tl.Pack.Motion;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
#if TL_URP
using Unity.Rendering;
#endif
using Unity.Transforms;

namespace TlPacks.Motion
{
    [BurstCompile]
    [UpdateInGroup(typeof(global::Tl.TlSimulationGroup), OrderLast = true)]
    public partial struct MotionPresentationSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency = new PoseToTransform().ScheduleParallel(state.Dependency);
            state.Dependency = new PoseToScale().ScheduleParallel(state.Dependency);
#if TL_URP
            state.Dependency = new TintToColor().ScheduleParallel(state.Dependency);
#endif
        }
    }

    [BurstCompile]
    public partial struct PoseToTransform : IJobEntity
    {
        void Execute(ref LocalTransform transform, in Pose pose)
        {
            if ((pose.Has & 1) != 0)
                transform.Position = new float3(pose.Position.x, pose.Position.y, pose.Position.z);
            if ((pose.Has & 4) != 0)
                transform.Rotation = quaternion.RotateY(math.radians(pose.Yaw));
        }
    }

    [BurstCompile]
    public partial struct PoseToScale : IJobEntity
    {
        void Execute(ref PostTransformMatrix scale, in Pose pose)
        {
            if ((pose.Has & 2) != 0)
                scale.Value = float4x4.Scale(pose.Scale.x, pose.Scale.y, pose.Scale.z);
        }
    }

#if TL_URP
    [BurstCompile]
    public partial struct TintToColor : IJobEntity
    {
        void Execute(ref URPMaterialPropertyBaseColor color, in Tint tint)
        {
            if (tint.Has != 0)
                color.Value = new float4(tint.Color.R, tint.Color.G, tint.Color.B, tint.Color.A);
        }
    }
#endif
}
