using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tl.Unity;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using CorePlayback = global::Tl.Playback;

namespace KindGame
{
    [TestFixture]
    public unsafe class KindHorizonTests
    {
        static void Glide(KindWorld game, byte limit = global::Tl.Horizon.Max)
        {
            for (var i = 0; i < game.Rows.Length; i++)
                game.Entities.AddComponent<TimelineHorizon>(game.Rows[i]);
            game.Entities.SetComponentData(game.TickHandle, new TimelineGlide { Limit = limit });
        }

        static int Steps(KindWorld game, int row) => game.Entities.GetComponentData<TimelineHorizon>(game.Rows[row]).Steps;

        static void Lazy(KindWorld game, byte limit = global::Tl.Horizon.Max)
        {
            for (var i = 0; i < game.Rows.Length; i++)
            {
                game.Entities.AddComponent<TimelineHorizon>(game.Rows[i]);
                game.Entities.AddComponent<TimelineClock>(game.Rows[i]);
                game.Entities.AddBuffer<PlaybackDue>(game.Rows[i]);
            }
            game.Entities.SetComponentData(game.TickHandle, new TimelineGlide { Limit = limit });
        }

        static void PlaybackBytes(List<byte> into, EntityManager entities, Entity entity)
        {
            var clock = entities.HasComponent<TimelineClock>(entity) ? entities.GetComponentData<TimelineClock>(entity).Value : (ushort)0;
            var buffer = entities.GetBuffer<Playback>(entity, true);
            into.Add((byte)buffer.Length);
            for (var i = 0; i < buffer.Length; i++)
            {
                var slot = buffer[i];
                slot.Tick = TimelineTicks.Real(in slot, clock);
                var data = (byte*)&slot;
                for (var b = 0; b < sizeof(Playback); b++)
                    into.Add(data[b]);
            }
        }

        static int Expected(KindWorld game, int row, bool forward, int limit)
        {
            using var query = game.Entities.CreateEntityQuery(typeof(TimelineLibrary));
            var kinds = query.GetSingleton<TimelineLibrary>().Kinds;
            var tables = kinds.Tables;
            var slots = game.Entities.GetBuffer<Playback>(game.Rows[row], true);
            return global::Tl.Horizon.OfKindRun((CorePlayback*)slots.GetUnsafeReadOnlyPtr(), slots.Length, in tables, in kinds.Calm, global::Tl.SlotWord.WorldReverse(forward), limit);
        }

        static void Bytes<T>(List<byte> into, EntityManager entities, Entity entity) where T : unmanaged, IBufferElementData
        {
            var buffer = entities.GetBuffer<T>(entity, true);
            var data = (byte*)buffer.GetUnsafeReadOnlyPtr();
            var length = buffer.Length * UnsafeUtility.SizeOf<T>();
            into.Add((byte)buffer.Length);
            for (var i = 0; i < length; i++)
                into.Add(data[i]);
        }

        static int EntryBytes<T>(List<byte> into, EntityManager entities, Entity entity, in KindIndex index) where T : unmanaged, IBufferElementData
        {
            var buffer = entities.GetBuffer<T>(entity, true);
            var slots = entities.GetBuffer<Playback>(entity, true);
            var stride = UnsafeUtility.SizeOf<T>();
            var data = (byte*)buffer.GetUnsafeReadOnlyPtr();
            var expected = stackalloc byte[stride];
            var valid = 0;
            into.Add((byte)buffer.Length);
            for (var i = 0; i < buffer.Length; i++)
            {
                var entry = (global::Tl.KindEntry*)(data + (long)i * stride);
                var cacheable = stride >= KindEntryCache.MinStride && KindEntryCache.Cacheable(entry);
                if (cacheable && (KindEntryCache.Bits(entry) & KindEntryCache.Valid) != 0)
                {
                    UnsafeUtility.MemCpy(expected, entry, stride);
                    var slot = slots[entry->Slot];
                    KindEntryCache.Write((global::Tl.KindEntry*)expected, (CorePlayback*)&slot);
                    for (var b = 8; b < 12; b++)
                        Assert.AreEqual(expected[b], ((byte*)entry)[b], "stale cache for slot " + entry->Slot);
                    if (stride >= KindEntryCache.StepStride)
                        Assert.AreEqual(index.PackedStep(entry->Timeline, entry->Step), *(uint*)((byte*)entry + 12), "stale step for slot " + entry->Slot);
                    valid++;
                }
                var cached = stride >= KindEntryCache.StepStride ? 16 : 12;
                for (var b = 0; b < stride; b++)
                    into.Add(cacheable && b >= 8 && b < cached ? (byte)0 : ((byte*)entry)[b]);
            }
            return valid;
        }

        int _cachedEntries;

        static void Value<T>(List<byte> into, T value) where T : unmanaged
        {
            var data = (byte*)&value;
            for (var i = 0; i < sizeof(T); i++)
                into.Add(data[i]);
        }

        byte[] Snapshot(KindWorld game)
        {
            var entities = game.Entities;
            entities.CompleteAllTrackedJobs();
            using var libraryQuery = entities.CreateEntityQuery(typeof(TimelineLibrary));
            var index = libraryQuery.GetSingleton<TimelineLibrary>().Kinds;
            var bytes = new List<byte>();
            foreach (var row in game.Rows)
            {
                PlaybackBytes(bytes, entities, row);
                _cachedEntries += EntryBytes<LocomotionEntry>(bytes, entities, row, in index);
                _cachedEntries += EntryBytes<LiftEntry>(bytes, entities, row, in index);
                _cachedEntries += EntryBytes<StatChangeEntry>(bytes, entities, row, in index);
                _cachedEntries += EntryBytes<DamageEntry>(bytes, entities, row, in index);
                bytes.Add((byte)((entities.IsComponentEnabled<TimelinePlaying>(row) ? 1 : 0) | (entities.IsComponentEnabled<LocomotionActive>(row) ? 2 : 0) | (entities.IsComponentEnabled<LiftActive>(row) ? 4 : 0) | (entities.IsComponentEnabled<StatChangeActive>(row) ? 8 : 0) | (entities.IsComponentEnabled<DamageActive>(row) ? 16 : 0)));
                Value(bytes, entities.GetComponentData<Movement_MotionData>(row));
                Value(bytes, entities.GetComponentData<Movement_StaminaData>(row));
                Value(bytes, entities.GetComponentData<Vitals_HealthData>(row));
            }
            return bytes.ToArray();
        }

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        [Test]
        public void HorizonRowsReproduceThePinnedKindScriptHashes()
        {
            using (var game = new KindWorld(KindScript.Rows))
            {
                Glide(game);
                Assert.AreEqual(KindScript.Pinned, KindScript.Play(game, KindScript.Steps));
                Assert.AreEqual(0, game.Tick.DroppedStarts);
            }
            using (var game = new KindWorld(KindScript.Rows, true))
            {
                Glide(game);
                Assert.AreEqual(KindScript.EventsPinned, KindScript.PlayEvents(game, KindScript.Steps));
            }
        }

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(255)]
        public void GlideMatchesTheEdgeLawEveryTickForPlaybacksKindRowsAndOutputs(int limit)
        {
            var ids = new[] { KindWorld.Jump, KindWorld.PotionHop, KindWorld.Run, KindWorld.SpeedBoost, KindWorld.Stomp, KindWorld.Vault, KindWorld.Waltz };
            using var reference = new KindWorld(16);
            using var candidate = new KindWorld(16);
            Glide(candidate, (byte)limit);
            var rng = 0x51ED270Bu;
            var glided = 0;
            for (var step = 0; step < 1500; step++)
            {
                var forward = !(step >= 500 && step < 560) && !(step >= 1100 && step < 1103);
                for (var row = 0; row < 16; row++)
                {
                    var r = Next(ref rng);
                    switch (r % 64)
                    {
                        case 0:
                        case 1:
                            var start = ids[(r >> 8) % (uint)ids.Length];
                            reference.Start(row, start);
                            candidate.Start(row, start);
                            break;
                        case 2:
                            var stop = ids[(r >> 8) % (uint)ids.Length];
                            reference.Stop(row, stop);
                            candidate.Stop(row, stop);
                            break;
                        case 3:
                            reference.Damage(row, 1 + (int)((r >> 8) & 3));
                            candidate.Damage(row, 1 + (int)((r >> 8) & 3));
                            break;
                        case 4:
                            if (((r >> 8) & 3) == 0 && reference.Entities.GetBuffer<Playback>(reference.Rows[row], true).Length > 0)
                            {
                                reference.Edit(row, 0, p => p);
                                candidate.Edit(row, 0, p => p);
                            }
                            break;
                    }
                }
                reference.Step(forward);
                candidate.Step(forward);
                Assert.AreEqual(Snapshot(reference), Snapshot(candidate), "step " + step);
                for (var row = 0; row < 16; row++)
                    if (Steps(candidate, row) > 0)
                        glided++;
            }
            Assert.Greater(glided, 1000);
            Assert.AreEqual(reference.Tick.DroppedStarts, candidate.Tick.DroppedStarts);
        }

        [Test]
        public void OnlyAPlaybackWriteFromOutsideTheTickSystemForgetsTheHorizon()
        {
            using var game = new KindWorld(2);
            Glide(game, 3);
            game.Start(0, KindWorld.Run);
            game.Start(1, KindWorld.Run);
            game.Step();
            Assert.AreEqual(0, Steps(game, 0));
            game.Step();
            Assert.AreEqual(3, Steps(game, 0));
            Assert.AreEqual(Expected(game, 0, true, 3), Steps(game, 0));
            game.Step();
            Assert.AreEqual(2, Steps(game, 0));
            game.Step();
            Assert.AreEqual(1, Steps(game, 0));
            Assert.AreEqual(3, Tick(game, 0));
            game.Edit(1, 0, p => p);
            game.Step();
            Assert.AreEqual(3, Steps(game, 0));
            Assert.AreEqual(4, Tick(game, 0));
            game.Step();
            game.Step();
            game.Step();
            Assert.AreEqual(0, Steps(game, 0));
            game.Step();
            Assert.AreEqual(3, Steps(game, 0));
            Assert.AreEqual(8, Tick(game, 0));
            game.Step(false);
            Assert.AreEqual(7, Tick(game, 0));
            Assert.AreEqual(Expected(game, 0, false, 3), Steps(game, 0));
            Assert.Greater(Steps(game, 0), 0);
            game.Start(0, KindWorld.Jump);
            game.Step(false);
            Assert.AreEqual(0, Steps(game, 0));
        }

        static int Tick(KindWorld game, int row) => game.Entities.GetBuffer<Playback>(game.Rows[row], true)[0].Tick;

        [Test]
        public void LimitZeroKeepsEveryRowOnTheEdgeLaw()
        {
            using var reference = new KindWorld(4);
            using var candidate = new KindWorld(4);
            Glide(candidate, 0);
            for (var row = 0; row < 4; row++)
            {
                reference.Start(row, KindWorld.Run);
                candidate.Start(row, KindWorld.Run);
            }
            for (var step = 0; step < 120; step++)
            {
                reference.Step();
                candidate.Step();
                for (var row = 0; row < 4; row++)
                    Assert.AreEqual(0, Steps(candidate, row));
            }
            Assert.AreEqual(Snapshot(reference), Snapshot(candidate));
        }

        [Test]
        public void WarmGlidingTicksRunUnderBurstAndAllocateNothingOnTheMainThread()
        {
            using var game = new KindWorld(256);
            Glide(game);
            for (var row = 0; row < 256; row++)
                game.Start(row, KindWorld.Run);
            for (var step = 0; step < 64; step++)
                game.Step();
            game.Entities.CompleteAllTrackedJobs();
            var managedBefore = game.Tick.ManagedRuns;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var step = 0; step < 1000; step++)
                game.Step();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated);
            Assert.AreEqual(managedBefore, game.Tick.ManagedRuns);
            Assert.AreEqual(0, game.Tick.ManagedRuns, "the kind tick job ran without Burst");
            Assert.Greater(Steps(game, 0) + Steps(game, 1) + Steps(game, 2), 0);
        }

        [Test]
        public void LazyRowsReproduceThePinnedKindScriptHashes()
        {
            using (var game = new KindWorld(KindScript.Rows))
            {
                Lazy(game);
                Assert.AreEqual(KindScript.Pinned, KindScript.Play(game, KindScript.Steps));
                Assert.AreEqual(0, game.Tick.DroppedStarts);
            }
            using (var game = new KindWorld(KindScript.Rows, true))
            {
                Lazy(game);
                Assert.AreEqual(KindScript.EventsPinned, KindScript.PlayEvents(game, KindScript.Steps));
            }
        }

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(255)]
        public void LazyRowsMatchTheEdgeLawEveryTickThroughLimitAndDirectionChanges(int limit)
        {
            var ids = new[] { KindWorld.Jump, KindWorld.PotionHop, KindWorld.Run, KindWorld.SpeedBoost, KindWorld.Stomp, KindWorld.Vault, KindWorld.Waltz };
            using var reference = new KindWorld(16);
            using var candidate = new KindWorld(16);
            Lazy(candidate, (byte)limit);
            var rng = 0x6A09E667u;
            var calm = 0;
            _cachedEntries = 0;
            for (var step = 0; step < 1500; step++)
            {
                if (step == 700) candidate.Entities.SetComponentData(candidate.TickHandle, new TimelineGlide { Limit = 0 });
                if (step == 760) candidate.Entities.SetComponentData(candidate.TickHandle, new TimelineGlide { Limit = (byte)limit });
                var forward = !(step >= 500 && step < 560) && !(step >= 1100 && step < 1103);
                for (var row = 0; row < 16; row++)
                {
                    var r = Next(ref rng);
                    switch (r % 64)
                    {
                        case 0:
                        case 1:
                            var start = ids[(r >> 8) % (uint)ids.Length];
                            reference.Start(row, start);
                            candidate.Start(row, start);
                            break;
                        case 2:
                            var stop = ids[(r >> 8) % (uint)ids.Length];
                            reference.Stop(row, stop);
                            candidate.Stop(row, stop);
                            break;
                        case 3:
                            reference.Damage(row, 1 + (int)((r >> 8) & 3));
                            candidate.Damage(row, 1 + (int)((r >> 8) & 3));
                            break;
                        case 4:
                            if (((r >> 8) & 3) == 0 && reference.Entities.GetBuffer<Playback>(reference.Rows[row], true).Length > 0)
                            {
                                reference.Edit(row, 0, p => p);
                                candidate.Edit(row, 0, p => p);
                            }
                            break;
                    }
                }
                reference.Step(forward);
                candidate.Step(forward);
                Assert.AreEqual(Snapshot(reference), Snapshot(candidate), "step " + step);
                for (var row = 0; row < 16; row++)
                    if (Steps(candidate, row) > 0)
                        calm++;
            }
            Assert.Greater(calm, 1000);
            Assert.Greater(_cachedEntries, 1000);
            Assert.AreEqual(reference.Tick.DroppedStarts, candidate.Tick.DroppedStarts);
        }

        static void EditReal(KindWorld game, int row, int slot, Func<Playback, Playback> change)
        {
            var entity = game.Rows[row];
            var clock = game.Entities.HasComponent<TimelineClock>(entity) ? game.Entities.GetComponentData<TimelineClock>(entity).Value : (ushort)0;
            game.Edit(row, slot, p =>
            {
                p.Tick = TimelineTicks.Real(in p, clock);
                p = change(p);
                var encoded = p;
                TimelineTicks.Encode((CorePlayback*)&encoded, clock);
                return encoded;
            });
        }

        static void Request(KindWorld reference, KindWorld candidate, int row, TimelineRequest request)
        {
            reference.Entities.GetBuffer<TimelineRequest>(reference.Rows[row]).Add(request);
            candidate.Entities.GetBuffer<TimelineRequest>(candidate.Rows[row]).Add(request);
        }

        [TestCase(1)]
        [TestCase(255)]
        public void KindEntryCachesMatchTheirSlotsThroughRequestsEditsAndModeChanges(int limit)
        {
            var ids = new[] { KindWorld.Jump, KindWorld.PotionHop, KindWorld.Run, KindWorld.SpeedBoost, KindWorld.Stomp, KindWorld.Vault, KindWorld.Waltz };
            using var reference = new KindWorld(24);
            using var candidate = new KindWorld(24);
            Lazy(candidate, (byte)limit);
            var rng = 0x3C6EF372u;
            _cachedEntries = 0;
            for (var step = 0; step < 1200; step++)
            {
                if (step == 400) candidate.Entities.SetComponentData(candidate.TickHandle, new TimelineGlide { Limit = 0 });
                if (step == 430) candidate.Entities.SetComponentData(candidate.TickHandle, new TimelineGlide { Limit = (byte)limit });
                var forward = !(step >= 600 && step < 650) && !(step >= 900 && step < 902);
                for (var row = 0; row < 24; row++)
                {
                    var r = Next(ref rng);
                    var timeline = ids[(r >> 8) % (uint)ids.Length];
                    switch (r % 48)
                    {
                        case 0:
                        case 1:
                            Request(reference, candidate, row, TimelineRequest.Start(0, timeline, (ushort)((r >> 16) & 7)));
                            break;
                        case 2:
                            Request(reference, candidate, row, TimelineRequest.Pause(0, timeline));
                            break;
                        case 3:
                            Request(reference, candidate, row, TimelineRequest.Resume(0, timeline));
                            break;
                        case 4:
                            Request(reference, candidate, row, TimelineRequest.Seek(0, timeline, (ushort)((r >> 16) & 15)));
                            break;
                        case 5:
                            Request(reference, candidate, row, TimelineRequest.Reverse(0, timeline, ((r >> 16) & 1) != 0));
                            break;
                        case 6:
                            Request(reference, candidate, row, TimelineRequest.Stop(0, timeline));
                            break;
                        case 7:
                            if (reference.Entities.GetBuffer<Playback>(reference.Rows[row], true).Length > 0)
                            {
                                var shift = (ushort)((r >> 16) & 3);
                                EditReal(reference, row, 0, p => { if (p.Tick > shift) p.Tick -= shift; return p; });
                                EditReal(candidate, row, 0, p => { if (p.Tick > shift) p.Tick -= shift; return p; });
                            }
                            break;
                    }
                }
                reference.Step(forward);
                candidate.Step(forward);
                Assert.AreEqual(Snapshot(reference), Snapshot(candidate), "step " + step);
            }
            Assert.Greater(_cachedEntries, 1000);
            Assert.AreEqual(reference.Tick.DroppedStarts, candidate.Tick.DroppedStarts);
        }

        [Test]
        public void ALazyRowThatLosesItsDueBufferDecodesAndDropsItsCaches()
        {
            using var reference = new KindWorld(4);
            using var candidate = new KindWorld(4);
            Lazy(candidate);
            for (var row = 0; row < 4; row++)
            {
                reference.Start(row, KindWorld.Run);
                candidate.Start(row, KindWorld.Run);
                reference.Start(row, KindWorld.Waltz);
                candidate.Start(row, KindWorld.Waltz);
            }
            _cachedEntries = 0;
            var cachedBefore = 0;
            for (var step = 0; step < 240; step++)
            {
                if (step == 97)
                {
                    cachedBefore = _cachedEntries;
                    for (var row = 0; row < 4; row++)
                        candidate.Entities.RemoveComponent<PlaybackDue>(candidate.Rows[row]);
                }
                reference.Step();
                candidate.Step();
                Assert.AreEqual(Snapshot(reference), Snapshot(candidate), "step " + step);
            }
            Assert.Greater(cachedBefore, 0);
            Assert.AreEqual(cachedBefore, _cachedEntries);
            for (var row = 0; row < 4; row++)
                Assert.AreEqual(0, candidate.Entities.GetComponentData<TimelineClock>(candidate.Rows[row]).Value);
        }

        [Test]
        public void ARepublishedLibraryRewritesEveryCachedStep()
        {
            using var other = TlLibrary.FromBytes(KindLibraryBytes.Bytes());
            using var reference = new KindWorld(8);
            using var candidate = new KindWorld(8);
            Lazy(candidate);
            using var query = candidate.Entities.CreateEntityQuery(typeof(TimelineLibrary));
            var original = default(TimelineLibrary);
            _cachedEntries = 0;
            for (var step = 0; step < 120; step++)
            {
                if (step == 0 || step == 60)
                    for (var row = 0; row < 8; row++)
                    {
                        reference.Start(row, KindWorld.Run);
                        candidate.Start(row, KindWorld.Run);
                        reference.Start(row, KindWorld.SpeedBoost);
                        candidate.Start(row, KindWorld.SpeedBoost);
                    }
                if (step == 17 || step == 77)
                {
                    candidate.Entities.CompleteAllTrackedJobs();
                    var published = query.GetSingleton<TimelineLibrary>();
                    if (step == 17) original = published;
                    var next = step == 17 ? other.Component : original;
                    next.Generation = published.Generation + 1;
                    query.SetSingleton(next);
                }
                reference.Step();
                candidate.Step();
                Assert.AreEqual(Snapshot(reference), Snapshot(candidate), "step " + step);
            }
            Assert.Greater(_cachedEntries, 100);
        }

        [Test]
        public void ACalmLazyRowKeepsItsSlotBytesAndOnlyMovesItsClock()
        {
            using var game = new KindWorld(1);
            Lazy(game);
            game.Start(0, KindWorld.Run);
            game.Step();
            game.Step();
            Assert.Greater(Steps(game, 0), 0);
            var before = game.Entities.GetBuffer<Playback>(game.Rows[0], true)[0];
            var clock = game.Entities.GetComponentData<TimelineClock>(game.Rows[0]).Value;
            game.Step();
            var after = game.Entities.GetBuffer<Playback>(game.Rows[0], true)[0];
            Assert.AreEqual(before.Tick, after.Tick);
            Assert.AreEqual((ushort)(clock + 1), game.Entities.GetComponentData<TimelineClock>(game.Rows[0]).Value);
            Assert.AreEqual((ushort)(TimelineTicks.Real(in before, clock) + 1), TimelineTicks.Real(in after, (ushort)(clock + 1)));
        }

        [Test]
        public void WarmLazyTicksRunUnderBurstAndAllocateNothingOnTheMainThread()
        {
            using var game = new KindWorld(256);
            Lazy(game);
            for (var row = 0; row < 256; row++)
                game.Start(row, KindWorld.Run);
            for (var step = 0; step < 64; step++)
                game.Step();
            game.Entities.CompleteAllTrackedJobs();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var step = 0; step < 1000; step++)
                game.Step();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated);
            Assert.AreEqual(0, game.Tick.ManagedRuns, "the kind tick job ran without Burst");
        }
    }
}
