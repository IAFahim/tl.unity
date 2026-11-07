namespace Tl
{
    public static class TlAssetIds
    {
        public static TlAssetId ToAssetId(this global::Unity.Entities.Hash128 hash)
        {
            return new TlAssetId(hash.Value.x, hash.Value.y, hash.Value.z, hash.Value.w);
        }

        public static global::Unity.Entities.Hash128 ToHash128(this TlAssetId id)
        {
            return new global::Unity.Entities.Hash128(id.X, id.Y, id.Z, id.W);
        }
    }
}
