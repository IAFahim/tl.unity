namespace Tl
{
    public unsafe struct TlCurve
    {
        public const int Samples = 16;

        fixed float _values[Samples];

        public float this[int index]
        {
            get => _values[index];
            set => _values[index] = value;
        }

        public float Evaluate(float progress)
        {
            var x = (progress < 0f ? 0f : progress > 1f ? 1f : progress) * (Samples - 1);
            var i = (int)x;
            var j = i < Samples - 1 ? i + 1 : i;
            return _values[i] + (_values[j] - _values[i]) * (x - i);
        }

        public static TlCurve Constant(float value)
        {
            var curve = default(TlCurve);
            for (var i = 0; i < Samples; i++)
                curve._values[i] = value;
            return curve;
        }
    }
}
