#if UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace CrowdDemo.Tests
{
    public class CrowdBenchTests
    {
        const int Steps = 200;
        const double SaneNsPerEntity = 100;

        [UnityTest]
        public IEnumerator TenThousandAndAHundredThousandDancersStepWithoutAllocating()
        {
            var scene = ScenePath("Crowd Demo");
            Assert.IsNotNull(scene, "Build the demo first: Tl > Samples > Build Crowd Demo.");
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new LoadSceneParameters(LoadSceneMode.Single));
            var world = World.DefaultGameObjectInjectionWorld;
            var entities = world.EntityManager;
            using var crowds = entities.CreateEntityQuery(ComponentType.ReadWrite<Crowd>());
            using var dancers = entities.CreateEntityQuery(ComponentType.ReadOnly<Dancer>(), ComponentType.ReadOnly<Playback>(), ComponentType.ReadOnly<DancePose>());
            var deadline = Time.realtimeSinceStartup + 120f;
            while (crowds.IsEmpty && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsFalse(crowds.IsEmpty, "the Crowd entity appears after the SubScene loads");
            var group = world.GetExistingSystemManaged<global::Tl.TlSimulationGroup>();
            var lines = "";
            foreach (var count in new[] { 10000, 100000 })
            {
                crowds.CompleteDependency();
                var crowd = crowds.GetSingleton<Crowd>();
                crowd.Count = count;
                crowds.SetSingleton(crowd);
                var playing = 0;
                deadline = Time.realtimeSinceStartup + 120f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    playing = Playing(entities, dancers);
                    if (dancers.CalculateEntityCount() == count && playing == count)
                        break;
                }
                for (var i = 0; i < 30; i++)
                    yield return null;
                Shot("CrowdDemo-" + count + ".png");
                Measure(group, entities, Steps, count, out _, out _);
                Measure(group, entities, Steps, count, out var ns, out var allocated);
                var line = string.Format(CultureInfo.InvariantCulture, "TL-BENCH crowd entities={0} ns/entity={1:0.0} warmB={2} playing={3} steps={4} burst={5} workers={6}",
                    count, ns, allocated, playing, Steps, BurstCompiler.IsEnabled, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount);
                Debug.Log(line);
                lines += line + "\n";
                Assert.AreEqual(count, dancers.CalculateEntityCount(), "the crowd spawned exactly " + count + " dancers");
                Assert.AreEqual(count, playing, "every dancer plays Dance");
                Assert.AreEqual(0, allocated, "a warm Tl step allocates nothing");
                Assert.Less(ns, SaneNsPerEntity, "ns per entity stays in a sane range");
            }
            File.WriteAllText(Path.GetFullPath(Path.Combine("Logs", "CrowdBench.txt")), lines);
        }

        static void Measure(ComponentSystemGroup group, EntityManager entities, int steps, int count, out double ns, out long allocated)
        {
            entities.CompleteAllTrackedJobs();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < steps; i++)
            {
                group.Update();
                entities.CompleteAllTrackedJobs();
            }
            watch.Stop();
            var after = GC.GetAllocatedBytesForCurrentThread();
            allocated = after - before;
            ns = watch.Elapsed.TotalMilliseconds * 1000000.0 / ((double)count * steps);
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

        static int Playing(EntityManager entities, EntityQuery dancers)
        {
            using var all = dancers.ToEntityArray(Allocator.Temp);
            var playing = 0;
            foreach (var dancer in all)
                foreach (var playback in entities.GetBuffer<Playback>(dancer, true))
                    if (playback.Timeline != 0)
                    {
                        playing++;
                        break;
                    }
            return playing;
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
