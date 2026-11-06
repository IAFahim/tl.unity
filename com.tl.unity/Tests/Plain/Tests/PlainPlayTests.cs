using System;
using System.Collections;
using NUnit.Framework;
using Tl;
using UnityEngine;
using UnityEngine.TestTools;

namespace PlainGame.Tests
{
    public class PlainPlayTests
    {
        const string LibraryPath = "Packages/com.tl.unity/Tests/Plain/Sample/game.bytes";

        static TextAsset Library()
        {
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(LibraryPath);
            Assert.IsNotNull(asset, LibraryPath);
            return asset;
#else
            Assert.Ignore("The test library is loaded through the AssetDatabase.");
            return null;
#endif
        }

        static PlainHost NewHost(int rows = PlainScript.Rows) => new PlainHost(Library().bytes, rows);

        [Test]
        public void SixHundredStepScriptHashesLikeDotNet()
        {
            using (var host = NewHost())
            {
                var hash = PlainScript.Play(host, PlainScript.Steps);
                Assert.AreEqual(PlainScript.Pinned, hash);
                Assert.AreEqual(0L, host.DroppedPulses);
            }
        }

        [Test]
        public void EventScriptRaisesTheSameCuesAsTheEcsHost()
        {
            using (var host = NewHost())
                Assert.AreEqual(PlainScript.EventsPinned, PlainScript.PlayEvents(host, PlainScript.Steps));
        }

        [Test]
        public void TwoSessionsInOneDomainGiveTheSameHashAndInstallTheModuleOnce()
        {
            PlainHost first;
            using (first = NewHost())
                Assert.AreEqual(PlainScript.Pinned, PlainScript.Play(first, PlainScript.Steps));
            Assert.IsTrue(first.IsDisposed);
            using (var second = NewHost())
                Assert.AreEqual(PlainScript.Pinned, PlainScript.Play(second, PlainScript.Steps));
            var mine = typeof(PlainHost).Assembly;
            var installed = 0;
            foreach (var module in TlGenerated.Modules)
                if (module.GetType().Assembly == mine)
                    installed++;
            Assert.AreEqual(1, installed);
        }

        [Test]
        public void StartStopPauseAndRewindMoveOneRow()
        {
            using (var host = NewHost(2))
            {
                host.Start(0, Timelines.Run);
                host.Step();
                Assert.AreEqual(2f, host.Motion[0].X);
                host.Step();
                host.Step();
                Assert.AreEqual(6f, host.Motion[0].X);
                Assert.IsTrue(host.TryTick(0, Timelines.Run, out var tick));
                Assert.AreEqual(2, tick);
                host.SetPaused(0, Timelines.Run, true);
                Assert.IsTrue(host.IsPaused(0, Timelines.Run));
                for (var i = 0; i < 3; i++)
                    host.Step();
                Assert.AreEqual(6f, host.Motion[0].X);
                host.TryTick(0, Timelines.Run, out tick);
                Assert.AreEqual(2, tick);
                host.SetPaused(0, Timelines.Run, false);
                host.Step();
                Assert.AreEqual(8f, host.Motion[0].X);
                for (var i = 0; i < 4; i++)
                    host.Step(false);
                Assert.AreEqual(0f, host.Motion[0].X);
                Assert.AreEqual(0f, host.Motion[1].X);
                Assert.IsTrue(host.IsPlaying(0, Timelines.Run));
                host.Stop(0, Timelines.Run);
                host.Step();
                Assert.IsFalse(host.IsPlaying(0, Timelines.Run));
                Assert.AreEqual(0, host.Playing(0));
            }
        }

        [Test]
        public void ATimelineCueIsReadOnceAfterTheStepThatRaisedIt()
        {
            using (var host = NewHost(3))
            {
                host.Start(1, Timelines.Whistle);
                host.Step();
                var cues = host.DrainCues();
                Assert.AreEqual(1, cues.Length);
                Assert.AreEqual(1, cues[0].Entity.Index);
                Assert.AreEqual(7, cues[0].Value.Voice);
                host.Step();
                Assert.AreEqual(0, host.DrainCues().Length);
                for (var i = 0; i < 10; i++)
                    host.Step();
                Assert.IsFalse(host.IsPlaying(1, Timelines.Whistle));
            }
        }

        [Test]
        public void GamePulsesAndClipsWriteTheRowColumns()
        {
            using (var host = NewHost(4))
            {
                host.Start(2, Timelines.Jump);
                host.Damage(3, 7);
                host.Step();
                Assert.AreEqual(6f, host.Motion[2].Y);
                Assert.AreEqual(-1, host.Stamina[2].Value);
                Assert.AreEqual(993, host.Health[3].Value);
                Assert.AreEqual(1000, host.Health[2].Value);
                Assert.AreEqual(0L, host.DroppedPulses);
            }
        }

        [Test]
        public void AStepAfterWarmUpAllocatesNothing()
        {
            using (var host = NewHost())
            {
                host.Start(0, Timelines.Run);
                host.Start(1, Timelines.Jump);
                for (var i = 0; i < 200; i++)
                    host.Step();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 1000; i++)
                    host.Step();
                var delta = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(0L, delta);
            }
        }

        [UnityTest]
        public IEnumerator TheRunnerStepsInFixedUpdateMovesATransformAndDisposesOnDestroy()
        {
            var go = new GameObject("PlainRunner");
            go.SetActive(false);
            var target = new GameObject("Target").transform;
            var runner = go.AddComponent<PlainRunner>();
            runner.Library = Library();
            runner.Targets = new[] { target };
            runner.Keyboard = false;
            var cues = 0;
            runner.CueRaised += (row, voice) => cues++;
            go.SetActive(true);
            var host = runner.Host;
            Assert.IsNotNull(host);
            runner.StartTimeline(0, Timelines.Whistle);
            var deadline = Time.realtimeSinceStartup + 60f;
            while (runner.Steps < 30 && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.GreaterOrEqual(runner.Steps, 30);
            Assert.AreEqual(2f * runner.Steps, host.Motion[0].X);
            Assert.AreEqual(host.Motion[0].X / runner.UnitsPerMeter, target.position.x, 1e-4f);
            Assert.AreEqual(1, cues);
            UnityEngine.Object.Destroy(go);
            yield return null;
            Assert.IsTrue(host.IsDisposed);
            Assert.IsNull(runner.Host);
            UnityEngine.Object.Destroy(target.gameObject);
        }
    }
}
