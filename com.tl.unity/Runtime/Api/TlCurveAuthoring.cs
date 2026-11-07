using UnityEngine;

namespace Tl.Unity
{
    public static class TlCurveAuthoring
    {
        public static AnimationCurve Default()
        {
            return AnimationCurve.Linear(0f, 1f, 1f, 0f);
        }

        public static TlCurve Sample(AnimationCurve curve)
        {
            var sampled = default(TlCurve);
            for (var i = 0; i < TlCurve.Samples; i++)
                sampled[i] = curve == null ? 1f : curve.Evaluate(i / (float)(TlCurve.Samples - 1));
            return sampled;
        }
    }
}
