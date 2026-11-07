#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Tl.Unity;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Demo.Tests
{
    public class DemoPlayTests
    {
        static string ScenePath()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:SceneAsset Demo"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == "Demo")
                    return path;
            }
            return null;
        }

        [UnityTest]
        public IEnumerator TheDemoRunsInTheDefaultWorld()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath(), new LoadSceneParameters(LoadSceneMode.Single));
            var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var clockQuery = entities.CreateEntityQuery(typeof(DemoClock));
            using var walkers = entities.CreateEntityQuery(typeof(LocomotionEntry), typeof(Vitals_HealthData));
            using var dummies = entities.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Vitals_HealthData>() }, None = new[] { ComponentType.ReadOnly<LocomotionEntry>() } });
            var seen = new Dictionary<int, float>();
            var deadline = Time.realtimeSinceStartup + 120f;
            var step = 0;
            while (Time.realtimeSinceStartup < deadline && step < 70)
            {
                yield return null;
                if (clockQuery.IsEmpty || walkers.IsEmpty)
                    continue;
                step = clockQuery.GetSingleton<DemoClock>().Step;
                using var found = walkers.ToEntityArray(Allocator.Temp);
                seen[step] = entities.GetComponentData<Movement_MotionData>(found[0]).Value.X;
            }
            Assert.GreaterOrEqual(step, 70, "the director reached step 70");
            using var w = walkers.ToEntityArray(Allocator.Temp);
            using var d = dummies.ToEntityArray(Allocator.Temp);
            Assert.AreEqual(1, w.Length);
            Assert.AreEqual(1, d.Length);
            var walkerHealth = entities.GetComponentData<Vitals_HealthData>(w[0]).Value.Value;
            var dummyHealth = entities.GetComponentData<Vitals_HealthData>(d[0]).Value.Value;
            var cues = entities.GetComponentData<Vitals_CuesData>(w[0]).Value;
            using var tickQuery = entities.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<TimelineKindTick>() }, Options = EntityQueryOptions.IncludeSystems });
            var tick = tickQuery.GetSingleton<TimelineKindTick>();
            seen.TryGetValue(30, out var before);
            seen.TryGetValue(50, out var after);
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "EcsDemo result walkerHealth={0} dummyHealth={1} cues={2} lastCue={3} x30={4} x50={5} droppedPulses={6} droppedStarts={7}",
                walkerHealth, dummyHealth, cues.Count, cues.Last, before, after, tick.DroppedPulses, tick.DroppedStarts));
            Assert.AreEqual(100 - 25 - 10 - 7, walkerHealth, "stomp hits and one game pulse");
            Assert.AreEqual(100 - 7, dummyHealth, "game pulse only");
            Assert.AreEqual(2, cues.Count);
            Assert.AreEqual(2, cues.Last);
            Assert.Greater(entities.GetComponentData<Movement_MotionData>(w[0]).Value.X, 0f);
            Assert.IsTrue(seen.ContainsKey(30) && seen.ContainsKey(50), "steps 30 and 50 were both observed");
            Assert.AreEqual(before, after, "ten steps back return X to its value ten steps earlier");
            Assert.AreEqual(0, tick.DroppedPulses);
            Assert.AreEqual(0, tick.DroppedStarts);
        }
    }
}
#endif
