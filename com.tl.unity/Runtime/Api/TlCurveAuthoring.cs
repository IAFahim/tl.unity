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

        public static AnimationCurve Curve(TlCurve sampled)
        {
            var keys = new Keyframe[TlCurve.Samples];
            var step = 1f / (TlCurve.Samples - 1);
            for (var i = 0; i < keys.Length; i++)
            {
                var before = i == 0 ? 0f : (sampled[i] - sampled[i - 1]) / step;
                var after = i == keys.Length - 1 ? 0f : (sampled[i + 1] - sampled[i]) / step;
                keys[i] = new Keyframe(i / (float)(TlCurve.Samples - 1), sampled[i], i == 0 ? after : before, i == keys.Length - 1 ? before : after);
            }
            return new AnimationCurve(keys);
        }
    }
}
