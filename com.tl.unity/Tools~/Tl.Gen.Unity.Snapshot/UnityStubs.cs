namespace Unity.Entities
{
    public struct Entity
    {
        public int Index;
        public int Version;
    }

    public interface IComponentData
    {
    }

    public struct ComponentLookup<T> where T : unmanaged, IComponentData
    {
        public bool TryGetComponent(Entity entity, out T component)
        {
            component = default;
            return false;
        }
    }
}

namespace Unity.Collections
{
    [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Parameter)]
    public sealed class ReadOnlyAttribute : System.Attribute
    {
    }
}
