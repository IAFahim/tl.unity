#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ManyAtOnce;
using NUnit.Framework;
using Tl.Unity;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ManyAtOnceTests
{
    public class ManyAtOnceTests
    {
        const string SceneName = "Many At Once.unity";

        static float FloorAt(int tick) => tick < 90 ? 1f : tick < 120 ? 1f + (5f - 1f) * ((tick - 90) / 29f) : 5f;

        static float WaveAt(int tick) => tick >= 30 && tick < 60 ? 0.5f : 0f;

        static float ShakeAt(int tick) => tick >= 15 && tick < 30 ? 0.25f : 0f;

        static float ExpectedHeight(int tick) => FloorAt(tick) + WaveAt(tick) + ShakeAt(tick);

        [UnityTest]
        public IEnumerator ThePlatformRidesUpThroughTheBlendWhileTheWaveTrackAndTheShakeTimelineAddToIt()
        {
            var scene = ScenePath();
            Assert.IsNotNull(scene, "Build the sample first: Tl Many At Once > Build Scene.");
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new LoadSceneParameters(LoadSceneMode.Single));
            var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var platforms = entities.CreateEntityQuery(ComponentType.ReadOnly<Platform>(), ComponentType.ReadOnly<Playback>());
            var deadline = Time.realtimeSinceStartup + 120f;
            while (platforms.IsEmpty && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsFalse(platforms.IsEmpty, "the Platform with its Playable Director appears after the SubScene loads");
            var platform = platforms.GetSingletonEntity();
            var heightAtTick = new SortedDictionary<int, float>();
            var mostPlaying = 0;
            var mismatches = new List<string>();
            deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                var ride = entities.GetBuffer<TlTimelineSet>(platform)[0].Id;
                var rideTick = -1;
                var playing = 0;
                foreach (var playback in entities.GetBuffer<Playback>(platform))
                {
                    if (playback.Timeline == 0)
                        continue;
                    playing++;
                    if (playback.Timeline == ride)
                        rideTick = playback.Tick;
                }
                if (rideTick < 0)
                {
                    if (heightAtTick.Count > 0)
                        break;
                    continue;
                }
                mostPlaying = Mathf.Max(mostPlaying, playing);
                var height = entities.GetComponentData<Platform>(platform).Height;
                heightAtTick[rideTick] = height;
                if (height != ExpectedHeight(rideTick))
                    mismatches.Add(rideTick + ": " + height.ToString("R", CultureInfo.InvariantCulture) + " expected " + ExpectedHeight(rideTick).ToString("R", CultureInfo.InvariantCulture));
            }
            var finalHeight = entities.GetComponentData<Platform>(platform).Height;
            var ridingTicks = 0;
            foreach (var tick in heightAtTick.Keys)
                if (tick > 90 && tick < 119)
                    ridingTicks++;
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "TL-DEMO many-at-once ticks={0} ridingTicks={1} mostPlaying={2} final={3} mismatches={4}", heightAtTick.Count, ridingTicks, mostPlaying, finalHeight, mismatches.Count));
            Assert.IsEmpty(mismatches, string.Join("; ", mismatches));
            Assert.AreEqual(2, mostPlaying, "Ride and Shake play at the same time");
            Assert.Greater(ridingTicks, 10, "the test saw the platform during the blend");
            Assert.AreEqual(5f, finalHeight, "after the Ride ends the platform stays on the Top clip's height");
        }

        static string ScenePath()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Scene"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) == SceneName)
                    return path;
            }
            return null;
        }
    }
}
#endif
