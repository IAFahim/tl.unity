using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Tl.Generated;
using Tl.Unity.Editor;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity.Tracks.Tests
{
    public class TlTimelineTests
    {
        readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var made in _made)
                if (made != null)
                    UnityEngine.Object.DestroyImmediate(made);
            _made.Clear();
        }

        [Test]
        public void TheMenuStubBecomesARealForceTrackInOneUndoStep()
        {
            var timeline = Timeline("Push");
            Undo.IncrementCurrentGroup();
            var stub = timeline.CreateTrack<TestForceMenu>(null, "Force");
            new TlMenuTrackEditor().OnCreate(stub, null);
            var tracks = timeline.GetOutputTracks().ToList();
            Assert.AreEqual(1, tracks.Count);
            var track = tracks[0] as TlTrack;
            Assert.IsNotNull(track, "the stub was replaced by a TlTrack");
            Assert.AreEqual("Force", track.name);
            Assert.IsInstanceOf<ForceClip_TlData>(track.Template);
            Undo.PerformUndo();
            Assert.AreEqual(0, timeline.GetOutputTracks().Count(), "one undo removes the whole add");
        }

        [Test]
        public void ANewClipGetsForceValuesAndTheForceLabel()
        {
            var track = ForceTrack(Timeline("Push"));
            var clip = track.CreateClip<TlClip>();
            Assert.AreEqual("Force", clip.displayName);
            var data = ((TlClip)clip.asset).Data as ForceClip_TlData;
            Assert.IsNotNull(data);
            Assert.AreEqual(1f, data.Falloff.Evaluate(0f), 1e-5f);
            Assert.AreEqual(0f, data.Falloff.Evaluate(1f), 1e-5f);
        }

        [Test]
        public void TheClipInspectorDrawsThePlainFieldsOfTheClip()
        {
            var track = ForceTrack(Timeline("Push"));
            var clip = track.CreateClip<TlClip>();
            var serialized = new SerializedObject(clip.asset);
            var data = serialized.FindProperty(nameof(TlClip.Data));
            var names = new List<string>();
            var end = data.GetEndProperty();
            var field = data.Copy();
            var enter = true;
            while (field.NextVisible(enter) && !SerializedProperty.EqualContents(field, end))
            {
                enter = false;
                names.Add(field.displayName);
            }
            CollectionAssert.AreEqual(new[] { "Direction", "Strength", "Falloff" }, names);
            Assert.AreEqual(typeof(TlClipEditor), EditorTypeFor(clip.asset));
        }

        [Test]
        public void TheDirectorBakerAddsEverythingTheForceTrackNeeds()
        {
            var timeline = Timeline("Push");
            Clip(ForceTrack(timeline), 0, 2, 4f);
            var director = Director("Cube", timeline);
            using var baked = new Baked(director.gameObject);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            Assert.IsTrue(entities.HasComponent<Force>(entity), "Force (ForceKind.Apply writes it)");
            Assert.IsTrue(entities.HasBuffer<ForceLog>(entity), "ForceLog (ForceBaking)");
            Assert.IsTrue(entities.HasComponent<ForceFalloff>(entity), "ForceFalloff (ForceBaking)");
            var falloff = entities.GetComponentData<ForceFalloff>(entity).Curve;
            ref var curve = ref falloff.Value;
            Assert.AreEqual(TlCurve.Samples, curve.Samples.Length);
            Assert.AreEqual(1f, curve.Samples[0], 1e-5f);
            Assert.AreEqual(0f, curve.Samples[TlCurve.Samples - 1], 1e-5f);
            var kind = TimelineKindSets.Registry[TimelineKindSets.Registry.IndexOf("ForceKind")];
            CollectionAssert.Contains(kind.Columns, typeof(Force), "the kind bake adds Force even without ForceBaking");
            Assert.IsTrue(entities.HasComponent(entity, ComponentType.ReadWrite(kind.Entry)), "ForceKind entry buffer");
            Assert.IsFalse(entities.IsComponentEnabled(entity, ComponentType.ReadWrite(kind.Active)), "ForceKind starts inactive");
            Assert.IsTrue(entities.HasBuffer<Playback>(entity));
            Assert.IsTrue(entities.HasBuffer<TimelineRequest>(entity));
            Assert.IsFalse(entities.IsComponentEnabled<TimelinePlaying>(entity));
            Assert.IsTrue(entities.IsComponentEnabled<TlUnregistered>(entity));
            Assert.IsTrue(entities.IsComponentEnabled<TlPlayOnAwake>(entity));
            var set = entities.GetBuffer<TlTimelineSet>(entity);
            Assert.AreEqual(1, set.Length);
            var reference = set[0].Blob;
            ref var blob = ref reference.Value;
            Assert.AreEqual("Push", TlTimelineBlobs.NameOf(ref blob));
            Assert.AreEqual(TlProjectSettings.instance.TicksPerSecond, blob.TickRate);
            Assert.AreEqual(60u, blob.TickRate, "the default project setting is 60 ticks per second");
            Assert.Greater(blob.Tlb.Length, 0);
            Assert.Greater(blob.Kinds.Length, 0);
        }

        [Test]
        public void ADirectorWithoutTlTracksBakesNothingOfTl()
        {
            var timeline = Timeline("Plain");
            timeline.CreateTrack<ActivationTrack>(null, "Activation");
            using var baked = new Baked(Director("Cube", timeline).gameObject);
            Assert.AreEqual(0, baked.Count<TlTimelineSet>());
        }

        [Test]
        public void TheSettingsMarkerAddsTheNextTimelineToTheSet()
        {
            var run = Timeline("Run");
            Clip(ForceTrack(run), 0, 1, 2f);
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 1, 4f);
            push.CreateMarkerTrack();
            var settings = push.markerTrack.CreateMarker<TlTimelineSettings>(0);
            settings.End = TlEnd.Next;
            settings.Next = run;
            settings.Channel = 2;
            using var baked = new Baked(Director("Cube", push).gameObject);
            var entities = baked.World.EntityManager;
            var set = entities.GetBuffer<TlTimelineSet>(baked.Single<TlTimelineSet>());
            Assert.AreEqual(2, set.Length);
            var push0 = set[0].Blob;
            var run1 = set[1].Blob;
            Assert.AreEqual("Push", TlTimelineBlobs.NameOf(ref push0.Value));
            Assert.AreEqual("Run", TlTimelineBlobs.NameOf(ref run1.Value));
            Assert.AreEqual(1, push0.Value.NextAssets.Length);
            Assert.AreEqual(run1.Value.Asset, push0.Value.NextAssets[0]);
        }

        [Test]
        public void TheRuntimeRegistersTheBlobAndStartsThePlayOnAwakeTimeline()
        {
            var timeline = Timeline("Push");
            Clip(ForceTrack(timeline), 0, 2, 4f);
            var director = Director("Cube", timeline);
            director.initialTime = 0.5;
            using var baked = new Baked(director.gameObject);
            var world = baked.World;
            var library = world.CreateSystem<TlLibrarySystem>();
            var register = world.CreateSystem<TlTimelineRegisterSystem>();
            var start = world.CreateSystem<TlPlayOnAwakeSystem>();
            library.Update(world.Unmanaged);
            register.Update(world.Unmanaged);
            start.Update(world.Unmanaged);
            var entities = world.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            Assert.IsFalse(entities.IsComponentEnabled<TlUnregistered>(entity));
            Assert.IsFalse(entities.IsComponentEnabled<TlPlayOnAwake>(entity));
            var id = entities.GetBuffer<TlTimelineSet>(entity)[0].Id;
            Assert.AreNotEqual(0, id);
            var requests = entities.GetBuffer<TimelineRequest>(entity);
            Assert.AreEqual(1, requests.Length);
            Assert.AreEqual(RequestOp.Start, requests[0].Op);
            Assert.AreEqual(id, requests[0].Timeline);
            Assert.AreEqual(30, requests[0].At);
            start.Update(world.Unmanaged);
            Assert.AreEqual(1, entities.GetBuffer<TimelineRequest>(entity).Length, "Play On Awake starts once");
        }

        [Test]
        public void TheProjectSettingDefaultsToSixtyTicksPerSecond()
        {
            Assert.AreEqual(60, TlProjectSettings.DefaultTicksPerSecond);
            Assert.AreEqual(60.0, TlTimelines.CreateAsset().editorSettings.frameRate, 1e-9);
        }

        TimelineAsset Timeline(string name)
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = name;
            _made.Add(timeline);
            return timeline;
        }

        static TlTrack ForceTrack(TimelineAsset timeline)
        {
            return TlTrackSwap.Replace(timeline.CreateTrack<TestForceMenu>(null, "Force"));
        }

        static void Clip(TlTrack track, double start, double duration, float strength)
        {
            var clip = track.CreateClip<TlClip>();
            clip.start = start;
            clip.duration = duration;
            var data = (ForceClip_TlData)((TlClip)clip.asset).Data;
            data.Direction = new float3(1f, 0f, 0f);
            data.Strength = strength;
        }

        PlayableDirector Director(string name, TimelineAsset timeline)
        {
            var gameObject = new GameObject(name);
            _made.Add(gameObject);
            var director = gameObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            director.playOnAwake = true;
            return director;
        }

        static Type EditorTypeFor(UnityEngine.Object target)
        {
            var editor = UnityEditor.Editor.CreateEditor(target);
            try
            {
                return editor.GetType();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(editor);
            }
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
                World = new World("TlTimelineBakingTest");
                _blobs = new BlobAssetStore(128);
                var settings = Activator.CreateInstance(hybrid.GetType("Unity.Entities.BakingSettings", true), flags, _blobs);
                utility.GetMethod("BakeGameObjects", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { World, roots, settings });
            }

            public int Count<T>() where T : unmanaged, IBufferElementData
            {
                using var query = World.EntityManager.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<T>() }, Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab });
                return query.CalculateEntityCount();
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
