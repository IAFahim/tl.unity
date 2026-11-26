using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Tl.Generated;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity.Tracks.Tests
{
    public class TlTimelineSnapshotTests
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

        TimelineAsset BuildMultiKindTimeline()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = "SnapshotKinds";
            _made.Add(timeline);

            var glowTrack = timeline.CreateTrack<TlTrack>(null, "Glow");
            glowTrack.Template = new AutoKinds_GlowClip_TlData();
            var glowClip = glowTrack.CreateClip<TlClip>();
            glowClip.start = 0.0;
            glowClip.duration = 0.5;
            ((AutoKinds_GlowClip_TlData)((TlClip)glowClip.asset).Data).Amount = 7f;

            var chimeTrack = timeline.CreateTrack<TlTrack>(null, "Chime");
            chimeTrack.Template = new AutoKinds_ChimeClip_TlData();
            var chimeClip = chimeTrack.CreateClip<TlClip>();
            chimeClip.start = 0.25;
            chimeClip.duration = 0.5;
            ((AutoKinds_ChimeClip_TlData)((TlClip)chimeClip.asset).Data).Rings = 2;

            var marker = chimeTrack.CreateMarker<TlMarker>(0.6);
            marker.Data = new AutoKinds_Bell_TlMarkerData { Rings = 3 };

            var doubleJumpTrack = timeline.CreateTrack<TlTrack>(null, "DoubleJump");
            doubleJumpTrack.Template = new AutoKinds_DoubleJumpClip_TlData();
            var djClip = doubleJumpTrack.CreateClip<TlClip>();
            djClip.start = 0.5;
            djClip.duration = 0.5;
            ((AutoKinds_DoubleJumpClip_TlData)((TlClip)djClip.asset).Data).Allowed = true;

            return timeline;
        }

        [Test]
        public void KindBearingAndDensePlaybackSnapshotAndRestoreMatchFirstRunBitForBitWithZeroWarmAllocations()
        {
            var timeline = BuildMultiKindTimeline();
            var go = new GameObject("Actor");
            _made.Add(go);
            var director = go.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            director.playOnAwake = true;

            var hybrid = typeof(Baker<>).Assembly;
            var utility = hybrid.GetType("Unity.Entities.BakingUtility", true);
            var flags = Enum.Parse(utility.GetNestedType("BakingFlags", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic), "AssignName");
            using var world = new World("TlKindSnapshotTest");
            using var blobs = new BlobAssetStore(128);
            var settings = Activator.CreateInstance(hybrid.GetType("Unity.Entities.BakingSettings", true), flags, blobs);
            utility.GetMethod("BakeGameObjects", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { world, new[] { go }, settings });

            var entities = world.EntityManager;
            using var q = entities.CreateEntityQuery(ComponentType.ReadOnly<TlTimelineSet>());
            using var found = q.ToEntityArray(Allocator.Temp);
            Assert.AreEqual(1, found.Length);
            var actor = found[0];

            var systems = new SystemHandle[]
            {
                world.CreateSystem<TlLibrarySystem>(),
                world.CreateSystem<TlTimelineRegisterSystem>(),
                world.CreateSystem<TlPlayOnAwakeSystem>(),
                world.CreateSystem<TlRequestResolveSystem>(),
                world.CreateSystem<TlKindReachSystem>(),
                world.CreateSystem<TimelineKindTickSystem>(),
                world.CreateSystem(typeof(TlKindSystem<>).MakeGenericType(Type.GetType("Tl.Generated.TlAuto_Tl_Unity_AutoKinds_Jobs, Tl.Unity.AutoKinds", true))),
            };

            void StepOnce()
            {
                foreach (var sys in systems)
                    sys.Update(world.Unmanaged);
                entities.CompleteAllTrackedJobs();
            }

            var snapshotter = new TimelineSnapshotter();
            var snapshot = TimelineSnapshot.Create(Allocator.Persistent, 4096);
            try
            {
                for (var i = 0; i < 25; i++)
                    StepOnce();

                Assert.IsTrue(snapshotter.Capture(entities, ref snapshot));

                var atCapture = entities.GetBuffer<Playback>(actor).AsNativeArray().ToArray();
                var glowAtCapture = entities.GetComponentData<AutoKinds.Glow>(actor).Value;
                var chimeAtCapture = entities.GetComponentData<AutoKinds.Chime>(actor).Count;
                var djWithAtCapture = entities.GetComponentData<AutoKinds.Abilities>(actor).CanDoubleJumpWithIdle;
                var djWithoutAtCapture = entities.GetComponentData<AutoKinds.Abilities>(actor).CanDoubleJumpWithoutIdle;

                var trace1 = new List<(float Glow, int Chime, bool DjWith, bool DjWithout, Playback[] Playbacks, int DingCount)>();
                for (var i = 25; i < 90; i++)
                {
                    StepOnce();
                    var ab = entities.GetComponentData<AutoKinds.Abilities>(actor);
                    trace1.Add((
                        entities.GetComponentData<AutoKinds.Glow>(actor).Value,
                        entities.GetComponentData<AutoKinds.Chime>(actor).Count,
                        ab.CanDoubleJumpWithIdle,
                        ab.CanDoubleJumpWithoutIdle,
                        entities.GetBuffer<Playback>(actor).AsNativeArray().ToArray(),
                        entities.GetBuffer<TimelineEvent<AutoKinds.Ding>>(actor).Length));
                }

                Assert.IsTrue(snapshotter.Restore(entities, in snapshot));

                var restored = entities.GetBuffer<Playback>(actor).AsNativeArray().ToArray();
                Assert.AreEqual(atCapture.Length, restored.Length, "Playback count right after Restore");
                for (var s = 0; s < atCapture.Length; s++)
                    Assert.AreEqual(atCapture[s], restored[s], "Playback slot " + s + " right after Restore");
                Assert.AreEqual(glowAtCapture, entities.GetComponentData<AutoKinds.Glow>(actor).Value, "Glow right after Restore");
                Assert.AreEqual(chimeAtCapture, entities.GetComponentData<AutoKinds.Chime>(actor).Count, "Chime right after Restore");
                Assert.AreEqual(djWithAtCapture, entities.GetComponentData<AutoKinds.Abilities>(actor).CanDoubleJumpWithIdle, "DoubleJumpWithIdle right after Restore");
                Assert.AreEqual(djWithoutAtCapture, entities.GetComponentData<AutoKinds.Abilities>(actor).CanDoubleJumpWithoutIdle, "DoubleJumpWithoutIdle right after Restore");

                var trace2 = new List<(float Glow, int Chime, bool DjWith, bool DjWithout, Playback[] Playbacks, int DingCount)>();
                for (var i = 25; i < 90; i++)
                {
                    StepOnce();
                    var ab = entities.GetComponentData<AutoKinds.Abilities>(actor);
                    trace2.Add((
                        entities.GetComponentData<AutoKinds.Glow>(actor).Value,
                        entities.GetComponentData<AutoKinds.Chime>(actor).Count,
                        ab.CanDoubleJumpWithIdle,
                        ab.CanDoubleJumpWithoutIdle,
                        entities.GetBuffer<Playback>(actor).AsNativeArray().ToArray(),
                        entities.GetBuffer<TimelineEvent<AutoKinds.Ding>>(actor).Length));
                }

                Assert.AreEqual(trace1.Count, trace2.Count);
                for (var i = 0; i < trace1.Count; i++)
                {
                    Assert.AreEqual(trace1[i].Glow, trace2[i].Glow, "Glow mismatch at tick " + (25 + i));
                    Assert.AreEqual(trace1[i].Chime, trace2[i].Chime, "Chime mismatch at tick " + (25 + i));
                    Assert.AreEqual(trace1[i].DjWith, trace2[i].DjWith, "DoubleJumpWithIdle mismatch at tick " + (25 + i));
                    Assert.AreEqual(trace1[i].DjWithout, trace2[i].DjWithout, "DoubleJumpWithoutIdle mismatch at tick " + (25 + i));
                    Assert.AreEqual(trace1[i].DingCount, trace2[i].DingCount, "Event buffer mismatch at tick " + (25 + i));
                    Assert.AreEqual(trace1[i].Playbacks.Length, trace2[i].Playbacks.Length, "Playback count mismatch at tick " + (25 + i));
                    for (var s = 0; s < trace1[i].Playbacks.Length; s++)
                        Assert.AreEqual(trace1[i].Playbacks[s], trace2[i].Playbacks[s], "Playback slot " + s + " mismatch at tick " + (25 + i));
                }

                long warmAllocated = 0;
                var allOk = true;
                for (var i = 0; i < 10; i++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var capOk = snapshotter.Capture(entities, ref snapshot);
                    StepOnce();
                    var resOk = snapshotter.Restore(entities, in snapshot);
                    StepOnce();
                    warmAllocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    allOk &= capOk && resOk;
                }
                Assert.IsTrue(allOk);
                Assert.AreEqual(0L, warmAllocated, "Warm kind-bearing Capture, Restore, and step allocate zero managed bytes");
            }
            finally
            {
                snapshotter.Dispose();
                snapshot.Dispose();
            }
        }
    }
}
