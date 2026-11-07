#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Tl.Unity;
using Unity.Collections;
using Unity.Entities;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace KindGame
{
    public class KindSceneTests
    {
        const string Scene = "Packages/com.tl.unity/Tests/Kinds/Scenes/KindsRunner.unity";

        static List<Entity> Players(EntityManager entities)
        {
            using var query = entities.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<TimelineRequest>() }, Options = EntityQueryOptions.IncludeDisabledEntities });
            using var found = query.ToEntityArray(Allocator.Temp);
            return new List<Entity>(found.ToArray());
        }

        static Entity Playing(EntityManager entities, List<Entity> players, ushort timeline)
        {
            foreach (var entity in players)
                foreach (var playback in entities.GetBuffer<Playback>(entity))
                    if (playback.Timeline == timeline)
                        return entity;
            return Entity.Null;
        }

        static string KindsOf(EntityManager entities, Entity entity, KindRegistry registry)
        {
            var names = new List<string>();
            for (var k = 0; k < registry.Count; k++)
                if (entities.HasComponent(entity, ComponentType.ReadWrite(registry[k].Entry)))
                    names.Add(registry[k].Name);
            names.Sort(System.StringComparer.Ordinal);
            return string.Join("+", names);
        }

        [UnityTest]
        public IEnumerator TheRunnerSceneBakesExactKindSetsAndPlaysThemInTheDefaultWorld()
        {
            using var scan = new LogScan();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(Scene, new LoadSceneParameters(LoadSceneMode.Single));
            var world = World.DefaultGameObjectInjectionWorld;
            var entities = world.EntityManager;
            var players = Players(entities);
            var deadline = Time.realtimeSinceStartup + 120f;
            while (players.Count < 5 && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                players = Players(entities);
            }
            Assert.AreEqual(5, players.Count, "players streamed in from the subscene");
            var registry = KindRegistry.Discover();
            var sets = new List<string>();
            foreach (var entity in players)
            {
                sets.Add(KindsOf(entities, entity, registry));
                foreach (var type in entities.GetComponentTypes(entity, Allocator.Temp))
                    StringAssert.DoesNotContain("TimelineKindBake", type.GetManagedType().Name, "no baking-only data reaches the game");
            }
            sets.Sort(System.StringComparer.Ordinal);
            CollectionAssert.AreEqual(new[] { "Damage", "Lift+Locomotion+StatChange", "Locomotion", "Locomotion", "Locomotion" }, sets);

            var walkerEntity = Entity.Null;
            var jumperEntity = Entity.Null;
            var brawlerEntity = Entity.Null;
            deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                walkerEntity = Playing(entities, players, Timelines.Run);
                if (walkerEntity == Entity.Null)
                    continue;
                if (jumperEntity == Entity.Null)
                    jumperEntity = Playing(entities, players, Timelines.Jump);
                if (brawlerEntity == Entity.Null)
                    brawlerEntity = Playing(entities, players, Timelines.Stomp);
                if (entities.GetComponentData<Movement_MotionData>(walkerEntity).Value.X >= 20f && entities.GetComponentData<Vitals_HealthData>(brawlerEntity == Entity.Null ? walkerEntity : brawlerEntity).Value.Value < 1000)
                    break;
            }
            Assert.AreNotEqual(Entity.Null, walkerEntity, "a player runs Run");
            Assert.AreNotEqual(Entity.Null, jumperEntity, "a player runs Jump");
            Assert.AreNotEqual(Entity.Null, brawlerEntity, "a player runs Stomp");
            Assert.AreEqual("Locomotion", KindsOf(entities, walkerEntity, registry));
            Assert.AreEqual("Lift+Locomotion+StatChange", KindsOf(entities, jumperEntity, registry));
            Assert.AreEqual("Damage", KindsOf(entities, brawlerEntity, registry));
            var walker = entities.GetComponentData<Movement_MotionData>(walkerEntity).Value;
            Assert.GreaterOrEqual(walker.X, 20f, "Run moves the walker");
            Assert.AreEqual(0f, walker.X % 2f, "Run adds 2 per step");
            Assert.Greater(entities.GetComponentData<Movement_MotionData>(jumperEntity).Value.Y, 0f, "Jump lifts the jumper");
            Assert.Less(entities.GetComponentData<Vitals_HealthData>(brawlerEntity).Value.Value, 1000, "Stomp hits the brawler");
            Debug.Log("Tl kind scene: walker X=" + walker.X + " jumper Y=" + entities.GetComponentData<Movement_MotionData>(jumperEntity).Value.Y + " brawler health=" + entities.GetComponentData<Vitals_HealthData>(brawlerEntity).Value.Value);

            var rows = KindReport.Capture(entities, registry);
            Debug.Log(KindReport.Text(world.Name, rows));
            Assert.AreEqual(3, rows.Count);
            Assert.AreEqual(3, rows.Find(r => r.Kinds == "Locomotion").Entities);
            Assert.AreEqual(1, rows.Find(r => r.Kinds == "Lift+Locomotion+StatChange").Entities);
            Assert.AreEqual(1, rows.Find(r => r.Kinds == "Damage").Entities);
            CollectionAssert.IsEmpty(KindReport.Warnings(rows));
            Assert.AreEqual("", scan.Report(), "warnings or errors logged during the steps");
        }
    }
}
#endif
