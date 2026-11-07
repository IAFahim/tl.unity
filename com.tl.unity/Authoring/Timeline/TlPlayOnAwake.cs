using Unity.Entities;

namespace Tl.Unity
{
    public struct TlPlayOnAwake : IComponentData, IEnableableComponent
    {
        public int Slot;
        public ushort At;
    }
}
