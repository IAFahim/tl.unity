using System;
using System.Collections.Generic;
using System.Linq;
using KindGame;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity.Library.Tests
{
    public sealed class TlLibraryTests
    {
        [Test]
        public void AssemblesTheKindGameLibraryLikeTlbFromBlobs()
        {
            using var blobs = new KindGameBlobs();
            using var library = new TlLibrary();
            var errors = new List<string>();
            var shuffled = blobs.Blobs.OrderBy(b => b.Value.Asset.Value.x).ToList();
            Assert.IsTrue(library.Add(shuffled, errors), string.Join("\n", errors));
            Assert.IsEmpty(errors);
            Assert.AreEqual(blobs.GameTlib, library.Bytes);
            for (var i = 0; i < blobs.Names.Count; i++)
                Assert.AreEqual(i + 1, library.IdOf(blobs.Assets[i]), blobs.Names[i]);
        }

        [Test]
        public void KeepsIdsAndSkipsWorkForKnownBlobs()
        {
            using var blobs = new KindGameBlobs();
            using var library = new TlLibrary();
            var errors = new List<string>();
            Assert.IsTrue(library.Add(blobs.Blobs.Take(4).ToList(), errors));
            var generation = library.Generation;
            Assert.IsFalse(library.Add(blobs.Blobs.Take(4).ToList(), errors));
            Assert.AreEqual(generation, library.Generation);
            Assert.IsTrue(library.Add(blobs.Blobs, errors));
            Assert.IsEmpty(errors);
            for (var i = 0; i < blobs.Names.Count; i++)
                Assert.AreEqual(i + 1, library.IdOf(blobs.Assets[i]), blobs.Names[i]);
        }

        [Test]
        public void ATimelineAddedLaterKeepsEveryEarlierId()
        {
            using var blobs = new KindGameBlobs();
            using var library = new TlLibrary();
            var errors = new List<string>();
            Assert.IsTrue(library.Add(blobs.Blobs.Skip(1).ToList(), errors));
            var before = blobs.Assets.Skip(1).Select(library.IdOf).ToList();
            Assert.IsTrue(library.Add(blobs.Blobs, errors), string.Join("\n", errors));
            Assert.IsEmpty(errors);
            Assert.AreEqual(before, blobs.Assets.Skip(1).Select(library.IdOf).ToList());
            Assert.AreEqual(blobs.Names.Count, library.IdOf(blobs.Assets[0]));
        }

        [Test]
        public void AChangedTimelineKeepsItsId()
        {
            using var blobs = new KindGameBlobs();
            using var faster = new KindGameBlobs(120);
            using var library = new TlLibrary();
            var errors = new List<string>();
            Assert.IsTrue(library.Add(blobs.Blobs, errors));
            var id = library.IdOf(blobs.Assets[2]);
            var changed = TlTimelineBlobs.Create(blobs.Assets[2], blobs.TickRate, blobs.Names[2], Tlb(blobs, 2), TlTimelineBlobs.ToArray(ref blobs.Blobs[2].Value.Kinds), new[] { new Hash128(1, 2, 3, 4) });
            var relaid = TlTimelineBlobs.Create(blobs.Assets[2], blobs.TickRate, blobs.Names[2], Tlb(blobs, 2), Array.Empty<byte>(), Array.Empty<Hash128>());
            try
            {
                Assert.AreNotEqual(blobs.Blobs[2].Value.Content, changed.Value.Content);
                Assert.IsTrue(library.Add(new[] { changed }, errors), string.Join("\n", errors));
                Assert.AreEqual(id, library.IdOf(blobs.Assets[2]));
                CollectionAssert.AreEqual(new[] { id }, library.Swapped);
                Assert.IsFalse(library.Add(new[] { relaid }, errors), "a version that reaches other kinds is not swapped in");
                Assert.AreEqual(1, errors.Count);
                StringAssert.Contains("adds or removes a type of track", errors[0]);
                errors.Clear();
                Assert.AreEqual(blobs.Names.Count, library.Count);
                Assert.IsFalse(library.Add(new[] { faster.Blobs[0] }, errors));
                Assert.AreEqual(1, errors.Count);
                StringAssert.Contains("120 ticks per second but the library runs at 60", errors[0]);
            }
            finally
            {
                changed.Dispose();
                relaid.Dispose();
            }
        }

        [Test]
        public void AWorldGetsItsLibraryFromTimelineSetsAndHashesLikeTheLegacyRoute()
        {
            using var blobs = new KindGameBlobs();
            using (var game = new KindWorld(KindScript.Rows, false, world => FromBlobs(world, blobs)))
                Assert.AreEqual(KindScript.Pinned, KindScript.Play(game, KindScript.Steps));
            using (var game = new KindWorld(KindScript.Rows, true, world => FromBlobs(world, blobs)))
                Assert.AreEqual(KindScript.EventsPinned, KindScript.PlayEvents(game, KindScript.Steps));
        }

        [Test]
        public void ARegisteredWorldUpdatesWithoutAllocating()
        {
            using var blobs = new KindGameBlobs();
            using var game = new KindWorld(1, false, world => FromBlobs(world, blobs));
            var library = game.World.GetExistingSystem<TlLibrarySystem>();
            var register = game.World.GetExistingSystem<TlTimelineRegisterSystem>();
            for (var i = 0; i < 8; i++)
            {
                library.Update(game.World.Unmanaged);
                register.Update(game.World.Unmanaged);
                game.Step();
            }
            game.Entities.CompleteAllTrackedJobs();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 64; i++)
            {
                library.Update(game.World.Unmanaged);
                register.Update(game.World.Unmanaged);
                game.Step();
            }
            game.Entities.CompleteAllTrackedJobs();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated);
        }

        static SystemHandle FromBlobs(World world, KindGameBlobs blobs)
        {
            var library = world.CreateSystem<TlLibrarySystem>();
            var register = world.CreateSystem<TlTimelineRegisterSystem>();
            var manager = world.EntityManager;
            var holders = new Entity[3];
            for (var h = 0; h < holders.Length; h++)
            {
                holders[h] = manager.CreateEntity(typeof(TlTimelineSet), typeof(TlUnregistered));
                var set = manager.GetBuffer<TlTimelineSet>(holders[h]);
                for (var i = h; i < blobs.Blobs.Count; i += 2)
                    set.Add(new TlTimelineSet { Blob = blobs.Blobs[i] });
            }
            library.Update(world.Unmanaged);
            register.Update(world.Unmanaged);
            using var query = manager.CreateEntityQuery(typeof(TimelineLibrary));
            Assert.AreEqual(1, query.CalculateEntityCount());
            Assert.AreEqual(blobs.Names.Count, query.GetSingleton<TimelineLibrary>().View.TimelineCount);
            foreach (var holder in holders)
            {
                Assert.IsFalse(manager.IsComponentEnabled<TlUnregistered>(holder));
                var set = manager.GetBuffer<TlTimelineSet>(holder);
                for (var i = 0; i < set.Length; i++)
                    Assert.AreEqual(blobs.Names.IndexOf(TlTimelineBlobs.NameOf(ref set[i].Blob.Value)) + 1, set[i].Id);
            }
            using var index = manager.CreateEntityQuery(typeof(TlLibraryIndex));
            Assert.AreEqual(KindWorld.Vault, index.GetSingleton<TlLibraryIndex>().IdOf(KindGameBlobs.AssetOf("vault")));
            return library;
        }

        static byte[] Tlb(KindGameBlobs blobs, int index) => TlTimelineBlobs.ToArray(ref blobs.Blobs[index].Value.Tlb);
    }
}
