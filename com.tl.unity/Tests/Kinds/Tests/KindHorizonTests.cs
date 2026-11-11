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

        static void Value<T>(List<byte> into, T value) where T : unmanaged
        {
            var data = (byte*)&value;
            for (var i = 0; i < sizeof(T); i++)
                into.Add(data[i]);
        }

        static byte[] Snapshot(KindWorld game)
        {
            var entities = game.Entities;
            entities.CompleteAllTrackedJobs();
            var bytes = new List<byte>();
            foreach (var row in game.Rows)
            {
                Bytes<Playback>(bytes, entities, row);
                Bytes<LocomotionEntry>(bytes, entities, row);
                Bytes<LiftEntry>(bytes, entities, row);
                Bytes<StatChangeEntry>(bytes, entities, row);
                Bytes<DamageEntry>(bytes, entities, row);
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
    }
}
