using System;
using NUnit.Framework;
using PushGame;
using PushPack;
using Tl.Unity;
using Unity.Entities;

namespace PushTests
{
    public class PushTwoAssemblyTests
    {
        const ulong Pinned = 12545803591333190611UL;
        const int Rows = 4;
        const int Steps = 600;

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        static ulong Mix(ulong h, float v) => (h ^ (ulong)BitConverter.SingleToInt32Bits(v)) * 1099511628211UL;

        [Test]
        public void TheKindStaysInThePackAndTheFeederInTheGame()
        {
            var pack = typeof(PushKind).Assembly;
            var game = typeof(GustTrack).Assembly;
            Assert.IsNotNull(pack.GetType("PushPack.PushKindEntry"));
            Assert.IsNull(game.GetType("PushPack.PushKindEntry"));
            Assert.IsNotNull(pack.GetType("Tl.Generated.TlAuto_Tl_Unity_PushPack_Jobs"));
            Assert.IsNull(pack.GetType("Tl.Generated.TlAuto_Tl_Unity_PushPack_Feeds"));
            Assert.IsNotNull(game.GetType("Tl.Generated.TlAuto_Tl_Unity_PushGame_Feeds"));
            Assert.IsNull(game.GetType("Tl.Generated.TlAuto_Tl_Unity_PushGame_Jobs"));
            Assert.IsTrue(typeof(IComponentData).IsAssignableFrom(typeof(Push)));
            Assert.IsTrue(typeof(IComponentData).IsAssignableFrom(typeof(Wind)));
        }

        [Test]
        public void ATrackInTheGameFeedsTheKindInThePackWithTheDotNetHash()
        {
            using var library = TlLibrary.FromBytes(PushLibraryBytes.Bytes());
            using var world = new World("TlPush");
            var entities = world.EntityManager;
            library.Publish(entities);
            var tick = world.CreateSystem<TimelineKindTickSystem>();
            var feed = world.CreateSystem(typeof(TlFeedSystem<>).MakeGenericType(typeof(GustTrack).Assembly.GetType("Tl.Generated.TlAuto_Tl_Unity_PushGame_Feeds", true)));
            var apply = world.CreateSystem(typeof(TlKindSystem<>).MakeGenericType(typeof(PushKind).Assembly.GetType("Tl.Generated.TlAuto_Tl_Unity_PushPack_Jobs", true)));
            var rows = new Entity[Rows];
            for (var e = 0; e < Rows; e++)
            {
                var types = new System.Collections.Generic.List<ComponentType> { typeof(Playback), typeof(TimelineRequest), typeof(TimelinePlaying), typeof(Push), typeof(Wind) };
                types.AddRange(PushKindKind.Types);
                rows[e] = entities.CreateEntity(types.ToArray());
                entities.SetComponentEnabled<TimelinePlaying>(rows[e], false);
                PushKindKind.Init(entities, rows[e]);
                entities.SetComponentData(rows[e], new Wind { Scale = 1f + 0.25f * e });
            }
            var step = entities.CreateEntityQuery(typeof(TimelineStep));
            var forwardNow = true;
            var rng = 0x6D2B79F5u;
            var h = 14695981039346656037UL;
            var moved = false;
            for (var s = 0; s < Steps; s++)
            {
                var forward = !(s >= 200 && s < 240);
                if (forward != forwardNow)
                {
                    step.SetSingleton(new TimelineStep { Forward = forward });
                    forwardNow = forward;
                }
                for (var e = 0; e < Rows; e++)
                {
                    var r = Next(ref rng);
                    var requests = entities.GetBuffer<TimelineRequest>(rows[e]);
                    switch (r & 7)
                    {
                        case 0:
                        case 1:
                            requests.Add(TimelineRequest.Start(0, (ushort)(1 + (r >> 8) % 4), 0));
                            break;
                        case 2:
                            requests.Add(TimelineRequest.Stop(0, (ushort)(1 + (r >> 8) % 4)));
                            break;
                        case 3:
                            if (((r >> 8) & 15) == 0) requests.Add(TimelineRequest.StopAll(0));
                            break;
                    }
                }
                tick.Update(world.Unmanaged);
                feed.Update(world.Unmanaged);
                apply.Update(world.Unmanaged);
                entities.CompleteAllTrackedJobs();
                for (var e = 0; e < Rows; e++)
                {
                    var push = entities.GetComponentData<Push>(rows[e]);
                    moved |= push.X != 0f;
                    h = Mix(h, push.X);
                    h = Mix(h, push.Y);
                }
            }
            Assert.IsTrue(moved);
            Assert.AreEqual(Pinned, h);
        }
    }
}
