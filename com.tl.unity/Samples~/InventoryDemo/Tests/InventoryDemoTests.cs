#if UNITY_EDITOR
using System.Collections;
using System.Globalization;
using NUnit.Framework;
using Tl.Pack.Inventory;
using Tl.Unity;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace InventoryDemo.Tests
{
    public class InventoryDemoTests
    {
        [UnityTest]
        public IEnumerator TheLootRunEndsWithTheDemoBag()
        {
            var scene = ScenePath("Inventory Demo");
            Assert.IsNotNull(scene, "Build the demo first: Tl > Samples > Build Inventory Demo.");
            var problems = 0;
            var firstProblem = "";
            void Count(string message, string stack, LogType type)
            {
                if (type == LogType.Log)
                    return;
                problems++;
                if (firstProblem.Length == 0)
                    firstProblem = message;
            }
            Application.logMessageReceived += Count;
            try
            {
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new LoadSceneParameters(LoadSceneMode.Single));
                var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
                using var bags = entities.CreateEntityQuery(ComponentType.ReadOnly<Bag>(), ComponentType.ReadOnly<Playback>());
                var deadline = Time.realtimeSinceStartup + 120f;
                while (bags.IsEmpty && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.IsFalse(bags.IsEmpty, "the Backpack with Tl Bag appears after the SubScene loads");
                var backpack = bags.GetSingletonEntity();
                var start = entities.GetComponentData<Bag>(backpack);
                var coinAt = -1;
                var gemAt = -1;
                var plankAt = -1;
                var last = -1;
                var final = default(Bag);
                deadline = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < deadline && last != 599)
                {
                    yield return null;
                    var playbacks = entities.GetBuffer<Playback>(backpack);
                    if (playbacks.Length == 0 || playbacks[0].Timeline == 0)
                        continue;
                    last = playbacks[0].Tick;
                    var bag = entities.GetComponentData<Bag>(backpack);
                    if (coinAt < 0 && bag.Coin == 10)
                        coinAt = last;
                    if (gemAt < 0 && bag.Gem == 1)
                        gemAt = last;
                    if (plankAt < 0 && bag.Plank == 2)
                        plankAt = last;
                    final = bag;
                }
                Debug.Log(string.Format(CultureInfo.InvariantCulture, "TL-DEMO inventory final={0} coinAt={1} gemAt={2} plankAt={3} start={4} problems={5} {6}",
                    BagOverlay.Show(final).Replace(' ', '_'), coinAt, gemAt, plankAt, BagOverlay.Show(start).Replace(' ', '_'), problems, firstProblem));
                Assert.AreEqual(599, last, "the run reached step 600");
                Assert.AreEqual(default(Bag), start, "the bag starts empty (Tl Bag with no items)");
                Assert.AreEqual(1, final.Wood, "Wood: 5 given, 4 crafted");
                Assert.AreEqual(3, final.Stone, "Stone");
                Assert.AreEqual(2, final.Plank, "Plank from the craft");
                Assert.AreEqual(1, final.Potion, "Potion: 2 given, 1 taken");
                Assert.AreEqual(1, final.Sword, "Sword");
                Assert.AreEqual(10, final.Coin, "Coin from the pickup marker");
                Assert.AreEqual(0, final.Key, "Key");
                Assert.AreEqual(1, final.Gem, "Gem from the pickup marker");
                Assert.AreEqual(420, coinAt, "the Coin pickup marker lands at tick 420");
                Assert.AreEqual(480, gemAt, "the Gem pickup marker lands at tick 480");
                Assert.AreEqual(299, plankAt, "the craft ends at tick 299");
                Assert.AreEqual(0, problems, firstProblem);
            }
            finally
            {
                Application.logMessageReceived -= Count;
            }
        }

        static string ScenePath(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets(name + " t:Scene"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == name)
                    return path;
            }
            return null;
        }
    }
}
#endif
