using System;
using System.Collections.Generic;
using System.IO;
using Tl.Gen.Tlb;
using Tl.Unity.Bake.Tests;
using Unity.Entities;

namespace Tl.Unity.Library.Tests
{
    sealed class KindGameBlobs : IDisposable
    {
        public readonly List<string> Names;
        public readonly List<Hash128> Assets = new List<Hash128>();
        public readonly List<BlobAssetReference<TlTimelineBlob>> Blobs = new List<BlobAssetReference<TlTimelineBlob>>();
        public readonly byte[] GameTlib;
        public readonly uint TickRate;

        public KindGameBlobs(uint tickRate = 0)
        {
            var fixture = KindGameFixture.Load();
            Names = fixture.Names;
            TickRate = tickRate == 0 ? fixture.TickRate : tickRate;
            GameTlib = File.ReadAllBytes(Path.Combine(KindGameFixture.Folder, "game.tlib"));
            var overrides = new Dictionary<string, global::Tl.KindOverride> { ["Damage"] = new global::Tl.KindOverride(11, 8) };
            var catalog = LibraryBake.Kinds(new[] { KindGameFixture.Game }, overrides);
            for (var i = 0; i < Names.Count; i++)
                Assets.Add(AssetOf(Names[i]));
            for (var i = 0; i < Names.Count; i++)
            {
                var timeline = fixture.Timelines[i];
                var next = timeline.Next == null ? Array.Empty<string>() : new[] { timeline.Next };
                var manifest = LibraryBake.Manifest(next, fixture.Channels, TickRate);
                var tlb = LibraryBake.BakeTyped(timeline, manifest);
                var nextAssets = timeline.Next == null ? Array.Empty<Hash128>() : new[] { AssetOf(timeline.Next) };
                Blobs.Add(TlTimelineBlobs.Create(Assets[i], TickRate, Names[i], tlb, LibraryBake.KindPayload(Names[i], tlb, catalog), nextAssets));
            }
        }

        public static Hash128 AssetOf(string name)
        {
            var hash = 2166136261u;
            foreach (var c in name)
                hash = (hash ^ c) * 16777619u;
            return new Hash128(hash, ~hash, 0x7100u, (uint)name.Length);
        }

        public void Dispose()
        {
            foreach (var blob in Blobs)
                if (blob.IsCreated)
                    blob.Dispose();
            Blobs.Clear();
        }
    }
}
