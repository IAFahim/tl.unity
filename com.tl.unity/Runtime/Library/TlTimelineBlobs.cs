using System;
using System.Security.Cryptography;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Tl.Unity
{
    public static unsafe class TlTimelineBlobs
    {
        public static BlobAssetReference<TlTimelineBlob> Create(Hash128 asset, uint tickRate, string name, byte[] tlb, byte[] kinds, Hash128[] nextAssets)
        {
            return Create(asset, tickRate, name, tlb, kinds, nextAssets, null);
        }

        public static BlobAssetReference<TlTimelineBlob> Create(Hash128 asset, uint tickRate, string name, byte[] tlb, byte[] kinds, Hash128[] nextAssets, uint[] refSites)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("A timeline blob needs a name.", nameof(name));
            if (tlb == null || tlb.Length == 0)
                throw new ArgumentException("Timeline '" + name + "' has no TLB bytes.", nameof(tlb));
            if (tickRate == 0)
                throw new ArgumentException("Timeline '" + name + "' has tick rate 0.", nameof(tickRate));
            kinds ??= Array.Empty<byte>();
            nextAssets ??= Array.Empty<Hash128>();
            refSites ??= Array.Empty<uint>();
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var builder = new BlobBuilder(Allocator.Temp);
            try
            {
                ref var root = ref builder.ConstructRoot<TlTimelineBlob>();
                root.Asset = asset;
                root.TickRate = tickRate;
                Copy(ref builder, ref root.Name, nameBytes);
                Copy(ref builder, ref root.Tlb, tlb);
                Copy(ref builder, ref root.Kinds, kinds);
                var next = builder.Allocate(ref root.NextAssets, nextAssets.Length);
                for (var i = 0; i < nextAssets.Length; i++)
                    next[i] = nextAssets[i];
                var sites = builder.Allocate(ref root.RefSites, refSites.Length);
                for (var i = 0; i < refSites.Length; i++)
                    sites[i] = refSites[i];
                root.Content = ContentOf(tickRate, nameBytes, tlb, kinds, nextAssets, refSites);
                return builder.CreateBlobAssetReference<TlTimelineBlob>(Allocator.Persistent);
            }
            finally
            {
                builder.Dispose();
            }
        }

        public static string NameOf(ref TlTimelineBlob blob)
        {
            return Encoding.UTF8.GetString((byte*)blob.Name.GetUnsafePtr(), blob.Name.Length);
        }

        public static byte[] ToArray(ref BlobArray<byte> bytes)
        {
            var result = new byte[bytes.Length];
            if (result.Length > 0)
                fixed (byte* to = result)
                    UnsafeUtility.MemCpy(to, bytes.GetUnsafePtr(), result.Length);
            return result;
        }

        static void Copy(ref BlobBuilder builder, ref BlobArray<byte> target, byte[] source)
        {
            var array = builder.Allocate(ref target, source.Length);
            if (source.Length > 0)
                fixed (byte* from = source)
                    UnsafeUtility.MemCpy(array.GetUnsafePtr(), from, source.Length);
        }

        static Hash128 ContentOf(uint tickRate, byte[] name, byte[] tlb, byte[] kinds, Hash128[] next, uint[] sites)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(BitConverter.GetBytes(tickRate));
            Feed(sha, name);
            Feed(sha, tlb);
            Feed(sha, kinds);
            foreach (var asset in next)
                Feed(sha, Encoding.ASCII.GetBytes(asset.ToString()));
            foreach (var site in sites)
                sha.AppendData(BitConverter.GetBytes(site));
            var digest = sha.GetHashAndReset();
            return new Hash128(BitConverter.ToUInt32(digest, 0), BitConverter.ToUInt32(digest, 4), BitConverter.ToUInt32(digest, 8), BitConverter.ToUInt32(digest, 12));
        }

        static void Feed(IncrementalHash sha, byte[] bytes)
        {
            sha.AppendData(BitConverter.GetBytes(bytes.Length));
            sha.AppendData(bytes);
        }
    }
}
