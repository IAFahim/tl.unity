using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Tl.Generated;
using Tl.Unity.Editor;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;
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
        public void EachAutoKindAddsOnlyTheColumnsItsOwnApplyWrites()
        {
            var registry = TimelineKindSets.Registry;
            var glow = registry[registry.IndexOf("AutoKinds.GlowKind")];
            var chime = registry[registry.IndexOf("AutoKinds.ChimeKind")];
            CollectionAssert.AreEqual(new[] { typeof(AutoKinds.Glow) }, glow.Columns);
            CollectionAssert.AreEqual(new[] { typeof(AutoKinds.Chime) }, chime.Columns);
            var timeline = Timeline("GlowOnly");
            var track = timeline.CreateTrack<TlTrack>(null, "Glow");
            track.Template = new AutoKinds_GlowClip_TlData();
            var clip = track.CreateClip<TlClip>();
            clip.start = 0;
            clip.duration = 1;
            using var baked = new Baked(Director("Lamp", timeline).gameObject);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            Assert.IsTrue(entities.HasComponent<AutoKinds.Glow>(entity), "the Glow kind adds Glow");
            Assert.IsTrue(entities.HasComponent(entity, ComponentType.ReadWrite(glow.Entry)));
            Assert.IsFalse(entities.HasComponent<AutoKinds.Chime>(entity), "an unrelated kind adds nothing");
            Assert.IsFalse(entities.HasComponent(entity, ComponentType.ReadWrite(chime.Entry)));
        }

        [Test]
        public void ClipStartEventsOfAnAutoTrackReachTheTimelineEventBuffer()
        {
            var registry = TimelineKindSets.Registry;
            CollectionAssert.Contains(registry.EventBuffers, typeof(TimelineEvent<AutoKinds.Ding>));
            var system = Type.GetType("Tl.Generated.TlAuto_Tl_Unity_AutoKinds_Jobs, Tl.Unity.AutoKinds");
            Assert.IsNotNull(system, "the auto kind system of the fixture");
            var timeline = Timeline("Chimes");
            var track = timeline.CreateTrack<TlTrack>(null, "Chime");
            track.Template = new AutoKinds_ChimeClip_TlData();
            var clip = track.CreateClip<TlClip>();
            clip.start = 0;
            clip.duration = 1;
            using var baked = new Baked(Director("Bell", timeline).gameObject);
            var entity = baked.Single<TlTimelineSet>();
            Assert.IsTrue(baked.World.EntityManager.HasBuffer<TimelineEvent<AutoKinds.Ding>>(entity), "the bell gets the Ding event buffer");
        }

        [Test]
        public void TheMarkerMenuStubBecomesATlMarkerInOneUndoStep()
        {
            var timeline = Timeline("Bells");
            timeline.CreateMarkerTrack();
            Undo.IncrementCurrentGroup();
            var stub = timeline.markerTrack.CreateMarker<TestBellMenu>(0.5);
            new TlMenuMarkerEditor().OnCreate(stub, null);
            var markers = timeline.markerTrack.GetMarkers().ToList();
            Assert.AreEqual(1, markers.Count);
            var marker = markers[0] as TlMarker;
            Assert.IsNotNull(marker, "the stub was replaced by a TlMarker");
            Assert.AreEqual(0.5, marker.time, 1e-9);
            Assert.AreEqual("Bell", marker.name);
            Assert.IsInstanceOf<AutoKinds_Bell_TlMarkerData>(marker.Data);
            Undo.PerformUndo();
            Assert.AreEqual(0, timeline.markerTrack.GetMarkers().Count(), "one undo removes the whole add");
        }

        [Test]
        public void MarkersPlacedInTheTimelineReachTheKindJobAtTheirTicks()
        {
            var timeline = Timeline("Bells");
            timeline.CreateMarkerTrack();
            var stub = timeline.markerTrack.CreateMarker<TestBellMenu>(0.5);
            new TlMenuMarkerEditor().OnCreate(stub, null);
            ((AutoKinds_Bell_TlMarkerData)timeline.markerTrack.GetMarkers().OfType<TlMarker>().Single().Data).Rings = 3;
            var track = timeline.CreateTrack<TlTrack>(null, "Chime");
            track.Template = new AutoKinds_ChimeClip_TlData();
            var onTrack = track.CreateMarker<TlMarker>(1.0);
            onTrack.Data = new AutoKinds_Bell_TlMarkerData { Rings = 4 };
            using var baked = new Baked(Director("Bell", timeline).gameObject);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            Assert.IsTrue(entities.HasComponent<AutoKinds.Chime>(entity), "a timeline with only markers still adds the kind they pulse");
            var live = new LiveWorld(baked.World, false, typeof(TlKindSystem<>).MakeGenericType(Type.GetType("Tl.Generated.TlAuto_Tl_Unity_AutoKinds_Jobs, Tl.Unity.AutoKinds", true)));
            var rang = new List<string>();
            var last = 0;
            for (var step = 0; step < 90; step++)
            {
                live.Step(1);
                var count = entities.GetComponentData<AutoKinds.Chime>(entity).Count;
                if (count != last)
                    rang.Add(entities.GetBuffer<Playback>(entity)[0].Tick + ":" + count);
                last = count;
            }
            CollectionAssert.AreEqual(new[] { "30:3", "60:7" }, rang, string.Join(", ", rang));
        }

        [Test]
        public void AKindsIdleRunsOnTicksNoClipFeedsItAndAKindWithoutOneKeepsItsLastValue()
        {
            var timeline = Timeline("DoubleJump");
            var track = timeline.CreateTrack<TlTrack>(null, "DoubleJump");
            track.Template = new AutoKinds_DoubleJumpClip_TlData();
            var windowClip = track.CreateClip<TlClip>();
            windowClip.start = 0.5;
            windowClip.duration = 0.5;
            ((AutoKinds_DoubleJumpClip_TlData)((TlClip)windowClip.asset).Data).Allowed = true;
            using var baked = new Baked(Director("Player", timeline).gameObject);
            var entities = baked.World.EntityManager;
            var player = baked.Single<TlTimelineSet>();
            var live = new LiveWorld(baked.World, false, typeof(TlKindSystem<>).MakeGenericType(Type.GetType("Tl.Generated.TlAuto_Tl_Unity_AutoKinds_Jobs, Tl.Unity.AutoKinds", true)));
            const int ticks = 120;
            var canDoubleJumpWithIdle = new bool[ticks];
            var canDoubleJumpWithoutIdle = new bool[ticks];
            for (var tick = 0; tick < ticks; tick++)
            {
                live.Step(1);
                var abilities = entities.GetComponentData<AutoKinds.Abilities>(player);
                canDoubleJumpWithIdle[tick] = abilities.CanDoubleJumpWithIdle;
                canDoubleJumpWithoutIdle[tick] = abilities.CanDoubleJumpWithoutIdle;
            }
            foreach (var tick in new[] { 0, 29 })
            {
                Assert.IsFalse(canDoubleJumpWithIdle[tick], "with Idle, tick " + tick + " is before the window");
                Assert.IsFalse(canDoubleJumpWithoutIdle[tick], "without Idle, tick " + tick + " is before the window");
            }
            foreach (var tick in new[] { 30, 45, 59 })
            {
                Assert.IsTrue(canDoubleJumpWithIdle[tick], "with Idle, tick " + tick + " is inside the window");
                Assert.IsTrue(canDoubleJumpWithoutIdle[tick], "without Idle, tick " + tick + " is inside the window");
            }
            foreach (var tick in new[] { 60, 61, 90, 119 })
            {
                Assert.IsFalse(canDoubleJumpWithIdle[tick], "with Idle, tick " + tick + " is after the window: Idle switched the flag off");
                Assert.IsTrue(canDoubleJumpWithoutIdle[tick], "without Idle, tick " + tick + " keeps the last clip value");
            }
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
        public void TwoClipsWithTheSameCurveBakeWithoutAWarning()
        {
            var timeline = Timeline("Push");
            var track = ForceTrack(timeline);
            Clip(track, 0, 1, 4f);
            Clip(track, 1, 1, 2f);
            using var baked = new Baked(Director("Cube", timeline).gameObject);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AnEmptyTrackNextToAFilledOneBakesWithoutALog()
        {
            var timeline = Timeline("Push");
            Clip(ForceTrack(timeline), 0, 1, 4f);
            ForceTrack(timeline);
            using var baked = new Baked(Director("Cube", timeline).gameObject);
            LogAssert.NoUnexpectedReceived();
            Assert.AreEqual(1, baked.Count<TlTimelineSet>(), "the timeline still bakes");
        }

        [Test]
        public void OnlyEmptyTracksBakeWithoutALog()
        {
            var timeline = Timeline("Push");
            ForceTrack(timeline);
            using var baked = new Baked(Director("Cube", timeline).gameObject);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void TwoClipsWithDifferentCurvesWarnOnce()
        {
            var timeline = Timeline("Push");
            var track = ForceTrack(timeline);
            Clip(track, 0, 1, 4f);
            Clip(track, 1, 1, 2f).Falloff = AnimationCurve.Constant(0f, 1f, 0.5f);
            LogAssert.Expect(LogType.Warning, new Regex("already sets ForceFalloff with a different value"));
            using var baked = new Baked(Director("Cube", timeline).gameObject);
        }

        [Test]
        public void AlsoPlaysAddsItsTimelinesAfterThePlayable()
        {
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 1, 4f);
            var dash = Timeline("Dash");
            Clip(ForceTrack(dash), 0, 1, 8f);
            var director = Director("Cube", push);
            var also = director.gameObject.AddComponent<TlAlsoPlays>();
            also.Timelines.Add(dash);
            also.Timelines.Add(null);
            using var baked = new Baked(director.gameObject);
            var set = baked.World.EntityManager.GetBuffer<TlTimelineSet>(baked.Single<TlTimelineSet>());
            Assert.AreEqual(2, set.Length);
            var first = set[0].Blob;
            var second = set[1].Blob;
            Assert.AreEqual("Push", TlTimelineBlobs.NameOf(ref first.Value));
            Assert.AreEqual("Dash", TlTimelineBlobs.NameOf(ref second.Value));
            CollectionAssert.AreEqual(new[] { push, dash }, TlInspectorModel.Plays(director, also));
        }

        [Test]
        public void TheTimelineSectionWritesTheSettingsMarkerOnTheFirstChange()
        {
            var push = Timeline("Push");
            Assert.AreEqual(TlInspectorModel.NoTlTracks, TlInspectorModel.Status(push));
            TlInspectorModel.SetSettings(push, TlEnd.Stop, null, 0);
            Assert.IsNull(TlDirectorTimelines.Settings(push), "no change, no marker");
            var run = Timeline("Run");
            TlInspectorModel.SetSettings(push, TlEnd.Next, run, 3);
            var settings = TlDirectorTimelines.Settings(push);
            Assert.IsNotNull(settings);
            Assert.AreEqual(TlEnd.Next, settings.End);
            Assert.AreEqual(run, settings.Next);
            Assert.AreEqual(3, settings.Channel);
            TlInspectorModel.SetSettings(push, TlEnd.Loop, null, 0);
            Assert.AreEqual(1, push.markerTrack.GetMarkers().Count(), "the marker is reused");
            Assert.AreEqual(TlEnd.Loop, TlDirectorTimelines.Settings(push).End);
            Clip(ForceTrack(push), 0, 1, 4f);
            Assert.AreEqual("OK. 1 Tl track, 1 clip.", TlInspectorModel.Status(push));
            var sixty = TlTimelines.CreateAsset();
            _made.Add(sixty);
            Assert.IsNull(TlInspectorModel.FrameRateHint(sixty, 60));
            Assert.IsNotNull(TlInspectorModel.FrameRateHint(sixty, 50));
        }

        [Test]
        public void TheDirectorFoldoutSaysWhatIsMissing()
        {
            var director = Director("Cube", null);
            CollectionAssert.AreEqual(new[] { TlInspectorModel.NoTimeline }, TlInspectorModel.DirectorMessages(director, null));
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 1, 4f);
            director.playableAsset = push;
            CollectionAssert.Contains(TlInspectorModel.DirectorMessages(director, null), TlInspectorModel.NotInSubScene);
        }

        [Test]
        public void ALiveEditSwapsTheTimelineAndKeepsEachPlaybacksTick()
        {
            var timeline = Timeline("Push");
            var track = ForceTrack(timeline);
            var data = Clip(track, 0, 2, 4f);
            var director = Director("Cube", timeline);
            using var baked = new Baked(director.gameObject);
            var live = new LiveWorld(baked.World);
            live.Step(10);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            var id = entities.GetBuffer<TlTimelineSet>(entity)[0].Id;
            var before = entities.GetBuffer<Playback>(entity)[0];
            Assert.AreEqual(id, before.Timeline);
            Assert.Greater(before.Tick, 0);
            var generation = live.Generation;

            data.Strength = 9f;
            using var edited = new Baked(director.gameObject);
            Patch(entities, entity, edited.World.EntityManager.GetBuffer<TlTimelineSet>(edited.Single<TlTimelineSet>())[0].Blob);
            live.Step(1);

            Assert.AreEqual(id, entities.GetBuffer<TlTimelineSet>(entity)[0].Id, "the edited timeline keeps its id");
            Assert.AreEqual(generation + 1, live.Generation, "the library reloaded once");
            var after = entities.GetBuffer<Playback>(entity)[0];
            Assert.AreEqual(id, after.Timeline);
            Assert.AreEqual(before.Tick, after.Tick, "the playback keeps its tick across the swap");
            Assert.AreEqual(1, entities.GetBuffer<TlUnityGenerated.ForceKindEntry>(entity).Length, "one Force span, not a stale copy");
            live.Step(1);
            Assert.AreEqual(before.Tick + 1, entities.GetBuffer<Playback>(entity)[0].Tick, "and keeps playing");
        }

        [Test]
        public void ALiveEditThatChangesTheTrackTypesIsRejectedWithAMessage()
        {
            var timeline = Timeline("Push");
            var track = ForceTrack(timeline);
            Clip(track, 0, 2, 4f);
            var director = Director("Cube", timeline);
            using var baked = new Baked(director.gameObject);
            var live = new LiveWorld(baked.World);
            live.Step(10);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            var id = entities.GetBuffer<TlTimelineSet>(entity)[0].Id;
            var tick = entities.GetBuffer<Playback>(entity)[0].Tick;
            var generation = live.Generation;

            foreach (var clip in track.GetClips().ToList())
                timeline.DeleteClip(clip);
            using var edited = new Baked(director.gameObject);
            Patch(entities, entity, edited.World.EntityManager.GetBuffer<TlTimelineSet>(edited.Single<TlTimelineSet>())[0].Blob);
            LogAssert.Expect(LogType.Error, new Regex("adds or removes a type of track.*earlier version of 'Push' keeps playing"));
            live.Step(1);

            Assert.AreEqual(id, entities.GetBuffer<TlTimelineSet>(entity)[0].Id, "the old version stays registered");
            Assert.AreEqual(generation, live.Generation, "no reload");
            Assert.AreEqual(tick + 1, entities.GetBuffer<Playback>(entity)[0].Tick, "the old version keeps playing");
        }

        static void Patch(EntityManager entities, Entity entity, BlobAssetReference<TlTimelineBlob> blob)
        {
            var set = entities.GetBuffer<TlTimelineSet>(entity);
            set[0] = new TlTimelineSet { Blob = blob };
        }

        // A cold editor compiles jobs on a background thread, so the first steps run the managed copy and set the probe.
        // Step until the Burst copy has compiled (bounded), then clear the probe; a job that never compiles still fails the assert.
        static void WarmBurst(LiveWorld live)
        {
            if (BurstCompiler.IsEnabled)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var clean = 0;
                while (clean < 2 && clock.Elapsed.TotalSeconds < 90)
                {
                    ControlProbe.RanManaged = false;
                    live.Step(1);
                    if (ControlProbe.RanManaged) { clean = 0; System.Threading.Thread.Sleep(20); }
                    else clean++;
                }
            }
        }

        sealed class LiveWorld
        {
            readonly World _world;
            readonly SystemHandle[] _systems;

            public LiveWorld(World world, bool control = false, Type apply = null)
            {
                _world = world;
                var systems = new List<SystemHandle>
                {
                    world.CreateSystem<TlLibrarySystem>(),
                    world.CreateSystem<TlTimelineRegisterSystem>(),
                    world.CreateSystem<TlPlayOnAwakeSystem>(),
                };
                if (control)
                {
                    systems.Add(world.CreateSystem<ThenPlaySystem>());
                    systems.Add(world.CreateSystem<TlRequestResolveSystem>());
                }
                systems.Add(world.CreateSystem<TimelineKindTickSystem>());
                if (apply != null)
                    systems.Add(world.CreateSystem(apply));
                _systems = systems.ToArray();
            }

            public uint Generation
            {
                get
                {
                    using var query = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TimelineLibrary>());
                    return query.GetSingleton<TimelineLibrary>().Generation;
                }
            }

            public void Step(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    foreach (var system in _systems)
                        system.Update(_world.Unmanaged);
                    _world.EntityManager.CompleteAllTrackedJobs();
                }
            }
        }

        [Test]
        public void ClipTimelineFieldsJoinTheSetAndTimelineIdsArePatchedToLibraryIds()
        {
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 2, 4f);
            var dash = Timeline("Dash");
            Clip(ForceTrack(dash), 0, 1, 8f);
            var kick = Timeline("Kick");
            Clip(ForceTrack(kick), 0, 1, 2f);
            ThenPlayClipOn(push, dash, kick);
            using var baked = new Baked(Director("Cube", push).gameObject);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            var set = entities.GetBuffer<TlTimelineSet>(entity);
            Assert.AreEqual(3, set.Length);
            var pushBlob = set[0].Blob;
            Assert.AreEqual("Push", TlTimelineBlobs.NameOf(ref pushBlob.Value));
            var names = new List<string>();
            for (var i = 0; i < set.Length; i++)
                names.Add(TlTimelineBlobs.NameOf(ref set[i].Blob.Value));
            CollectionAssert.AreEquivalent(new[] { "Push", "Dash", "Kick" }, names);
            Assert.AreEqual(2, pushBlob.Value.RefSites.Length, "one TimelineId field");
            Assert.AreEqual(1u, pushBlob.Value.RefSites[1], "it holds local id 1");
            var tlb = TlTimelineBlobs.ToArray(ref pushBlob.Value.Tlb);
            var site = (int)pushBlob.Value.RefSites[0];
            Assert.AreEqual(1, tlb[site] | (tlb[site + 1] << 8));
            Assert.IsTrue(entities.HasComponent<ThenPlay>(entity));
            Assert.AreEqual(TlTimelinePacker.AssetOf(dash).ToAssetId(), entities.GetComponentData<ThenPlay>(entity).Timeline.Asset, "the TlTimeline field bakes to the Dash asset");

            var live = new LiveWorld(baked.World);
            live.Step(1);
            var library = Library(baked.World);
            var kickId = library.IdOf(TlTimelinePacker.AssetOf(kick));
            Assert.AreNotEqual(0, kickId);
            tlb[site] = (byte)kickId;
            tlb[site + 1] = (byte)(kickId >> 8);
            Assert.GreaterOrEqual(IndexOf(library.Bytes, tlb), 0, "the library holds Push with Kick's library id in the clip");
        }

        [Test]
        public void AJobStartsPausesAndResumesATimelineFromItsTlTimelineField()
        {
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 2, 4f);
            var dash = Timeline("Dash");
            Clip(ForceTrack(dash), 0, 1, 8f);
            ThenPlayClipOn(push, dash, null);
            using var baked = new Baked(Director("Cube", push).gameObject);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            var live = new LiveWorld(baked.World, true);
            WarmBurst(live);
            ControlProbe.RanManaged = false;
            live.Step(3);
            var dashId = Library(baked.World).IdOf(TlTimelinePacker.AssetOf(dash));
            Assert.AreNotEqual(0, dashId);
            Assert.IsFalse(Playing(entities, entity, dashId, out _), "Dash waits for the gameplay condition");

            entities.SetComponentData(entity, new Trigger { Fire = 1 });
            live.Step(3);
            Assert.IsTrue(Playing(entities, entity, dashId, out var started), "the job started Dash");
            if (BurstCompiler.IsEnabled)
                Assert.IsFalse(ControlProbe.RanManaged, "the job ran as Burst code");

            entities.SetComponentData(entity, new Trigger { Hold = 1 });
            live.Step(1);
            Playing(entities, entity, dashId, out var held);
            Assert.AreNotEqual(0, (int)(held.Flags & global::Tl.PlaybackFlags.Paused), "Pause sets the flag");
            live.Step(5);
            Playing(entities, entity, dashId, out var still);
            Assert.AreEqual(held.Tick, still.Tick, "a paused timeline holds its tick");

            entities.SetComponentData(entity, new Trigger { Release = 1 });
            live.Step(3);
            Playing(entities, entity, dashId, out var resumed);
            Assert.AreEqual(0, (int)(resumed.Flags & global::Tl.PlaybackFlags.Paused));
            Assert.Greater(resumed.Tick, still.Tick, "Resume continues");
        }

        [Test]
        public void AJobChangesTheSpeedOfATimelineFromItsTlTimelineField()
        {
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 2, 4f);
            var dash = Timeline("Dash");
            Clip(ForceTrack(dash), 0, 40, 8f);
            ThenPlayClipOn(push, dash, null);
            using var baked = new Baked(Director("Cube", push).gameObject);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            var live = new LiveWorld(baked.World, true);
            WarmBurst(live);
            ControlProbe.RanManaged = false;
            live.Step(1);
            var dashId = Library(baked.World).IdOf(TlTimelinePacker.AssetOf(dash));
            entities.SetComponentData(entity, new Trigger { Fire = 1 });
            live.Step(3);
            Assert.IsTrue(Playing(entities, entity, dashId, out var before), "the job started Dash");
            entities.SetComponentData(entity, new Trigger { Speed = 48 });
            live.Step(1);
            Playing(entities, entity, dashId, out var fast);
            Assert.AreEqual(48, fast.Rate, "SetSpeed stores sixteenths");
            Assert.AreEqual(before.Tick + 3, fast.Tick, "3x crosses three ticks per step");
            if (BurstCompiler.IsEnabled)
                Assert.IsFalse(ControlProbe.RanManaged, "the job ran as Burst code");
            entities.SetComponentData(entity, new Trigger { Speed = 8 });
            live.Step(2);
            Playing(entities, entity, dashId, out var slow);
            Assert.AreEqual(fast.Tick + 1, slow.Tick, "half speed crosses one tick in two steps");
            entities.SetComponentData(entity, new Trigger { Speed = 16 });
            live.Step(1);
            Playing(entities, entity, dashId, out var normal);
            Assert.AreEqual(0, normal.Rate, "1x stores zero");
            Assert.AreEqual(slow.Tick + 1, normal.Tick);
        }

        [Test]
        public void ThePanelButtonsPlayPauseSeekReverseAndChangeSpeed()
        {
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 20, 4f);
            using var baked = new Baked(Director("Cube", push).gameObject);
            var live = new LiveWorld(baked.World, true);
            var host = new GameObject("Panel");
            try
            {
                var panel = host.AddComponent<TlPanel>();
                panel.TargetWorld = baked.World;
                live.Step(2);
                Assert.AreEqual(1, panel.TargetCount);
                Assert.AreEqual(1, panel.TimelineCount);
                Assert.AreEqual("Push", panel.TimelineName(0));
                Assert.IsTrue(panel.State().Live, "the director starts Push");

                Assert.IsTrue(panel.Pause());
                live.Step(1);
                var parked = panel.State();
                Assert.IsTrue(parked.Paused);
                live.Step(5);
                Assert.AreEqual(parked.Tick, panel.State().Tick, "a paused timeline does not move");
                Assert.IsTrue(panel.Resume());
                live.Step(3);
                Assert.AreEqual(parked.Tick + 3, panel.State().Tick);

                Assert.IsTrue(panel.SeekTo(100));
                live.Step(1);
                Assert.AreEqual(100, panel.State().Tick, "seek lands on the tick");
                Assert.IsTrue(panel.SeekBy(1f));
                live.Step(1);
                Assert.AreEqual(160, panel.State().Tick, "+1 s is 60 ticks");

                Assert.IsTrue(panel.SetSpeed(2f));
                live.Step(1);
                var fast = panel.State();
                Assert.AreEqual(32, fast.Sixteenths);
                live.Step(1);
                Assert.AreEqual(fast.Tick + 2, panel.State().Tick, "2x crosses two ticks per step");
                Assert.IsTrue(panel.SetSpeed(1f));
                live.Step(1);
                Assert.AreEqual(16, panel.State().Sixteenths);

                var before = panel.State().Tick;
                Assert.IsTrue(panel.ToggleReverse());
                live.Step(3);
                var back = panel.State();
                Assert.IsTrue(back.Reverse);
                Assert.AreEqual(before - 3, back.Tick, "reverse walks backward");
                Assert.IsTrue(panel.ToggleReverse());
                live.Step(2);
                Assert.IsFalse(panel.State().Reverse);

                Assert.IsTrue(panel.WorldForward);
                panel.WorldForward = false;
                Assert.IsFalse(panel.WorldForward);
                var rewound = panel.State().Tick;
                live.Step(2);
                Assert.AreEqual(rewound - 2, panel.State().Tick, "the world rewinds every timeline");
                panel.WorldForward = true;

                Assert.IsTrue(panel.Stop());
                live.Step(4);
                Assert.IsFalse(panel.State().Live, "stopped");
                Assert.IsTrue(panel.Play());
                live.Step(2);
                Assert.IsTrue(panel.State().Live, "play starts it again");
                Assert.IsTrue(panel.StopAll());
                live.Step(4);
                Assert.IsFalse(panel.State().Live, "stop all");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void StartingATimelineTheObjectCannotPlayWarnsAndStartsNothing()
        {
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 2, 4f);
            using var baked = new Baked(Director("Cube", push).gameObject);
            var entities = baked.World.EntityManager;
            var entity = baked.Single<TlTimelineSet>();
            var live = new LiveWorld(baked.World, true);
            live.Step(2);
            var stranger = new TlTimeline { Asset = new TlAssetId(9, 9, 9, 9) };
            entities.GetBuffer<TimelineRequest>(entity).Start(stranger);
            LogAssert.Expect(LogType.Warning, new Regex("cannot play. Drag that timeline into Also Plays"));
            live.Step(2);
            Assert.AreEqual(1, entities.GetBuffer<Playback>(entity).AsNativeArray().ToArray().Count(p => p.Timeline != 0));
        }

        static TlLibrary Library(World world)
        {
            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TlLibraryHandle>());
            return query.GetSingleton<TlLibraryHandle>().Library;
        }

        static bool Playing(EntityManager entities, Entity entity, ushort id, out Playback playback)
        {
            foreach (var slot in entities.GetBuffer<Playback>(entity))
                if (slot.Timeline == id)
                {
                    playback = slot;
                    return true;
                }
            playback = default;
            return false;
        }

        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j])
                    j++;
                if (j == needle.Length)
                    return i;
            }
            return -1;
        }

        static void ThenPlayClipOn(TimelineAsset timeline, TimelineAsset then, TimelineAsset fallback)
        {
            var track = timeline.CreateTrack<TlTrack>(null, "Then Play");
            track.Template = new ThenPlayClip_TlData();
            var clip = track.CreateClip<TlClip>();
            clip.start = 0;
            clip.duration = 2;
            var data = (ThenPlayClip_TlData)((TlClip)clip.asset).Data;
            data.Timeline = then;
            data.Fallback = fallback;
            data.Gain = 1f;
        }

        [Test]
        public void ExportLibraryWritesATlibThatLoads()
        {
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 2, 4f);
            var dash = Timeline("Dash");
            Clip(ForceTrack(dash), 0, 1, 8f);
            ThenPlayClipOn(push, dash, null);
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tl-export-test.tlib");
            try
            {
                var names = TlExport.Export(new[] { push }, path);
                CollectionAssert.AreEquivalent(new[] { "Push", "Dash" }, names, "the clip's Dash comes along");
                using var library = TlLibrary.FromBytes(System.IO.File.ReadAllBytes(path));
                Assert.IsTrue(library.IsLoaded);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Test]
        public void ARatedPlaybackHandsOverToTheNextTimelineLikeOneXOverTheCrossedTicks()
        {
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 0.5, 4f);
            var dash = Timeline("Dash");
            Clip(ForceTrack(dash), 0, 3, 8f);
            TlInspectorModel.SetSettings(push, TlEnd.Next, dash, 0);
            var cube = Director("Cube", push).gameObject;
            using var fast = new Baked(cube);
            using var slow = new Baked(cube);
            var fastLive = new LiveWorld(fast.World, true);
            var slowLive = new LiveWorld(slow.World, true);
            fastLive.Step(1);
            slowLive.Step(1);
            var fastEntities = fast.World.EntityManager;
            var slowEntities = slow.World.EntityManager;
            var fastEntity = fast.Single<TlTimelineSet>();
            var slowEntity = slow.Single<TlTimelineSet>();
            var pushAsset = new TlTimeline { Asset = TlTimelinePacker.AssetOf(push).ToAssetId() };
            fastEntities.GetBuffer<TimelineRequest>(fastEntity).SetSpeed(pushAsset, 40);
            var handedOver = false;
            for (var step = 1; step <= 40; step++)
            {
                fastLive.Step(1);
                var crossed = (step * 40 >> 4) - ((step - 1) * 40 >> 4);
                slowLive.Step(crossed);
                var a = fastEntities.GetBuffer<Playback>(fastEntity)[0];
                var b = slowEntities.GetBuffer<Playback>(slowEntity)[0];
                Assert.AreEqual(b.Timeline, a.Timeline, "step " + step + " timeline");
                Assert.AreEqual(b.Tick, a.Tick, "step " + step + " tick");
                handedOver |= a.Timeline != fastEntities.GetBuffer<TlTimelineSet>(fastEntity)[0].Id;
            }
            Assert.IsTrue(handedOver, "the run crossed the handover to Dash");
        }

        [Test]
        public void ExportedSceneNamesTheDirectorsTimelineAndRunsInTheSceneRunner()
        {
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 2, 4f);
            var cube = Director("Cube", push).gameObject;
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tl-export-scene-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = TlSceneExport.Export(new[] { cube }, directory);
                var text = System.IO.File.ReadAllText(path);
                StringAssert.Contains("\"project\": \"tl.project.json\"", text);
                StringAssert.Contains("\"name\": \"Cube\"", text);
                StringAssert.Contains("\"timeline\": \"Push\"", text);
                Assert.AreEqual(text, System.IO.File.ReadAllText(TlSceneExport.Export(new[] { cube }, directory)), "exporting twice writes the same scene");
                var libraryPath = System.IO.Path.Combine(directory, "game.tlib");
                TlExport.Export(new[] { push }, libraryPath);
                var library = System.IO.File.ReadAllBytes(libraryPath);
                var scene = TlSceneRunner.Read(System.IO.File.ReadAllBytes(path), library);
                Assert.AreEqual(1, scene.Entities.Count);
                Assert.AreEqual(1, scene.Entities[0].Play.Count, "the director plays Push on awake");
                var early = TlSceneRunner.Run(scene, library, 10);
                Assert.AreEqual(early, TlSceneRunner.Run(scene, library, 10), "the same scene gives the same hash");
                Assert.AreNotEqual(early, TlSceneRunner.Run(scene, library, 20), "the playback moves");
            }
            finally
            {
                if (System.IO.Directory.Exists(directory))
                    System.IO.Directory.Delete(directory, true);
            }
        }

        [Test]
        public void NamedChannelsExportAsNamesAndBakeToThePackedBytes()
        {
            var settings = TlProjectSettings.instance;
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 2, 4f);
            TlInspectorModel.SetSettings(push, TlEnd.Stop, null, 2);
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tl-export-channels-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.IsTrue(settings.TrySetChannelName(1, "body", out _));
                Assert.IsTrue(settings.TrySetChannelName(2, "voice", out _));
                Assert.IsFalse(settings.TrySetChannelName(3, "voice", out var clash));
                StringAssert.Contains("Channel 2", clash);
                Assert.AreEqual("3", settings.ChannelName(3), "an unnamed channel is its number");
                TlExport.ExportJson(new[] { push }, directory);
                var json = System.IO.File.ReadAllText(System.IO.Path.Combine(directory, "Push.json"));
                StringAssert.Contains("\"channel\": \"voice\"", json);
                using (var project = Tl.Gen.Tlb.TlJson.ReadProject(System.IO.File.ReadAllBytes(System.IO.Path.Combine(directory, "tl.project.json"))))
                    CollectionAssert.AreEqual(new[] { "body", "voice" }, project.RootElement.GetProperty("channels").EnumerateArray().Select(e => e.GetString()).ToArray());
                var rate = settings.TicksPerSecond;
                var resolver = Tl.Gen.Tlb.BakerAssemblyResolver.FromAssemblies(new[] { push.GetOutputTracks().OfType<TlTrack>().First().Template.ClipType.Assembly, typeof(TlNoSettings<>).Assembly });
                var manifest = Tl.Gen.Tlb.LibraryBake.Manifest(new string[0], new[] { "body", "voice" }, rate);
                var baker = new TlBaker(null, Entity.Null, new HashSet<ComponentType>(), null);
                var blob = TlTimelinePacker.Pack(push, rate, TlTimelinePacker.Catalog, ref baker, push, out _);
                try
                {
                    var packed = blob.Value.Tlb.ToArray();
                    CollectionAssert.AreEqual(packed, Tl.Gen.Tlb.TimelineBaker.BakeJson(json, resolver, manifest: manifest), "JSON with named channels bakes to the packed bytes");
                    Assert.AreEqual(json, Tl.Gen.Tlb.TimelineJson.Decode(packed, resolver, manifest));
                }
                finally
                {
                    blob.Dispose();
                }
            }
            finally
            {
                settings.ResetChannelNames();
                if (System.IO.Directory.Exists(directory))
                    System.IO.Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ExportWritesJsonThatBakesToTheSamePackedBytes()
        {
            var push = Timeline("Push");
            Clip(ForceTrack(push), 0, 2, 4f);
            Clip(ForceTrack(push), 1, 2, 6f);
            var dash = Timeline("Dash");
            Clip(ForceTrack(dash), 0, 1, 8f);
            ThenPlayClipOn(push, dash, null);
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tl-export-json-" + Guid.NewGuid().ToString("N"));
            try
            {
                var names = TlExport.ExportJson(new[] { push }, directory);
                CollectionAssert.AreEquivalent(new[] { "Push", "Dash" }, names);
                using (var project = Tl.Gen.Tlb.TlJson.ReadProject(System.IO.File.ReadAllBytes(System.IO.Path.Combine(directory, "tl.project.json"))))
                    Assert.AreEqual(2, project.RootElement.GetProperty("timelines").GetArrayLength());
                var rate = TlProjectSettings.instance.TicksPerSecond;
                var assemblies = push.GetOutputTracks().OfType<TlTrack>().Select(t => t.Template.ClipType.Assembly).Append(typeof(TlNoSettings<>).Assembly).Distinct().ToArray();
                var resolver = Tl.Gen.Tlb.BakerAssemblyResolver.FromAssemblies(assemblies);
                foreach (var (timeline, refs) in new[] { (push, new[] { "Dash" }), (dash, new string[0]) })
                {
                    var baker = new TlBaker(null, Entity.Null, new HashSet<ComponentType>(), null);
                    var blob = TlTimelinePacker.Pack(timeline, rate, TlTimelinePacker.Catalog, ref baker, timeline, out _);
                    try
                    {
                        var packed = blob.Value.Tlb.ToArray();
                        var json = System.IO.File.ReadAllText(System.IO.Path.Combine(directory, timeline.name + ".json"));
                        var manifest = Tl.Gen.Tlb.LibraryBake.Manifest(refs, new string[0], rate);
                        CollectionAssert.AreEqual(packed, Tl.Gen.Tlb.TimelineBaker.BakeJson(json, resolver, manifest: manifest), timeline.name + " JSON bakes to the packed bytes");
                        Assert.AreEqual(json, Tl.Gen.Tlb.TimelineJson.Decode(packed, resolver, manifest), timeline.name + " decodes to the exported JSON");
                    }
                    finally
                    {
                        blob.Dispose();
                    }
                }
            }
            finally
            {
                if (System.IO.Directory.Exists(directory))
                    System.IO.Directory.Delete(directory, true);
            }
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

        static ForceClip_TlData Clip(TlTrack track, double start, double duration, float strength)
        {
            var clip = track.CreateClip<TlClip>();
            clip.start = start;
            clip.duration = duration;
            var data = (ForceClip_TlData)((TlClip)clip.asset).Data;
            data.Direction = new float3(1f, 0f, 0f);
            data.Strength = strength;
            return data;
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
