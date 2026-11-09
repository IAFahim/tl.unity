using NUnit.Framework;
using Tl.Unity;
using Unity.Entities;

namespace KindGame
{
    [TestFixture]
    public class ControlTests
    {
        const byte Locomotion = 1;

        KindWorld _game;

        [SetUp]
        public void SetUp()
        {
            _game = new KindWorld(2);
        }

        [TearDown]
        public void TearDown()
        {
            _game.Dispose();
        }

        void Send(int row, TimelineRequest request) => _game.Entities.GetBuffer<TimelineRequest>(_game.Rows[row]).Add(request);

        void Steps(int count, bool forward = true)
        {
            for (var i = 0; i < count; i++)
                _game.Step(forward);
        }

        void Expect(int tick, float x, float y, int stamina)
        {
            Assert.AreEqual(tick, _game.Slots(0)[0].Tick);
            Assert.AreEqual(x, _game.Motion(0).X);
            Assert.AreEqual(y, _game.Motion(0).Y);
            Assert.AreEqual(stamina, _game.Stamina(0));
        }

        [Test]
        public void SeekLandsWithoutCrossingThenPlaysThroughTheMarkerAgain()
        {
            _game.Start(0, KindWorld.Jump);
            Steps(8);
            Expect(7, 8f, 48f, 9);
            Send(0, TimelineRequest.Seek(0, KindWorld.Jump, 2));
            Steps(1);
            Expect(2, 9f, 54f, 9);
            Steps(3);
            Expect(5, 12f, 72f, 9);
            Steps(1);
            Expect(6, 13f, 78f, 19);
        }

        [Test]
        public void SeekForwardSkipsTheMarkerAndClampsPastTheEnd()
        {
            _game.Start(0, KindWorld.Jump);
            Steps(1);
            Send(0, TimelineRequest.Seek(0, KindWorld.Jump, 9));
            Steps(1);
            Expect(9, 2f, 12f, -1);
            Send(0, TimelineRequest.Seek(0, KindWorld.Jump, 500));
            Steps(1);
            Expect(11, 3f, 18f, -1);
            Steps(1);
            var slots = _game.Slots(0);
            Assert.IsTrue(slots.Length == 0 || slots[0].Timeline == 0);
            Assert.AreEqual(18f, _game.Motion(0).Y);
        }

        [Test]
        public void ASoughtPausedPlaybackLandsAndStaysPausedUntilResumed()
        {
            _game.Start(0, KindWorld.Jump);
            Steps(3);
            Send(0, TimelineRequest.Pause(0, KindWorld.Jump));
            Steps(1);
            Expect(2, 3f, 18f, -1);
            Send(0, TimelineRequest.Seek(0, KindWorld.Jump, 7));
            Steps(1);
            Steps(1, false);
            Expect(7, 3f, 18f, -1);
            Send(0, TimelineRequest.Resume(0, KindWorld.Jump));
            Steps(1);
            Expect(8, 4f, 24f, -1);
        }

        [Test]
        public void ReversingOnePlaybackUndoesItsTicksExactlyParksAtTheStartAndPlaysAgain()
        {
            _game.Start(0, KindWorld.Jump);
            _game.Start(1, KindWorld.Jump);
            Steps(7);
            Expect(6, 7f, 42f, 9);
            Send(0, TimelineRequest.Reverse(0, KindWorld.Jump, true));
            Steps(1);
            Expect(5, 6f, 36f, 9);
            Assert.AreEqual(7, _game.Slots(1)[0].Tick);
            Steps(5);
            Expect(0, 1f, 6f, 10);
            _game.StopAll(1);
            Steps(3);
            Assert.AreEqual(KindWorld.Jump, _game.Slots(0)[0].Timeline);
            Assert.AreEqual(global::Tl.PlaybackRules.Parked | global::Tl.PlaybackFlags.Reverse, _game.Slots(0)[0].Flags);
            Expect(0, 1f, 6f, 10);
            var other = _game.Slots(1);
            Assert.IsTrue(other.Length == 0 || other[0].Timeline == 0);
            _game.Entities.SetComponentData(_game.Rows[1], _game.Entities.GetComponentData<Movement_MotionData>(_game.Rows[0]));
            _game.Entities.SetComponentData(_game.Rows[1], _game.Entities.GetComponentData<Movement_StaminaData>(_game.Rows[0]));
            _game.Start(1, KindWorld.Jump);
            Send(0, TimelineRequest.Reverse(0, KindWorld.Jump, false));
            for (var s = 0; s < 7; s++)
            {
                Steps(1);
                Assert.AreEqual(_game.Slots(1)[0].Tick, _game.Slots(0)[0].Tick);
                Assert.AreEqual(_game.Motion(1).X, _game.Motion(0).X);
                Assert.AreEqual(_game.Motion(1).Y, _game.Motion(0).Y);
                Assert.AreEqual(_game.Stamina(1), _game.Stamina(0));
            }
            Expect(6, 8f, 48f, 19);
        }

        [Test]
        public void ChannelVerbsActOnTheChannelsPlaybackAndChannelZeroDoesNothing()
        {
            _game.Start(0, KindWorld.Jump);
            Steps(3);
            Send(0, TimelineRequest.PauseChannel(0, 0));
            Send(0, TimelineRequest.SeekChannel(0, 0, 9));
            Send(0, TimelineRequest.ReverseChannel(0, 0, true));
            Steps(1);
            Expect(3, 4f, 24f, -1);
            Send(0, TimelineRequest.PauseChannel(0, Locomotion));
            Steps(1);
            Expect(3, 4f, 24f, -1);
            Send(0, TimelineRequest.ResumeChannel(0, Locomotion));
            Steps(1);
            Expect(4, 5f, 30f, -1);
            Send(0, TimelineRequest.SeekChannel(0, Locomotion, 8));
            Steps(1);
            Expect(8, 6f, 36f, -1);
            Send(0, TimelineRequest.ReverseChannel(0, Locomotion, true));
            Steps(1);
            Expect(7, 5f, 30f, -1);
        }
    }
}
