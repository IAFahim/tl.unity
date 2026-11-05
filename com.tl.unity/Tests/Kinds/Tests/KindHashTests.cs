using NUnit.Framework;

namespace KindGame
{
    [TestFixture]
    public class KindHashTests
    {
        [Test]
        public void SixHundredStepKindScriptHashesLikeDotNet()
        {
            using (var game = new KindWorld(KindScript.Rows))
            {
                var hash = KindScript.Play(game, KindScript.Steps);
                Assert.AreEqual(KindScript.Pinned, hash);
                Assert.AreEqual(0, game.Tick.DroppedPulses);
                Assert.AreEqual(0, game.Tick.DroppedStarts);
            }
        }
    }
}
