using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Tl.Unity;
using Tl.Unity.Editor;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.TestTools;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace KindGame.Bridge
{
    public class TimelineBridgeTests
    {
        const string KindFolder = "Packages/com.tl.unity/Tests/Kinds/Timelines~";
        readonly List<Object> _made = new List<Object>();
        readonly List<string> _folders = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var made in _made)
                if (made != null)
                    Object.DestroyImmediate(made);
            _made.Clear();
            foreach (var folder in _folders)
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            _folders.Clear();
        }

        static double Seconds(int ticks) => ticks / 60.0;

        TimelineAsset Timeline(string name)
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = name;
            timeline.editorSettings.frameRate = 60;
            _made.Add(timeline);
            return timeline;
        }

        static void Clip<TAsset, TData>(TrackAsset track, int start, int end, TData data) where TAsset : TlClip<TData> where TData : unmanaged
        {
            var clip = track.CreateClip<TAsset>();
            clip.start = Seconds(start);
            clip.duration = Seconds(end - start);
            ((TAsset)clip.asset).Data = data;
        }

        TimelineAsset Run()
        {
            var timeline = Timeline("run");
            var walk = timeline.CreateTrack<WalkTrackAsset>(null, "Walk");
            walk.Data = new WalkTrack { Scale = 1 };
            Clip<WalkClipAsset, WalkClip>(walk, 0, 40, new WalkClip { Speed = 2, Priority = 0 });
            return timeline;
        }

        TimelineAsset SpeedBoost()
        {
            var timeline = Timeline("speed_boost");
            var walk = timeline.CreateTrack<WalkTrackAsset>(null, "Walk");
            walk.Data = new WalkTrack { Scale = 1 };
            Clip<WalkClipAsset, WalkClip>(walk, 0, 20, new WalkClip { Speed = 5, Priority = 10 });
            return timeline;
        }

        TimelineAsset Jump()
        {
            var timeline = Timeline("jump");
            var arc = timeline.CreateTrack<ArcTrackAsset>(null, "Arc");
            arc.Data = new ArcTrack { Scale = 1 };
            Clip<ArcClipAsset, ArcClip>(arc, 0, 12, new ArcClip { Velocity = 3 });
            var walk = timeline.CreateTrack<WalkTrackAsset>(null, "Walk");
            walk.Data = new WalkTrack { Scale = 1 };
            Clip<WalkClipAsset, WalkClip>(walk, 0, 12, new WalkClip { Speed = 1, Priority = 0 });
            var stat = timeline.CreateTrack<StatTrackAsset>(null, "Stat");
            stat.Data = new StatTrack { Scale = 1 };
            Clip<StatClipAsset, StatClip>(stat, 0, 1, new StatClip { Delta = -1 });
            timeline.CreateMarkerTrack();
            timeline.markerTrack.CreateMarker<StepMarkerAsset>(Seconds(6)).Data = new StepMarker { Delta = 10 };
            return timeline;
        }

        TimelineAsset Stomp()
        {
            var timeline = Timeline("stomp");
            timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration = Seconds(8);
            var hit = timeline.CreateTrack<HitTrackAsset>(null, "Hit");
            hit.Data = new HitTrack { Scale = 1 };
            Clip<HitClipAsset, HitClip>(hit, 5, 7, new HitClip { Amount = 10 });
            Clip<HitClipAsset, HitClip>(hit, 2, 4, new HitClip { Amount = 25 });
            return timeline;
        }

        static string Handwritten(string file)
        {
            return Normalize(File.ReadAllText(Path.Combine(TimelineKindSets.PhysicalPath(KindFolder), file)));
        }

        static string Normalize(string json) => json.Replace("\r\n", "\n").Trim();

        static string Convert(TimelineAsset timeline, TimelineEnd end = TimelineEnd.Remove, string channel = "", List<string> warnings = null)
        {
            return TimelineJson.Write(timeline, new TimelineSpec(timeline.name, end, null, channel), 60, warnings ?? new List<string>());
        }

        [Test]
        public void RunConvertsToTheHandWrittenJson()
        {
            var warnings = new List<string>();
            Assert.AreEqual(Handwritten("run.json"), Normalize(Convert(Run(), TimelineEnd.Loop, "locomotion", warnings)));
            CollectionAssert.IsEmpty(warnings);
        }

        [Test]
        public void JumpWithThreeTracksAndAMarkerConvertsToTheHandWrittenJson()
        {
            var warnings = new List<string>();
            Assert.AreEqual(Handwritten("jump.json"), Normalize(Convert(Jump(), TimelineEnd.Remove, "locomotion", warnings)));
            CollectionAssert.IsEmpty(warnings);
        }

        [Test]
        public void SpeedBoostConvertsToTheHandWrittenJson()
        {
            Assert.AreEqual(Handwritten("speed_boost.json"), Normalize(Convert(SpeedBoost())));
        }

        [Test]
        public void AFixedLengthTimelineKeepsItsDurationAndSortsClipsByStart()
        {
            Assert.AreEqual(Handwritten("stomp.json"), Normalize(Convert(Stomp(), TimelineEnd.Remove, "action")));
        }

        [Test]
        public void NextAndHoldWriteTheirKeys()
        {
            var vault = Timeline("vault");
            var walk = vault.CreateTrack<WalkTrackAsset>(null, "Walk");
            walk.Data = new WalkTrack { Scale = 1 };
            Clip<WalkClipAsset, WalkClip>(walk, 0, 4, new WalkClip { Speed = 9 });
            var json = TimelineJson.Write(vault, new TimelineSpec("vault", TimelineEnd.Next, "waltz", "locomotion"), 60, new List<string>());
            Assert.AreEqual(Handwritten("vault.json"), Normalize(json));
            var waltz = Timeline("waltz");
            walk = waltz.CreateTrack<WalkTrackAsset>(null, "Walk");
            walk.Data = new WalkTrack { Scale = 1 };
            Clip<WalkClipAsset, WalkClip>(walk, 0, 3, new WalkClip { Speed = 7 });
            Assert.AreEqual(Handwritten("waltz.json"), Normalize(Convert(waltz, TimelineEnd.Hold, "locomotion")));
        }

        [Test]
        public void TicksRoundToTheNearestTickAndHalvesRoundUp()
        {
            Assert.AreEqual(0, TimelineJson.Ticks(0, 60));
            Assert.AreEqual(40, TimelineJson.Ticks(40 / 60.0, 60));
            Assert.AreEqual(0, TimelineJson.Ticks(0.4 / 60, 60));
            Assert.AreEqual(1, TimelineJson.Ticks(0.5 / 60, 60));
            Assert.AreEqual(1, TimelineJson.Ticks(1 / 120.0, 60));
            Assert.AreEqual(1, TimelineJson.Ticks(0.6 / 60, 60));
            Assert.AreEqual(3, TimelineJson.Ticks(2.5 / 60, 60));
            Assert.AreEqual(2, TimelineJson.Ticks(1 / 30.0, 60));
            Assert.AreEqual(30, TimelineJson.Ticks(1.0, 30));
            Assert.AreEqual(65535, TimelineJson.Ticks(65535 / 60.0, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => TimelineJson.Ticks(65536 / 60.0, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => TimelineJson.Ticks(-1 / 60.0, 60));
        }

        [Test]
        public void OffGridTimesRoundAndWarn()
        {
            var timeline = Timeline("drift");
            var walk = timeline.CreateTrack<WalkTrackAsset>(null, "Walk");
            var clip = walk.CreateClip<WalkClipAsset>();
            clip.start = 1 / 120.0;
            clip.duration = 10 / 60.0;
            var warnings = new List<string>();
            var json = Convert(timeline, warnings: warnings);
            StringAssert.Contains("\"start\": 1,", json);
            StringAssert.Contains("\"end\": 11,", json);
            StringAssert.Contains("\"duration\": 11,", json);
            Assert.IsTrue(warnings.Exists(w => w.Contains("start is 0.5 ticks, rounded to 1")), string.Join("\n", warnings));
        }

        [Test]
        public void OtherTracksAndMutedTracksAreSkippedWithWarnings()
        {
            var timeline = Run();
            timeline.CreateTrack<ActivationTrack>(null, "Show");
            var muted = timeline.CreateTrack<WalkTrackAsset>(null, "Muted");
            Clip<WalkClipAsset, WalkClip>(muted, 0, 4, new WalkClip { Speed = 1 });
            muted.muted = true;
            var warnings = new List<string>();
            Assert.AreEqual(Handwritten("run.json"), Normalize(Convert(timeline, TimelineEnd.Loop, "locomotion", warnings)));
            Assert.AreEqual(2, warnings.Count, string.Join("\n", warnings));
            StringAssert.Contains("'Show' (ActivationTrack) is not a Tl track", warnings[0]);
            StringAssert.Contains("'Muted' is muted", warnings[1]);
        }

        [Test]
        public void AClipShorterThanATickAndAStaleFieldFailWithTheirLocation()
        {
            var timeline = Timeline("tiny");
            timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration = Seconds(10);
            var walk = timeline.CreateTrack<WalkTrackAsset>(null, "Walk");
            var clip = walk.CreateClip<WalkClipAsset>();
            clip.start = 0;
            clip.duration = 0.2 / 60;
            var error = Assert.Throws<InvalidOperationException>(() => Convert(timeline));
            StringAssert.Contains("shorter than one tick", error.Message);
            clip.duration = 2 / 60.0;
            ((WalkClipAsset)clip.asset).Fields.Add(new TlField("Sped", "3"));
            error = Assert.Throws<InvalidOperationException>(() => Convert(timeline));
            StringAssert.Contains("field 'Sped' is not on WalkClip", error.Message);
        }

        [Test]
        public void DataRoundTripsThroughTheStoredFields()
        {
            var clip = ScriptableObject.CreateInstance<WalkClipAsset>();
            _made.Add(clip);
            clip.Data = new WalkClip { Speed = 0.1f, Priority = 200 };
            Assert.AreEqual("0.1", clip.Fields[0].Value);
            Assert.AreEqual("200", clip.Fields[1].Value);
            Assert.AreEqual(0.1f, clip.Data.Speed);
            Assert.AreEqual(200, clip.Data.Priority);
        }

        [Test]
        public void TheLibraryTimelinesListGainsMissingFilesOnly()
        {
            var json = "{\n  \"tickRate\": 30,\n  \"timelines\": [\n    \"run.json\",\n    \"combat/*.json\"\n  ]\n}";
            Assert.AreEqual(30u, TimelineBridgeBake.TickRate(json));
            Assert.AreEqual(60u, TimelineBridgeBake.TickRate("{}"));
            Assert.AreSame(json, TimelineBridgeBake.ListTimelines(json, new[] { "run.json" }));
            Assert.AreEqual("{\n  \"tickRate\": 30,\n  \"timelines\": [\n    \"run.json\",\n    \"combat/*.json\",\n    \"dash.json\"\n  ]\n}", TimelineBridgeBake.ListTimelines(json, new[] { "dash.json" }));
            Assert.IsTrue(TimelineBridgeBake.Glob("*.json", "dash.json"));
            Assert.IsFalse(TimelineBridgeBake.Glob("combat/*.json", "dash.json"));
            Assert.AreEqual("{\n  \"timelines\": [\n    \"a.json\"\n  ]\n}", TimelineBridgeBake.ListTimelines("{}", new[] { "a.json" }));
        }

        [Test]
        public void ConstantNamesFollowTheTlbRule()
        {
            Assert.AreEqual("SpeedBoost", TimelineBridgeAsset.ConstantName("speed_boost"));
            Assert.AreEqual("PotionHop", TimelineBridgeAsset.ConstantName("potion-hop"));
            Assert.AreEqual("_2Step", TimelineBridgeAsset.ConstantName("2 step"));
        }

        TimelineLibraryAsset Library(string folder, string tlb = "tlb")
        {
            var library = ScriptableObject.CreateInstance<TimelineLibraryAsset>();
            library.name = "BridgeTestLibrary";
            library.Folder = folder;
            library.TimelineIds = "KindGame.Timelines";
            library.GameAssembly = "Tl.Unity.Kinds";
            library.Namespace = "KindGame";
            library.Tlb = tlb;
            _made.Add(library);
            return library;
        }

        TimelineBridgeAsset Bridge(TimelineLibraryAsset library, BridgeOutput output, params (TimelineAsset Timeline, TimelineEnd End, string Channel)[] timelines)
        {
            var bridge = ScriptableObject.CreateInstance<TimelineBridgeAsset>();
            bridge.name = "BridgeTest" + output;
            bridge.Library = library;
            bridge.Output = output;
            foreach (var (timeline, end, channel) in timelines)
                bridge.Timelines.Add(new BridgedTimeline { Timeline = timeline, End = end, Channel = channel });
            _made.Add(bridge);
            return bridge;
        }

        string CopyKindFolder(params string[] without)
        {
            var folder = Path.Combine(Path.GetTempPath(), "tl-bridge-" + Guid.NewGuid().ToString("N"));
            _folders.Add(folder);
            Directory.CreateDirectory(folder);
            foreach (var file in Directory.GetFiles(TimelineKindSets.PhysicalPath(KindFolder), "*.json"))
                if (Array.IndexOf(without, Path.GetFileName(file)) < 0)
                    File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            return folder;
        }

        [Test]
        public void BridgedTimelinesBakeTheSameGameTlibAsTheHandWrittenJson()
        {
            var tlb = Environment.GetEnvironmentVariable("TL_TLB");
            if (string.IsNullOrEmpty(tlb))
                Assert.Ignore("Set TL_TLB to a tlb or Tl.Bake.dll with the library command.");
            var bridged = new[] { "run.json", "jump.json", "speed_boost.json" };
            var handwritten = CopyKindFolder();
            var inFolder = CopyKindFolder(bridged);
            var staged = CopyKindFolder(bridged);
            var gameDll = KindCapacityBake.Stage("Tl.Unity.Kinds");
            TimelineBridgeBake.Tlb(tlb, new[] { "library", handwritten, "--out", Path.Combine(handwritten, "game.tlib"), "--assembly", gameDll, "--namespace", "KindGame", "--diagnostics", "json" });
            var expected = File.ReadAllBytes(Path.Combine(handwritten, "game.tlib"));
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(TimelineKindSets.PhysicalPath(BridgeLibraryWorld.LibraryFile)), "The kinds library fixture is stale; run eng/pack-kinds-fixture.sh.");
            foreach (var (folder, output) in new[] { (inFolder, BridgeOutput.JsonInLibraryFolder), (staged, BridgeOutput.StagedJson) })
            {
                var bridge = Bridge(Library(folder, tlb), output, (Run(), TimelineEnd.Loop, "locomotion"), (Jump(), TimelineEnd.Remove, "locomotion"), (SpeedBoost(), TimelineEnd.Remove, ""));
                var result = TimelineBridgeBake.Bake(bridge, false);
                CollectionAssert.IsEmpty(result.Warnings);
                Assert.IsFalse(result.LibraryJsonChanged);
                Assert.AreEqual(3, result.JsonFiles.Count);
                CollectionAssert.AreEqual(expected, File.ReadAllBytes(Path.Combine(folder, "game.tlib")), output + " game.tlib differs from the hand-written bake.");
                foreach (var name in bridged)
                    Assert.AreEqual(output == BridgeOutput.JsonInLibraryFolder, File.Exists(Path.Combine(folder, name)), output + " " + name);
            }
        }

        static World Bake(params GameObject[] roots)
        {
            var hybrid = typeof(Baker<>).Assembly;
            var utility = hybrid.GetType("Unity.Entities.BakingUtility", true);
            var flags = Enum.Parse(utility.GetNestedType("BakingFlags", BindingFlags.Public | BindingFlags.NonPublic), "AssignName");
            var world = new World("TlBridgeBakingTest");
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

        GameObject Director(string name, TimelineBridgeAsset bridge, TimelineAsset plays, params TimelineAsset[] mayStart)
        {
            var gameObject = new GameObject(name);
            _made.Add(gameObject);
            var director = gameObject.AddComponent<PlayableDirector>();
            director.playableAsset = plays;
            director.playOnAwake = true;
            gameObject.AddComponent<TimelinePlayerAuthoring>();
            gameObject.AddComponent<RunnerAuthoring>();
            var authoring = gameObject.AddComponent<TimelineDirectorAuthoring>();
            authoring.Bridge = bridge;
            authoring.MayStart = mayStart;
            authoring.Sync();
            return gameObject;
        }

        static string KindsOf(World world, Entity entity)
        {
            var registry = TimelineKindSets.Registry;
            var names = new List<string>();
            for (var k = 0; k < registry.Count; k++)
                if (world.EntityManager.HasComponent(entity, ComponentType.ReadWrite(registry[k].Entry)))
                    names.Add(registry[k].Name);
            names.Sort(StringComparer.Ordinal);
            return string.Join("+", names);
        }

        static Entity Find(World world, string name)
        {
            using var all = world.EntityManager.GetAllEntities(Allocator.Temp);
            foreach (var entity in all)
                if (world.EntityManager.GetName(entity) == name && world.EntityManager.HasBuffer<TimelineRequest>(entity))
                    return entity;
            Assert.Fail("No baked entity named '" + name + "'.");
            return Entity.Null;
        }

        [Test]
        public void ADirectorBakesItsPlayerWithExactlyTheKindsOfItsTimelines()
        {
            var run = Run();
            var jump = Jump();
            var bridge = Bridge(Library(KindFolder), BridgeOutput.JsonInLibraryFolder, (run, TimelineEnd.Loop, "locomotion"), (jump, TimelineEnd.Remove, "locomotion"));
            var walker = Director("BridgeWalker", bridge, run);
            var jumper = Director("BridgeJumper", bridge, run, jump);
            var player = jumper.GetComponent<TimelinePlayerAuthoring>();
            CollectionAssert.AreEqual(new[] { "Run" }, player.StartWith);
            CollectionAssert.AreEqual(new[] { "Jump" }, player.MayStart);
            Assert.AreSame(bridge.Library, player.Library);
            using var world = Bake(walker, jumper);
            Assert.AreEqual("Locomotion", KindsOf(world, Find(world, "BridgeWalker")));
            Assert.AreEqual("Lift+Locomotion+StatChange", KindsOf(world, Find(world, "BridgeJumper")));
            var requests = world.EntityManager.GetBuffer<TimelineRequest>(Find(world, "BridgeWalker"));
            Assert.AreEqual(1, requests.Length);
            Assert.AreEqual(Timelines.Run, requests[0].Timeline);
        }

        [Test]
        public void ADirectorWhosePlayerIsOutOfSyncIsABakingError()
        {
            var run = Run();
            var bridge = Bridge(Library(KindFolder), BridgeOutput.JsonInLibraryFolder, (run, TimelineEnd.Loop, "locomotion"));
            var walker = Director("BridgeStale", bridge, run);
            walker.GetComponent<PlayableDirector>().playOnAwake = false;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("GameObject 'BridgeStale' Timeline Player does not match its PlayableDirector \\(expected start-with \\[\\]"));
            using var world = Bake(walker);
            Assert.IsTrue(walker.GetComponent<TimelineDirectorAuthoring>().Sync());
            CollectionAssert.IsEmpty(walker.GetComponent<TimelinePlayerAuthoring>().StartWith);
        }
    }
}
