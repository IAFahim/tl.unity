using NUnit.Framework;
using Tl.Unity;
using Unity.Entities;

namespace KindGame
{
    [TestFixture]
    public class KindSpeedTests
    {
        static void Send(KindWorld game, int row, TimelineRequest request) => game.Entities.GetBuffer<TimelineRequest>(game.Rows[row]).Add(request);

        [Test]
        public void TwoRatesFeedingOneKindResolvePriorityPerSubstep()
        {
            using var game = new KindWorld(1);
            game.Start(0, KindWorld.Run);
            game.Start(0, KindWorld.SpeedBoost);
            Send(game, 0, TimelineRequest.SetSpeed(0, KindWorld.SpeedBoost, 32));
            var expected = 0f;
            for (var s = 1; s <= 14; s++)
            {
                game.Step();
                expected += s <= 10 ? 10f : 2f;
                Assert.AreEqual(expected, game.Motion(0).X, "step " + s);
            }
            Assert.AreEqual(108f, game.Motion(0).X);
        }

        [Test]
        public void TwoXOverSixStepsEqualsOneXOverTwelveWithEachModePulsesAndMarkers()
        {
            using var game = new KindWorld(2);
            game.Start(0, KindWorld.Jump);
            Send(game, 0, TimelineRequest.SetSpeed(0, KindWorld.Jump, 32));
            game.Start(1, KindWorld.Jump);
            var fast = new float[7];
            for (var s = 0; s < 14; s++)
            {
                game.Step();
                if ((s & 1) == 1)
                    fast[s >> 1] = game.Motion(0).Y;
            }
            Assert.AreEqual(12f, game.Motion(0).X);
            Assert.AreEqual(72f, game.Motion(0).Y);
            Assert.AreEqual(game.Motion(1).Y, game.Motion(0).Y);
            Assert.AreEqual(game.Stamina(1), game.Stamina(0));
            CollectionAssert.AreEqual(new[] { 24f, 48f, 72f, 72f, 72f, 72f, 72f }, fast);
        }

        [Test]
        public void HalfSpeedContributesNothingInAZeroCrossingStep()
        {
            using var game = new KindWorld(2);
            game.Start(0, KindWorld.Run);
            Send(game, 0, TimelineRequest.SetSpeed(0, KindWorld.Run, 8));
            game.Start(1, KindWorld.Jump);
            Send(game, 1, TimelineRequest.SetSpeed(0, KindWorld.Jump, 8));
            var xs = new float[6];
            var ys = new float[6];
            for (var s = 0; s < 6; s++)
            {
                game.Step();
                xs[s] = game.Motion(0).X;
                ys[s] = game.Motion(1).Y;
            }
            CollectionAssert.AreEqual(new[] { 0f, 2f, 2f, 4f, 4f, 6f }, xs);
            CollectionAssert.AreEqual(new[] { 0f, 6f, 6f, 12f, 12f, 18f }, ys);
        }

        [Test]
        public void ReversingAtTwoXParksAtTheStartLikeOneX()
        {
            using var game = new KindWorld(1);
            game.Start(0, KindWorld.Jump);
            Send(game, 0, TimelineRequest.SetSpeed(0, KindWorld.Jump, 32));
            for (var s = 0; s < 4; s++)
                game.Step();
            Send(game, 0, TimelineRequest.Reverse(0, KindWorld.Jump, true));
            for (var s = 0; s < 4; s++)
                game.Step();
            Assert.AreEqual(global::Tl.PlaybackRules.Parked | global::Tl.PlaybackFlags.Reverse, game.Slots(0)[0].Flags);
            Assert.AreEqual(1f, game.Motion(0).X);
            Assert.AreEqual(6f, game.Motion(0).Y);
        }

        [Test]
        public void AGamePulseLandsOnceInSubstepZeroOfASampledEntity()
        {
            using var game = new KindWorld(1);
            game.Start(0, KindWorld.Stomp);
            Send(game, 0, TimelineRequest.SetSpeed(0, KindWorld.Stomp, 48));
            var health = new int[4];
            for (var s = 0; s < 4; s++)
            {
                game.Damage(0, 1);
                game.Step();
                health[s] = game.Health(0);
            }
            CollectionAssert.AreEqual(new[] { 974, 963, 962, 961 }, health);
        }
    }
}
