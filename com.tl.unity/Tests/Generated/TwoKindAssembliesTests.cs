using KindGame;
using NUnit.Framework;
using Tl.Unity;

namespace GeneratedGame
{
    public class TwoKindAssembliesTests
    {
        [Test]
        public void KindIdsComeFromKindNamesInEveryAssembly()
        {
            Assert.AreEqual(KindIndex.IdOf("GeneratedGame.Gust"), GustKind.Id);
            Assert.AreEqual(KindIndex.IdOf("KindGame.Damage"), DamageKind.Id);
            Assert.AreEqual(KindIndex.IdOf("KindGame.StatChange"), StatChangeKind.Id);
            var registry = KindRegistry.Discover();
            var gust = registry.IndexOf("GeneratedGame.Gust");
            var damage = registry.IndexOf("KindGame.Damage");
            Assert.GreaterOrEqual(gust, 0);
            Assert.GreaterOrEqual(damage, 0);
            Assert.AreEqual("Tl.Unity.Generated.Tests", registry.SourceOf(gust));
            Assert.AreEqual("Tl.Unity.Kinds", registry.SourceOf(damage));
        }

        [Test]
        public void TheKindScriptKeepsItsHashNextToASecondKindAssembly()
        {
            using var scan = new LogScan();
            using var game = new KindWorld(KindScript.Rows);
            Assert.AreEqual(KindScript.Pinned, KindScript.Play(game, KindScript.Steps));
            game.Entities.CompleteAllTrackedJobs();
            Assert.AreEqual(0, game.Tick.DroppedPulses);
            Assert.AreEqual("", scan.Report(), "warnings or errors logged during the steps");
        }

        [Test]
        public void TheEventScriptKeepsItsHashNextToASecondKindAssembly()
        {
            using var game = new KindWorld(KindScript.Rows, true);
            Assert.AreEqual(KindScript.EventsPinned, KindScript.PlayEvents(game, KindScript.Steps));
        }

        [Test]
        public void APulseToAKindTheLibraryLacksIsDroppedWhileTheOthersLand()
        {
            using var game = new KindWorld(2);
            var sender = game.Tick.BeginPulses(1);
            var writer = sender.Writer;
            writer.Begin(0);
            GustKind.Pulses(writer).To(game.Rows[0], 1.5f);
            DamageKind.Pulses(writer).To(game.Rows[0], 7);
            writer.End();
            game.Step();
            game.Entities.CompleteAllTrackedJobs();
            Assert.AreEqual(1, game.Tick.DroppedPulses);
            Assert.AreEqual(993, game.Health(0));
            Assert.AreEqual(1000, game.Health(1));
        }
    }
}
