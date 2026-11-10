using NUnit.Framework;
using Tl.Unity;
using Unity.Entities;

namespace KindGame
{
    public struct SplitMarker : IComponentData
    {
    }

    [TestFixture]
    public class KindHashTests
    {
        [Test]
        public void SixHundredStepKindScriptHashesLikeDotNet()
        {
            using (var scan = new LogScan())
            using (var game = new KindWorld(KindScript.Rows))
            {
                var hash = KindScript.Play(game, KindScript.Steps);
                Assert.AreEqual("", scan.Report(), "warnings or errors logged during the steps");
                Assert.AreEqual(KindScript.Pinned, hash);
                Assert.AreEqual(0, game.Tick.DroppedPulses);
                Assert.AreEqual(0, game.Tick.DroppedStarts);
            }
        }

        [Test]
        public void EventScriptRaisesTheSameCuesAsThePlainHost()
        {
            using (var scan = new LogScan())
            using (var game = new KindWorld(KindScript.Rows, true))
            {
                var hash = KindScript.PlayEvents(game, KindScript.Steps);
                Assert.AreEqual("", scan.Report(), "warnings or errors logged during the steps");
                Assert.AreEqual(KindScript.EventsPinned, hash);
                Assert.AreEqual(0, game.Tick.DroppedStarts);
            }
        }

        [Test]
        public void AWhistleRaisesOneCueOnTheStepItStarts()
        {
            using (var game = new KindWorld(3, true))
            {
                game.Start(1, Timelines.Whistle);
                game.Step();
                Assert.AreEqual(1, game.Cues(1).Length);
                Assert.AreEqual(7, game.Cues(1)[0].Record.Value.Voice);
                Assert.AreEqual(0, game.Cues(0).Length);
                game.Step();
                Assert.AreEqual(0, game.Cues(1).Length);
            }
        }

        [Test]
        public void EventsHashIsIndependentOfChunkPartitioning()
        {
            using (var game = new KindWorld(KindScript.Rows, true))
            {
                game.Entities.AddComponent<SplitMarker>(game.Rows[2]);
                game.Entities.AddComponent<SplitMarker>(game.Rows[3]);
                var hash = KindScript.PlayEvents(game, KindScript.Steps);
                Assert.AreEqual(KindScript.EventsPinned, hash);
                Assert.AreEqual(0, game.Tick.DroppedPulses);
                Assert.AreEqual(0, game.Tick.DroppedStarts);
            }
        }
    }
}
