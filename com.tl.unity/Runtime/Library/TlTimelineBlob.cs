using Unity.Entities;

namespace Tl.Unity
{
    public struct TlTimelineBlob
    {
        public Hash128 Asset;
        public Hash128 Content;
        public uint TickRate;
        public BlobArray<byte> Name;
        public BlobArray<byte> Tlb;
        public BlobArray<byte> Kinds;
        public BlobArray<Hash128> NextAssets;
        public BlobArray<uint> RefSites;
    }
}
