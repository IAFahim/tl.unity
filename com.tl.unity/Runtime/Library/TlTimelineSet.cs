using Unity.Entities;

namespace Tl.Unity
{
    [InternalBufferCapacity(1)]
    public struct TlTimelineSet : IBufferElementData
    {
        public BlobAssetReference<TlTimelineBlob> Blob;
        public ushort Id;
    }

    public struct TlUnregistered : IComponentData, IEnableableComponent
    {
    }
}
