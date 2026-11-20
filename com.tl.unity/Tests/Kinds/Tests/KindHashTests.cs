using NUnit.Framework;

namespace KindGame
{
    [TestFixture]
    public class KindHashTests
    {
        static bool Plays(KindWorld game, int row, ushort timeline) => System.Array.Exists(game.Slots(row), slot => slot.Timeline == timeline);

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
        public void CrossScriptStompsTheAimedRowLikeThePlainHost()
        {
            using (var scan = new LogScan())
            using (var game = new KindWorld(KindScript.Rows, true))
            {
                game.AimEachRowAtTheNext();
                var hash = KindScript.PlayCross(game, KindScript.Steps);
                Assert.AreEqual("", scan.Report(), "warnings or errors logged during the steps");
                Assert.AreEqual(KindScript.CrossPinned, hash);
                Assert.AreEqual(0, game.Tick.DroppedStarts);
            }
        }

        [Test]
        public void AWhistleOnOneRowStartsAStompOnTheRowItAimsAtWhichHitsTwoTicksLater()
        {
            using (var game = new KindWorld(3, true))
            {
                game.AimEachRowAtTheNext();
                game.Start(1, Timelines.Whistle);
                game.Step();
                Assert.AreEqual(1, game.Cues(1).Length);
                Assert.IsFalse(Plays(game, 2, Timelines.Stomp));
                game.Step();
                Assert.IsTrue(Plays(game, 2, Timelines.Stomp));
                game.Step();
                Assert.AreEqual(1000, game.Health(2));
                game.Step();
                Assert.AreEqual(975, game.Health(2));
                Assert.AreEqual(1000, game.Health(0));
                Assert.AreEqual(1000, game.Health(1));
                Assert.IsFalse(Plays(game, 0, Timelines.Stomp));
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
    }
}
