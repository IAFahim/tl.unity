using System;
using System.Text;
using NUnit.Framework;
using ParityGame;
using ParityPack;
using Tl.Unity;
using Unity.Entities;

namespace ParityTests
{
    public class HostParityTests
    {
        const int Rows = 4;

        static string Hex(Screen screen)
        {
            var text = new StringBuilder();
            foreach (var value in new[] { screen.Caption, screen.Mood, screen.Reel })
                foreach (var b in BitConverter.GetBytes(value))
                    text.Append(b.ToString("X2"));
            return text.ToString();
        }

        [Test]
        public void TheIdleKindStaysInThePackAndTheGameAssemblyOnlyFeedsIt()
        {
            var pack = typeof(Caption).Assembly;
            var game = typeof(LineTrack).Assembly;
            Assert.IsNotNull(pack.GetType("Tl.Generated.TlAuto_Tl_Unity_ParityPack_Jobs"));
            Assert.IsNotNull(game.GetType("Tl.Generated.TlAuto_Tl_Unity_ParityGame_Feeds"));
            Assert.IsNull(game.GetType("Tl.Generated.TlAuto_Tl_Unity_ParityGame_Jobs"));
            Assert.IsTrue(typeof(IComponentData).IsAssignableFrom(typeof(Screen)));
        }

        sealed class Rig : IDisposable
        {
            public TlLibrary Library;
            public World World;
            public EntityManager Entities;
            public SystemHandle Tick, Feed, Apply;
            public Entity[] Rows = new Entity[Rows_];
            const int Rows_ = HostParityTests.Rows;

            public Rig()
            {
                Library = TlLibrary.FromBytes(ParityFixtureData.Library());
                World = new World("TlParity");
                Entities = World.EntityManager;
                Library.Publish(Entities);
                Tick = World.CreateSystem<TimelineKindTickSystem>();
                Feed = World.CreateSystem(typeof(TlFeedSystem<>).MakeGenericType(typeof(LineTrack).Assembly.GetType("Tl.Generated.TlAuto_Tl_Unity_ParityGame_Feeds", true)));
                Apply = World.CreateSystem(typeof(TlKindSystem<>).MakeGenericType(typeof(Caption).Assembly.GetType("Tl.Generated.TlAuto_Tl_Unity_ParityPack_Jobs", true)));
                for (var e = 0; e < Rows_; e++)
                {
                    var types = new System.Collections.Generic.List<ComponentType> { typeof(Playback), typeof(TimelineRequest), typeof(TimelinePlaying), typeof(Screen), typeof(Stage) };
                    types.AddRange(CaptionKind.Types);
                    types.AddRange(MoodKind.Types);
                    types.AddRange(ReelKind.Types);
                    Rows[e] = Entities.CreateEntity(types.ToArray());
                    Entities.SetComponentEnabled<TimelinePlaying>(Rows[e], false);
                    CaptionKind.Init(Entities, Rows[e]);
                    MoodKind.Init(Entities, Rows[e]);
                    ReelKind.Init(Entities, Rows[e]);
                    Entities.SetComponentData(Rows[e], new Stage { Offset = e * 100 });
                }
            }

            public void Step()
            {
                Tick.Update(World.Unmanaged);
                Feed.Update(World.Unmanaged);
                Apply.Update(World.Unmanaged);
                Entities.CompleteAllTrackedJobs();
            }

            public void Dispose()
            {
                World.Dispose();
                Library.Dispose();
            }
        }

        [Test]
        public void ColumnBytesEveryTickEqualTheDotNetFixtureIncludingIdleAcrossAssembliesAndTheRewind()
        {
            var script = ParityFixtureData.Script().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var expected = ParityFixtureData.Expected().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(script.Length, expected.Length);
            using var rig = new Rig();
            var entities = rig.Entities;
            var step = entities.CreateEntityQuery(typeof(TimelineStep));
            var forwardNow = true;
            var first = -1;
            for (var t = 0; t < script.Length && first < 0; t++)
            {
                var parts = script[t].Trim().Split(';');
                var forward = parts[0] == "1";
                if (forward != forwardNow)
                {
                    step.SetSingleton(new TimelineStep { Forward = forward });
                    forwardNow = forward;
                }
                if (parts.Length > 1 && parts[1].Length > 0)
                    foreach (var command in parts[1].Split(','))
                    {
                        var c = command.Split(':');
                        var requests = entities.GetBuffer<TimelineRequest>(rig.Rows[int.Parse(c[0])]);
                        if (c[1] == "S") requests.Add(TimelineRequest.Start(0, ushort.Parse(c[2]), 0));
                        else if (c[1] == "P") requests.Add(TimelineRequest.Stop(0, ushort.Parse(c[2])));
                        else requests.Add(TimelineRequest.StopAll(0));
                    }
                rig.Step();
                var actual = new StringBuilder();
                for (var e = 0; e < Rows; e++) actual.Append(Hex(entities.GetComponentData<Screen>(rig.Rows[e])));
                if (actual.ToString() != expected[t].Trim()) first = t;
            }
            Assert.AreEqual(-1, first, "column bytes first differ from the .NET fixture at tick " + first);
        }

        [Test]
        public void AResetRowDropsItsPlaybacksAndTheNextStartIsClean()
        {
            using var rig = new Rig();
            var entities = rig.Entities;
            var row = rig.Rows[0];
            entities.GetBuffer<TimelineRequest>(row).Add(TimelineRequest.Start(0, 4, 0));
            for (var i = 0; i < 6; i++) rig.Step();
            Assert.AreEqual(21, entities.GetComponentData<Screen>(row).Caption);
            var requests = entities.GetBuffer<TimelineRequest>(row);
            requests.Add(TimelineRequest.Reset(0));
            requests.Add(TimelineRequest.Start(0, 3, 0));
            rig.Step();
            var live = 0;
            foreach (var playback in entities.GetBuffer<Playback>(row))
            {
                if (playback.Timeline == 4) Assert.Fail("the old occupant's playback survived Reset");
                if (playback.Timeline == 3) live++;
            }
            Assert.AreEqual(1, live);
            Assert.AreEqual(-1, entities.GetComponentData<Screen>(row).Caption);
            Assert.AreEqual(0, entities.GetComponentData<Screen>(row).Mood);
        }
    }
}
