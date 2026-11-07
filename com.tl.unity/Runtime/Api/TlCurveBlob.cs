using Tl;
using Unity.Collections;
using Unity.Entities;

public struct TlCurveBlob
{
    public BlobArray<float> Samples;

    public float Evaluate(float progress)
    {
        var last = Samples.Length - 1;
        if (last < 0)
            return 0f;
        var x = (progress < 0f ? 0f : progress > 1f ? 1f : progress) * last;
        var i = (int)x;
        var j = i < last ? i + 1 : i;
        return Samples[i] + (Samples[j] - Samples[i]) * (x - i);
    }

    public static BlobAssetReference<TlCurveBlob> Create(in TlCurve curve)
    {
        var builder = new BlobBuilder(Allocator.Temp);
        ref var root = ref builder.ConstructRoot<TlCurveBlob>();
        var samples = builder.Allocate(ref root.Samples, TlCurve.Samples);
        for (var i = 0; i < TlCurve.Samples; i++)
            samples[i] = curve[i];
        var blob = builder.CreateBlobAssetReference<TlCurveBlob>(Allocator.Persistent);
        builder.Dispose();
        return blob;
    }
}
