using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Tl.Unity;
using Tl.Unity.Editor;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace KindGame
{
    public class KindBakingTests
    {
        readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var made in _made)
                if (made != null)
                    Object.DestroyImmediate(made);
            _made.Clear();
        }

        TimelineLibraryAsset Library(KindSets sets = KindSets.Exact)
        {
            var library = ScriptableObject.CreateInstance<TimelineLibraryAsset>();
            library.name = "KindTestLibrary";
            library.Folder = "Packages/com.tl.unity/Tests/Kinds/Timelines~";
            library.TimelineIds = "KindGame.Timelines";
            library.GameAssembly = "Tl.Unity.Kinds";
            library.Namespace = "KindGame";
            library.KindSets = sets;
            _made.Add(library);
            return library;
        }

        GameObject Player(string name, TimelineLibraryAsset library, string[] startWith, bool columns = true, string[] mayStart = null, string[] kinds = null, KindProfile profile = null, bool damageable = false)
        {
            var gameObject = new GameObject(name);
            _made.Add(gameObject);
            var player = gameObject.AddComponent<TimelinePlayerAuthoring>();
            player.Library = library;
            player.StartWith = startWith ?? Array.Empty<string>();
            player.MayStart = mayStart ?? Array.Empty<string>();
            player.Kinds = kinds ?? Array.Empty<string>();
            player.Profile = profile;
            if (columns)
                gameObject.AddComponent<RunnerAuthoring>();
            if (damageable)
                gameObject.AddComponent<DamageableAuthoring>();
            return gameObject;
        }

        static World Bake(params GameObject[] roots)
        {
            var hybrid = typeof(Baker<>).Assembly;
            var utility = hybrid.GetType("Unity.Entities.BakingUtility", true);
            var flags = Enum.Parse(utility.GetNestedType("BakingFlags", BindingFlags.Public | BindingFlags.NonPublic), "AssignName");
            var world = new World("TlKindBakingTest");
            var blobs = new BlobAssetStore(128);
            try
            {
                var settings = Activator.CreateInstance(hybrid.GetType("Unity.Entities.BakingSettings", true), flags, blobs);
                utility.GetMethod("BakeGameObjects", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { world, roots, settings });
            }
            finally
            {
                blobs.Dispose();
            }
            return world;
        }

        static Entity Find(World world, string name)
        {
            var entities = world.EntityManager;
            using var all = entities.GetAllEntities(Allocator.Temp);
            foreach (var entity in all)
                if (entities.GetName(entity) == name && entities.HasBuffer<TimelineRequest>(entity))
                    return entity;
            Assert.Fail("No baked entity named '" + name + "'.");
            return Entity.Null;
        }

        static string KindsOf(World world, Entity entity)
        {
            var registry = TimelineKindSets.Registry;
            var names = new List<string>();
            for (var k = 0; k < registry.Count; k++)
            {
                if (!world.EntityManager.HasComponent(entity, ComponentType.ReadWrite(registry[k].Entry)))
                    continue;
                names.Add(registry[k].Name);
                Assert.IsTrue(world.EntityManager.HasComponent(entity, ComponentType.ReadWrite(registry[k].Active)), registry[k].Name + " Active");
                Assert.IsFalse(world.EntityManager.IsComponentEnabled(entity, ComponentType.ReadWrite(registry[k].Active)), registry[k].Name + " Active starts disabled");
                Assert.IsTrue(world.EntityManager.IsComponentEnabled(entity, ComponentType.ReadWrite(registry[k].Enabled)), registry[k].Name + " Enabled starts enabled");
            }
            names.Sort(StringComparer.Ordinal);
            return string.Join("+", names);
        }

        [Test]
        public void BakesEachPlayerWithExactlyTheKindsItsTimelinesAndAuthoringReach()
        {
            var library = Library();
            var walker = Player("Walker", library, new[] { "Run" });
            var vaulter = Player("Vaulter", library, new[] { "Vault" });
            var hopper = Player("Hopper", library, new[] { "PotionHop" });
            var brawler = Player("Brawler", library, new[] { "Run" }, mayStart: new[] { "Jump" }, damageable: true);
            var target = Player("Target", library, null, kinds: new[] { "Damage" });
            using var world = Bake(walker, vaulter, hopper, brawler, target);
            Assert.AreEqual("Locomotion", KindsOf(world, Find(world, "Walker")));
            Assert.AreEqual("Locomotion", KindsOf(world, Find(world, "Vaulter")));
            Assert.AreEqual("Lift", KindsOf(world, Find(world, "Hopper")));
            Assert.AreEqual("Damage+Lift+Locomotion+StatChange", KindsOf(world, Find(world, "Brawler")));
            Assert.AreEqual("Damage", KindsOf(world, Find(world, "Target")));
            var entities = world.EntityManager;
            var walking = Find(world, "Walker");
            Assert.IsTrue(entities.HasBuffer<Playback>(walking));
            Assert.IsFalse(entities.IsComponentEnabled<TimelinePlaying>(walking));
            var requests = entities.GetBuffer<TimelineRequest>(walking);
            Assert.AreEqual(1, requests.Length);
            Assert.AreEqual(Timelines.Run, requests[0].Timeline);
            Assert.AreEqual(0, entities.GetBuffer<TimelineRequest>(Find(world, "Target")).Length);
        }

        [Test]
        public void ANextChainReachesTheFollowingTimeline()
        {
            var library = Library();
            var vaulter = Player("Vaulter", library, new[] { "Vault" }).GetComponent<TimelinePlayerAuthoring>();
            var plan = TimelineKindSets.Plan(vaulter, new[] { typeof(RunnerAuthoring) }, TimelineKindSets.Registry);
            CollectionAssert.IsEmpty(plan.Errors);
            CollectionAssert.AreEqual(new[] { Timelines.Vault, Timelines.Waltz }, plan.Timelines);
            CollectionAssert.AreEqual(new[] { Timelines.Vault }, plan.Starts);
        }

        [Test]
        public void AProfileGivesEveryMemberItsUnion()
        {
            var library = Library(KindSets.UnionPerProfile);
            var profile = ScriptableObject.CreateInstance<KindProfile>();
            profile.name = "Runners";
            profile.Kinds = new[] { "Locomotion", "Lift" };
            _made.Add(profile);
            var walker = Player("Walker", library, new[] { "Run" }, profile: profile);
            var hopper = Player("Hopper", library, new[] { "PotionHop" }, profile: profile);
            using var world = Bake(walker, hopper);
            Assert.AreEqual("Lift+Locomotion", KindsOf(world, Find(world, "Walker")));
            Assert.AreEqual("Lift+Locomotion", KindsOf(world, Find(world, "Hopper")));
            Assert.AreEqual(world.EntityManager.GetChunk(Find(world, "Walker")).Archetype, world.EntityManager.GetChunk(Find(world, "Hopper")).Archetype);
        }

        [Test]
        public void AMissingColumnIsABakingErrorNamingGameObjectKindTimelineTrackAndComponent()
        {
            var library = Library();
            var bare = Player("Bare", library, new[] { "Run" }, columns: false);
            foreach (var column in new[] { "Movement_MotionData", "Movement_TargetData", "Movement_WalkStatData" })
                LogAssert.Expect(LogType.Error, new Regex("GameObject 'Bare' gets kind 'Locomotion' from timeline 'Run', fed by Walk\\.Evaluate, but has no '" + column + "' component"));
            using var world = Bake(bare);
        }

        [Test]
        public void AnUnknownTimelineOrKindNameIsABakingError()
        {
            var library = Library();
            var typo = Player("Typo", library, new[] { "Rnu" }, kinds: new[] { "Damag" });
            LogAssert.Expect(LogType.Error, new Regex("GameObject 'Typo' start-with names timeline 'Rnu', which KindGame\\.Timelines does not declare"));
            LogAssert.Expect(LogType.Error, new Regex("GameObject 'Typo' the Kinds list names kind 'Damag', which no Unity system runs"));
            using var world = Bake(typo);
            Assert.AreEqual(string.Empty, KindsOf(world, Find(world, "Typo")));
        }

        [Test]
        public void TheReportCountsTheChunksAndEntitiesUnityMeasures()
        {
            using var world = new World("TlKindReportTest");
            var entities = world.EntityManager;
            var walkers = Spawn(entities, 300, LocomotionKind.Types, DamageKind.Types);
            var jumpers = Spawn(entities, 50, LocomotionKind.Types, DamageKind.Types, LiftKind.Types, StatChangeKind.Types);
            var rows = KindReport.Capture(entities, TimelineKindSets.Registry);
            Assert.AreEqual(2, rows.Count);
            Check(entities, rows, "Damage+Locomotion", walkers, new ComponentType[] { typeof(LiftEntry) }, 300);
            Check(entities, rows, "Damage+Lift+Locomotion+StatChange", jumpers, Array.Empty<ComponentType>(), 50);
            CollectionAssert.IsEmpty(KindReport.Warnings(rows));
            Debug.Log(KindReport.Text(world.Name, rows));
        }

        [Test]
        public void TheReportWarnsAboutManyKindsLargeStorageAndThinChunks()
        {
            var rows = new[]
            {
                new KindArchetypeRow { Kinds = "fine", KindCount = 4, KindBytes = 200, ChunkCapacity = 64 },
                new KindArchetypeRow { Kinds = "wide", KindCount = 33, KindBytes = 1025, ChunkCapacity = 15 },
            };
            var warnings = KindReport.Warnings(rows);
            Assert.AreEqual(3, warnings.Count);
            StringAssert.StartsWith("wide: 33 kinds", warnings[0]);
            StringAssert.StartsWith("wide: 1025 B", warnings[1]);
            StringAssert.StartsWith("wide: 15 entities per chunk", warnings[2]);
        }

        static ComponentType[] Spawn(EntityManager entities, int count, params ComponentType[][] kinds)
        {
            var types = new List<ComponentType> { typeof(Playback), typeof(TimelineRequest), typeof(Movement_MotionData), typeof(Vitals_HealthData) };
            foreach (var kind in kinds)
                types.AddRange(kind);
            var archetype = entities.CreateArchetype(types.ToArray());
            using var made = entities.CreateEntity(archetype, count, Allocator.Temp);
            return types.ToArray();
        }

        static void Check(EntityManager entities, List<KindArchetypeRow> rows, string kinds, ComponentType[] types, ComponentType[] none, int expected)
        {
            var row = rows.Find(r => r.Kinds == kinds);
            Assert.AreEqual(kinds, row.Kinds);
            using var query = entities.CreateEntityQuery(new EntityQueryDesc { All = types, None = none, Options = EntityQueryOptions.IncludeDisabledEntities });
            using var chunks = query.ToArchetypeChunkArray(Allocator.Temp);
            Assert.AreEqual(expected, row.Entities);
            Assert.AreEqual(query.CalculateEntityCount(), row.Entities);
            Assert.AreEqual(chunks.Length, row.Chunks);
            Assert.AreEqual(chunks[0].Capacity, row.ChunkCapacity);
            Assert.AreEqual((expected + row.ChunkCapacity - 1) / row.ChunkCapacity, row.Chunks);
        }

        [Test]
        public void EditingATimelineWithoutChangingACapacityRewritesNothing()
        {
            var tlb = Environment.GetEnvironmentVariable("TL_TLB");
            if (string.IsNullOrEmpty(tlb))
                Assert.Ignore("Set TL_TLB to a tlb with the kinds command (or its Tl.Bake.dll) to run the capacity bake.");
            var root = Path.GetFullPath("Temp/TlKindBakeTest");
            var folder = Path.Combine(root, "library");
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            Directory.CreateDirectory(folder);
            foreach (var file in Directory.GetFiles(TimelineKindSets.PhysicalPath("Packages/com.tl.unity/Tests/Kinds/Timelines~"), "*.json"))
                if (Path.GetFileName(file) != "run_legacy.json")
                    File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            var libraryJson = Path.Combine(folder, "library.json");
            File.WriteAllText(libraryJson, File.ReadAllText(libraryJson).Replace("\"run_legacy.json\",", string.Empty));
            var output = Path.Combine(root, "Kinds.g.cs");
            var assembly = KindCapacityBake.Stage("Tl.Unity.Kinds");

            var first = KindCapacityBake.Run(tlb, folder, "KindGame", output, assembly);
            Assert.IsTrue(first.kindsWritten);
            Assert.AreEqual(KindCapacity.Damage, Capacity(first, "Damage"));
            Assert.AreEqual(KindCapacity.Lift, Capacity(first, "Lift"));
            Assert.AreEqual(KindCapacity.Locomotion, Capacity(first, "Locomotion"));
            Assert.AreEqual(KindCapacity.StatChange, Capacity(first, "StatChange"));
            var stamp = File.GetLastWriteTimeUtc(output);
            var text = File.ReadAllText(output);

            var run = Path.Combine(folder, "run.json");
            var edited = File.ReadAllText(run).Replace("\"Speed\": 2", "\"Speed\": 3");
            Assert.AreNotEqual(File.ReadAllText(run), edited);
            File.WriteAllText(run, edited);
            var second = KindCapacityBake.Run(tlb, folder, "KindGame", output, assembly);
            Assert.IsFalse(second.kindsWritten);
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(output));
            Assert.AreEqual(text, File.ReadAllText(output));

            File.WriteAllText(libraryJson, File.ReadAllText(libraryJson).Replace("\"gamePulses\": 8", "\"gamePulses\": 2"));
            var third = KindCapacityBake.Run(tlb, folder, "KindGame", output, assembly);
            Assert.IsTrue(third.kindsWritten);
            Assert.Less(Capacity(third, "Damage"), KindCapacity.Damage);
            Assert.AreNotEqual(text, File.ReadAllText(output));
        }

        static int Capacity(KindCapacityBake.Result result, string kind)
        {
            foreach (var row in result.kinds)
                if (row.name == kind)
                    return row.capacity;
            Assert.Fail("tlb reported no kind '" + kind + "'.");
            return -1;
        }
    }
}
