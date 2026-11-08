#if UNITY_EDITOR
using System.Collections;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Tl.Unity;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace EventsDemo.Tests
{
    public class EventsDemoTests
    {
        const int Steps = 600;
        const int ExpectedStrikes = 15;
        const int ExpectedHealth = 10;

        [UnityTest]
        public IEnumerator FifteenStrikesInTenSecondsLeaveTheDummyAtTen()
        {
            var scene = ScenePath("Events Demo");
            Assert.IsNotNull(scene, "Build the demo first: Tl > Samples > Build Events Demo.");
            var warnings = 0;
            var errors = 0;
            var firstProblem = "";
            void Count(string message, string stack, LogType type)
            {
                if (type == LogType.Warning)
                    warnings++;
                else if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    errors++;
                else
                    return;
                if (firstProblem.Length == 0)
                    firstProblem = message;
            }
            Application.logMessageReceived += Count;
            try
            {
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new LoadSceneParameters(LoadSceneMode.Single));
                var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
                using var knights = entities.CreateEntityQuery(ComponentType.ReadOnly<Score>(), ComponentType.ReadOnly<Attacker>());
                using var dummies = entities.CreateEntityQuery(ComponentType.ReadOnly<DummyHealth>(), ComponentType.ReadOnly<Wounds>(), ComponentType.ReadOnly<Playback>());
                using var sparks = entities.CreateEntityQuery(ComponentType.ReadOnly<Spark>());
                var deadline = Time.realtimeSinceStartup + 120f;
                while ((knights.IsEmpty || dummies.IsEmpty) && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.IsFalse(knights.IsEmpty, "the Knight with Arena Attacker appears after the SubScene loads");
                Assert.IsFalse(dummies.IsEmpty, "the Dummy with its Hurt timeline appears; " + Describe(entities));
                var knight = knights.GetSingletonEntity();
                var dummy = dummies.GetSingletonEntity();
                var sawSpark = false;
                var sawHurt = false;
                var mismatch = -1;
                var shot = "none";
                deadline = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    var score = entities.GetComponentData<Score>(knight);
                    if (!sawSpark && !sparks.IsEmpty)
                        shot = Shot("EventsDemo.png");
                    sawSpark |= !sparks.IsEmpty;
                    foreach (var playback in entities.GetBuffer<Playback>(dummy))
                        sawHurt |= playback.Timeline != 0;
                    var health = entities.GetComponentData<DummyHealth>(dummy).Max - entities.GetComponentData<Wounds>(dummy).Taken;
                    if (mismatch < 0 && health != 100 - 6 * score.Strikes && health != 100 - 6 * (score.Strikes - 1))
                        mismatch = score.Ticks;
                    if (score.Ticks >= Steps)
                        break;
                }
                var final = entities.GetComponentData<Score>(knight);
                var finalHealth = entities.GetComponentData<DummyHealth>(dummy).Max - entities.GetComponentData<Wounds>(dummy).Taken;
                Debug.Log(string.Format(CultureInfo.InvariantCulture, "TL-DEMO events strikes={0} health={1} hurt={2} sparks={3} ticks={4} sawSpark={5} sawHurt={6} healthMismatchAt={7} warnings={8} errors={9} shot={10}",
                    final.Strikes, finalHealth, final.Hurts, final.Sparks, final.Ticks, sawSpark, sawHurt, mismatch, warnings, errors, shot));
                Assert.That(final.Ticks, Is.InRange(Steps, Steps + 19), "the test read the numbers within the first 20 ticks after step 600");
                Assert.AreEqual(ExpectedStrikes, final.Strikes, "Combo loops every 120 ticks with strikes at 20, 50 and 80: 5 loops, 15 strikes");
                Assert.AreEqual(ExpectedHealth, finalHealth, "each strike sends 6 damage to the dummy: 100 - 15 x 6");
                Assert.AreEqual(ExpectedStrikes, final.Hurts, "every strike starts Hurt on the dummy");
                Assert.AreEqual(ExpectedStrikes, final.Sparks, "every strike spawns a spark");
                Assert.IsTrue(sawSpark, "spark entities were alive at some frame");
                Assert.IsTrue(sawHurt, "the dummy played Hurt at some frame");
                Assert.AreEqual(-1, mismatch, "health always equals 100 - 6 x strikes, one tick late at most");
                Assert.AreEqual(0, warnings, "no warnings while the demo plays: " + firstProblem);
                Assert.AreEqual(0, errors, "no errors while the demo plays: " + firstProblem);
            }
            finally
            {
                Application.logMessageReceived -= Count;
            }
        }

        static string Shot(string file)
        {
            var camera = Camera.main;
            if (camera == null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                return "none";
            var target = RenderTexture.GetTemporary(960, 540, 24);
            var previous = RenderTexture.active;
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            var image = new Texture2D(960, 540, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 960, 540), 0, 0);
            image.Apply();
            camera.targetTexture = null;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            var path = Path.GetFullPath(Path.Combine("Logs", file));
            File.WriteAllBytes(path, image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
            return path;
        }

        static string Describe(EntityManager entities)
        {
            using var health = entities.CreateEntityQuery(ComponentType.ReadOnly<DummyHealth>());
            using var all = health.ToEntityArray(Unity.Collections.Allocator.Temp);
            var text = "entities with DummyHealth: " + all.Length;
            foreach (var entity in all)
            {
                text += " [";
                using var types = entities.GetComponentTypes(entity, Unity.Collections.Allocator.Temp);
                foreach (var type in types)
                    text += type.GetManagedType().Name + " ";
                text += "]";
            }
            return text;
        }

        static string ScenePath(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:SceneAsset " + name))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == name)
                    return path;
            }
            return null;
        }
    }
}
#endif
