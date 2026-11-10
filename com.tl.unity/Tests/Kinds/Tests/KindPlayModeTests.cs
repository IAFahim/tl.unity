using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace KindGame
{
    public class KindPlayModeTests
    {
        [UnityTest]
        public IEnumerator PlayModeScriptHashesEachTickLikeDotNet()
        {
            using (var game = new KindWorld(KindScript.Rows, true))
            {
                yield return null;
                var hash = KindScript.PlayEvents(game, KindScript.Steps);
                Assert.AreEqual(KindScript.EventsPinned, hash, "events hash under the PlayMode host");
                Assert.AreEqual(0, game.Tick.DroppedPulses);
                Assert.AreEqual(0, game.Tick.DroppedStarts);
            }
            using (var game = new KindWorld(KindScript.Rows))
            {
                yield return null;
                var hash = KindScript.Play(game, KindScript.Steps);
                Assert.AreEqual(KindScript.Pinned, hash, "columns hash under the PlayMode host");
                Assert.AreEqual(0, game.Tick.DroppedPulses);
                Assert.AreEqual(0, game.Tick.DroppedStarts);
            }
        }
    }
}
