#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Tl.Pack.Stats;
using Tl.Unity;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace StatsDemo.Tests
{
    public class StatsDemoTests
    {
        [UnityTest]
        public IEnumerator TheDuelGivesHealth64And53And72()
        {
            var scene = ScenePath("Stats Demo");
            Assert.IsNotNull(scene, "Build the demo first: Tl > Samples > Build Stats Demo.");
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
                using var heroes = entities.CreateEntityQuery(ComponentType.ReadOnly<Health>(), ComponentType.ReadOnly<Attack>(), ComponentType.ReadOnly<Playback>());
                var deadline = Time.realtimeSinceStartup + 120f;
                while (heroes.IsEmpty && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.IsFalse(heroes.IsEmpty, "the Hero with Tl Stats appears after the SubScene loads");
                var hero = heroes.GetSingletonEntity();
                var seen = new Dictionary<int, (int Health, int Attack)>();
                var mostBonus = 0;
                deadline = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < deadline && !seen.ContainsKey(599))
                {
                    yield return null;
                    var playbacks = entities.GetBuffer<Playback>(hero);
                    if (playbacks.Length == 0 || playbacks[0].Timeline == 0)
                        continue;
                    int tick = playbacks[0].Tick;
                    var health = entities.GetComponentData<Health>(hero);
                    var attack = entities.GetComponentData<Attack>(hero);
                    mostBonus = Mathf.Max(mostBonus, attack.Bonus);
                    if (tick == 199 || tick == 449 || tick == 599)
                        seen[tick] = (health.Value, attack.Value);
                }
                string Show(int tick) => seen.TryGetValue(tick, out var v) ? v.Health + "/" + v.Attack : "none";
                Debug.Log(string.Format(CultureInfo.InvariantCulture, "TL-DEMO stats health200={0} health450={1} health600={2} mostBonus={3} problems={4} {5}",
                    Show(199), Show(449), Show(599), mostBonus, problems, firstProblem));
                Assert.AreEqual((64, 20), seen[199], "step 200: three hits of 12, haste on");
                Assert.AreEqual((53, 20), seen[449], "step 450: poison every 20 ticks, haste restarted at 250");
                Assert.AreEqual((72, 10), seen[599], "step 600: healed 20, haste over");
                Assert.AreEqual(10, mostBonus, "restarting haste never stacks the bonus");
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
