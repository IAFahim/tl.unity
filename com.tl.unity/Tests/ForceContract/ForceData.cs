using Unity.Entities;
using Unity.Mathematics;

public struct Force : IComponentData
{
    public float3 Value;
}

public struct ForceLog : IBufferElementData
{
    public float3 Value;
    public int Tick;
}

public struct ForceFalloff : IComponentData
{
    public BlobAssetReference<TlCurveBlob> Curve;
}
