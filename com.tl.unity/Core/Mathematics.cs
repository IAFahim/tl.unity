namespace Tl.Mathematics
{
    [global::System.Serializable]
    public struct float3
    {
        public float x;
        public float y;
        public float z;

        public float3(float value) : this(value, value, value) { }

        public float3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static float3 operator +(float3 a, float3 b) => new float3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static float3 operator -(float3 a, float3 b) => new float3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static float3 operator -(float3 value) => new float3(-value.x, -value.y, -value.z);
        public static float3 operator *(float3 a, float b) => new float3(a.x * b, a.y * b, a.z * b);
        public static float3 operator *(float a, float3 b) => new float3(a * b.x, a * b.y, a * b.z);
        public static float3 operator /(float3 a, float b) => new float3(a.x / b, a.y / b, a.z / b);
    }
}
