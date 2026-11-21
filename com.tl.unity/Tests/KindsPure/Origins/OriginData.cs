using Unity.Entities;

namespace KindPure.Origins
{
    public struct Motion : IComponentData
    {
        public float X;
        public float Y;
    }

    public struct Vitals : IComponentData
    {
        public int Health;
        public int Stamina;
    }

    public struct Stats : IComponentData
    {
        public float Walk;
        public float Jump;
    }

    [InternalBufferCapacity(0)]
    public struct HeapOrigin : IBufferElementData
    {
        public ushort Origin;
    }

    [InternalBufferCapacity(2)]
    public struct Inline2Origin : IBufferElementData
    {
        public ushort Origin;
    }

    [InternalBufferCapacity(4)]
    public struct Inline4Origin : IBufferElementData
    {
        public ushort Origin;
    }

    [InternalBufferCapacity(8)]
    public struct Inline8Origin : IBufferElementData
    {
        public ushort Origin;
    }

    [InternalBufferCapacity(100)]
    public struct Inline100Origin : IBufferElementData
    {
        public ushort Origin;
    }

    public unsafe struct Fixed2Origins : IComponentData
    {
        public fixed ushort Origin[2];
    }

    public unsafe struct Fixed4Origins : IComponentData
    {
        public fixed ushort Origin[4];
    }

    public enum OriginLayout
    {
        Heap,
        Inline2,
        Inline4,
        Inline8,
        Inline100,
        Fixed2,
        Fixed4
    }

    public enum OriginJobShape
    {
        Chunk,
        Entity,
        Floor
    }
}
