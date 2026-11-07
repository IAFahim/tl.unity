using NUnit.Framework;
using TlUnityGenerated;

namespace DirectGame
{
    public class DirectApplyTests
    {
        [Test]
        public void AKindAppliesIntoTheCoderComponentWithNoTimelineSystem()
        {
            using (var game = new DirectWorld(3))
            {
                game.Start(1, DirectWorld.Stroll);
                for (var i = 0; i < 5; i++)
                    game.Step();
                var before = game.Stride(1).X;
                game.Step();
                Assert.AreEqual(2f, game.Stride(1).X - before, 1e-4f);
                Assert.GreaterOrEqual(before, 8f);
                Assert.AreEqual(0f, game.Stride(0).X);
                Assert.AreEqual(0f, game.Stride(2).X);
            }
        }

        [Test]
        public void ThePinnedInlineCapacityWins()
        {
            Assert.AreEqual(2, PacingKind.InlineCapacity);
        }
    }
}
