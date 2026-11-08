using System;
using System.Reflection;
using NUnit.Framework;
using Tl.Generated;
using Tl.Pack.Motion;
using Tl.Pack.Stats;
using Tl.Unity;
using Tl.Unity.Editor;
using Pose = Tl.Pack.Motion.Pose;
using TlPacks.Stats;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
#if TL_URP
using Unity.Rendering;
#endif
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace TlPacks.Tests
{
    public class PackAdapterTests
    {
        GameObject _made;
        TimelineAsset _timeline;

        [TearDown]
        public void TearDown()
        {
            if (_made != null)
                UnityEngine.Object.DestroyImmediate(_made);
            if (_timeline != null)
                UnityEngine.Object.DestroyImmediate(_timeline);
        }

        [Test]
        public void EveryPackClipGetsATimelineMenuEntry()
        {
            var wanted = TlMenuStubFiles.Wanted();
            foreach (var stub in new[] { "Tl_Pack_Motion_MoveClip_TlMenu.cs", "Tl_Pack_Motion_ScaleClip_TlMenu.cs", "Tl_Pack_Motion_SpinClip_TlMenu.cs", "Tl_Pack_Motion_TintClip_TlMenu.cs", "Tl_Pack_Stats_HitClip_TlMenu.cs", "Tl_Pack_Stats_HealClip_TlMenu.cs", "Tl_Pack_Stats_PoisonClip_TlMenu.cs", "Tl_Pack_Stats_BuffClip_TlMenu.cs", "Tl_Pack_Inventory_GiveClip_TlMenu.cs", "Tl_Pack_Inventory_CraftClip_TlMenu.cs", "Tl_Pack_Force_ForceClip_TlMenu.cs" })
                Assert.IsTrue(wanted.ContainsKey(stub), stub);
            StringAssert.Contains("\"Motion/Tint\"", wanted["Tl_Pack_Motion_TintClip_TlMenu.cs"]);
            StringAssert.Contains("TlMenuMarker", wanted["Tl_Pack_Inventory_PickupMarker_TlMarkerMenu.cs"]);
        }

        [Test]
        public void SameNamedClipsInTwoNamespacesGetTwoMirrors()
        {
            var pack = Type.GetType("Tl.Generated.Tl_Pack_Force_ForceClip_TlData, Tl.Pack.Force", true);
            var game = Type.GetType("Tl.Generated.ForceClip_TlData, Tl.Unity.ForceContract", true);
            Assert.AreEqual("Tl.Pack.Force.ForceClip", ((TlClipData)Activator.CreateInstance(pack)).ClipType.FullName);
            Assert.AreEqual("ForceClip", ((TlClipData)Activator.CreateInstance(game)).ClipType.FullName);
        }

        [Test]
        public void AScaleAndTintTimelineBakesPoseTintIdentityScaleAndWhite()
        {
            _timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            _timeline.name = "Breathe";
            var scale = _timeline.CreateTrack<TlTrack>(null, "Scale");
            scale.Template = new Tl_Pack_Motion_ScaleClip_TlData();
            var clip = scale.CreateClip<TlClip>();
            clip.start = 0;
            clip.duration = 1;
            var tint = _timeline.CreateTrack<TlTrack>(null, "Tint");
            tint.Template = new Tl_Pack_Motion_TintClip_TlData();
            clip = tint.CreateClip<TlClip>();
            clip.start = 0;
            clip.duration = 1;
            _made = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var director = _made.AddComponent<PlayableDirector>();
            director.playableAsset = _timeline;
            using var baked = new Baked(_made);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            Assert.IsTrue(entities.HasComponent<Pose>(entity));
            Assert.IsTrue(entities.HasComponent<Tint>(entity));
            Assert.AreEqual(float4x4.identity, entities.GetComponentData<PostTransformMatrix>(entity).Value);
#if TL_URP
            Assert.AreEqual(new float4(1f, 1f, 1f, 1f), entities.GetComponentData<URPMaterialPropertyBaseColor>(entity).Value);
#endif
        }

        [Test]
        public void TlStatsKeepsTheDesignersStartingValues()
        {
            _timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            _timeline.name = "Duel";
            var hits = _timeline.CreateTrack<TlTrack>(null, "Hit");
            hits.Template = new Tl_Pack_Stats_HitClip_TlData();
            var clip = hits.CreateClip<TlClip>();
            clip.start = 0;
            clip.duration = 1;
            _made = new GameObject("Hero");
            var director = _made.AddComponent<PlayableDirector>();
            director.playableAsset = _timeline;
            var stats = _made.AddComponent<TlStatsAuthoring>();
            stats.Health = 80;
            stats.MaxHealth = 120;
            stats.Attack = 7;
            using var baked = new Baked(_made);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            var health = entities.GetComponentData<Health>(entity);
            Assert.AreEqual(80, health.Value);
            Assert.AreEqual(120, health.Max);
            Assert.AreEqual(7, entities.GetComponentData<Attack>(entity).Base);
        }

        [Test]
        public void TlBagKeepsTheDesignersStartingItemsAndAPickupMarkerReachesTheBag()
        {
            _timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            _timeline.name = "Loot";
            _timeline.CreateMarkerTrack();
            var pickup = _timeline.markerTrack.CreateMarker<TlMarker>(0.5);
            pickup.Data = new Tl_Pack_Inventory_PickupMarker_TlMarkerData { Item = Tl.Pack.Inventory.Item.Coin, Count = 10 };
            _made = new GameObject("Backpack");
            var director = _made.AddComponent<PlayableDirector>();
            director.playableAsset = _timeline;
            var bag = _made.AddComponent<TlPacks.Inventory.TlBagAuthoring>();
            bag.Stone = 2;
            bag.Key = 1;
            using var baked = new Baked(_made);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            var start = entities.GetComponentData<Tl.Pack.Inventory.Bag>(entity);
            Assert.AreEqual(2, start.Stone);
            Assert.AreEqual(1, start.Key);
            Assert.AreEqual(0, start.Coin);
            Assert.IsTrue(entities.HasComponent(entity, ComponentType.ReadWrite(TimelineKindSets.Registry[TimelineKindSets.Registry.IndexOf("Tl.Pack.Inventory.InventoryItems")].Entry)), "the pickup marker reaches the InventoryItems kind");
        }

        sealed class Baked : IDisposable
        {
            public readonly World World;
            readonly BlobAssetStore _blobs;

            public Baked(params GameObject[] roots)
            {
                var hybrid = typeof(Baker<>).Assembly;
                var utility = hybrid.GetType("Unity.Entities.BakingUtility", true);
                var flags = Enum.Parse(utility.GetNestedType("BakingFlags", BindingFlags.Public | BindingFlags.NonPublic), "AssignName");
                World = new World("TlPackBakingTest");
                _blobs = new BlobAssetStore(128);
                var settings = Activator.CreateInstance(hybrid.GetType("Unity.Entities.BakingSettings", true), flags, _blobs);
                utility.GetMethod("BakeGameObjects", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { World, roots, settings });
            }

            public Entity Single<T>() where T : unmanaged, IBufferElementData
            {
                using var query = World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>());
                using var found = query.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(1, found.Length, typeof(T).Name + " entities");
                return found[0];
            }

            public void Dispose()
            {
                World.Dispose();
                _blobs.Dispose();
            }
        }
    }
}
