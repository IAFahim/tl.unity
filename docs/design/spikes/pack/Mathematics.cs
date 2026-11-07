#pragma warning disable CS8981
namespace Tl.Mathematics
{
    [System.Serializable]
    public struct float3 : System.IEquatable<float3>
    {
        public float x;
        public float y;
        public float z;

        public float3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static float3 operator +(float3 a, float3 b) => new float3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static float3 operator -(float3 a, float3 b) => new float3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static float3 operator *(float3 a, float3 b) => new float3(a.x * b.x, a.y * b.y, a.z * b.z);
        public static float3 operator *(float3 a, float b) => new float3(a.x * b, a.y * b, a.z * b);
        public static float3 operator *(float a, float3 b) => new float3(a * b.x, a * b.y, a * b.z);
        public bool Equals(float3 other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object? obj) => obj is float3 other && Equals(other);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() * 397) ^ (z.GetHashCode() * 7919);
    }
}
