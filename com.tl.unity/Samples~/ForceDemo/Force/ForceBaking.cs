using Tl;

public struct ForceBaking : ITlBaker<ForceClip>
{
    public void Bake(ref TlBaker baker, in ForceClip clip)
    {
        baker.AddComponent<Force>();
        baker.AddBuffer<ForceLog>();
        baker.AddBlob(new ForceFalloff { Curve = baker.CreateCurve(clip.Falloff) });
    }
}
