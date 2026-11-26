using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Tl.Generated;
using Tl.Unity.Editor;
using Unity.Mathematics;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.Timeline;

namespace Tl.Unity.Record.Tests
{
    public class TlFrameRecorderTests
    {
        const string TempAssetsFolder = "Assets/TlFrameRecorderTestTmp";
        const string ScenePathKey = "TlFrameRecorderTests.ScenePath";
        const string OutAKey = "TlFrameRecorderTests.OutA";
        const string OutBKey = "TlFrameRecorderTests.OutB";

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (EditorApplication.isPlaying)
                yield return new ExitPlayMode();

            if (Directory.Exists(TempAssetsFolder))
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(TempAssetsFolder);
            }

            DeleteDirectoryIfPresent(SessionState.GetString(OutAKey, string.Empty));
            DeleteDirectoryIfPresent(SessionState.GetString(OutBKey, string.Empty));
            SessionState.EraseString(OutAKey);
            SessionState.EraseString(OutBKey);
            SessionState.EraseString(ScenePathKey);
        }

        [UnityTest]
        public IEnumerator RecordsTicksAndPositionsDeterministicallyAndWarnsWithoutGraphics()
        {
            var scenePath = BuildTestScene();
            var outDir = NewTempDirectory();
            File.WriteAllText(Path.Combine(outDir, "stale.txt"), "stale");
            SessionState.SetString(ScenePathKey, scenePath);
            SessionState.SetString(OutAKey, outDir);

            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            yield return new EnterPlayMode();

            var hasGraphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            if (!hasGraphics)
                LogAssert.Expect(LogType.Warning, new Regex("TlFrameRecorder: no graphics device.*-nographics", RegexOptions.IgnoreCase));

            yield return TlFrameRecorder.RecordInPlayMode(new TlFrameRecorder.Settings
            {
                Frames = 120,
                Every = 2,
                Width = 320,
                Height = 180,
                OutputFolder = SessionState.GetString(OutAKey, string.Empty)
            });

            yield return new ExitPlayMode();

            var result = TlFrameRecorder.LastResult;
            var recordedOut = SessionState.GetString(OutAKey, string.Empty);
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.AreEqual(120, result.FramesRecorded);
            Assert.AreEqual(60, result.RowsWritten);
            Assert.AreEqual(0, Time.captureFramerate);
            Assert.IsFalse(File.Exists(Path.Combine(recordedOut, "stale.txt")), "old files are cleared before recording");

            var csvPath = Path.Combine(recordedOut, "ticks.csv");
            Assert.IsTrue(File.Exists(csvPath), "ticks.csv is written");
            var rows = ParseCsv(csvPath);
            Assert.AreEqual(60, rows.Count, "ticks.csv has 120 / 2 rows");

            for (var i = 0; i < rows.Count; i++)
            {
                Assert.AreEqual(i * 2, rows[i].Frame, "row frame column matches captured frame index");
                if (i > 0)
                    Assert.AreEqual(2, rows[i].Tick - rows[i - 1].Tick, "tick advances by exactly every per row");
            }

            var byTick = new Dictionary<int, float3>();
            foreach (var row in rows)
                byTick[row.Tick] = row.Position;

            Assert.IsTrue(byTick.ContainsKey(20), "tick 20 present");
            Assert.IsTrue(byTick.ContainsKey(60), "tick 60 present");
            Assert.IsTrue(byTick.ContainsKey(100), "tick 100 present");
            Assert.AreEqual(0f, byTick[20].x, 1e-3f, "before clip start");
            Assert.AreEqual(3.875f, byTick[60].x, 1e-3f, "midpoint of 7.5 force over 31 ticks");
            Assert.AreEqual(7.5f, byTick[100].x, 1e-3f, "after clip end across 60 ticks");

            var pngs = Directory.GetFiles(recordedOut, "*.png");
            if (!hasGraphics)
            {
                Assert.AreEqual(0, result.PicturesWritten);
                Assert.AreEqual(0, pngs.Length, "no PNG pictures are written without a graphics device");
            }
            else
            {
                Assert.AreEqual(60, result.PicturesWritten);
                Assert.AreEqual(60, pngs.Length);
                Assert.IsTrue(File.Exists(Path.Combine(recordedOut, "tick_0020.png")));
                Assert.IsTrue(File.Exists(Path.Combine(recordedOut, "tick_0060.png")));
                Assert.IsTrue(File.Exists(Path.Combine(recordedOut, "tick_0100.png")));
            }
        }

        [UnityTest]
        public IEnumerator RecordsByteIdenticalPicturesAcrossTwoRunsWithGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Requires a real graphics device; run without -nographics to verify deterministic PNG pictures.");

            var scenePath = BuildTestScene();
            var outA = NewTempDirectory();
            var outB = NewTempDirectory();
            File.WriteAllText(Path.Combine(outB, "stale.png"), "stale");
            SessionState.SetString(ScenePathKey, scenePath);
            SessionState.SetString(OutAKey, outA);
            SessionState.SetString(OutBKey, outB);

            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            yield return new EnterPlayMode();
            yield return TlFrameRecorder.RecordInPlayMode(new TlFrameRecorder.Settings
            {
                Frames = 60,
                Every = 10,
                Width = 320,
                Height = 180,
                OutputFolder = SessionState.GetString(OutAKey, string.Empty)
            });
            yield return new ExitPlayMode();

            var resultA = TlFrameRecorder.LastResult;
            Assert.IsTrue(resultA != null && resultA.Succeeded, resultA?.Error);
            Assert.AreEqual(6, resultA.PicturesWritten);

            EditorSceneManager.OpenScene(SessionState.GetString(ScenePathKey, string.Empty), OpenSceneMode.Single);
            yield return new EnterPlayMode();
            yield return TlFrameRecorder.RecordInPlayMode(new TlFrameRecorder.Settings
            {
                Frames = 60,
                Every = 10,
                Width = 320,
                Height = 180,
                OutputFolder = SessionState.GetString(OutBKey, string.Empty)
            });
            yield return new ExitPlayMode();

            var resultB = TlFrameRecorder.LastResult;
            var dirA = SessionState.GetString(OutAKey, string.Empty);
            var dirB = SessionState.GetString(OutBKey, string.Empty);
            Assert.IsTrue(resultB != null && resultB.Succeeded, resultB?.Error);
            Assert.AreEqual(6, resultB.PicturesWritten);
            Assert.IsFalse(File.Exists(Path.Combine(dirB, "stale.png")));
            Assert.AreEqual(
                File.ReadAllText(Path.Combine(dirA, "ticks.csv")),
                File.ReadAllText(Path.Combine(dirB, "ticks.csv")),
                "ticks.csv matches across runs");

            for (var tick = 0; tick < 60; tick += 10)
            {
                var name = "tick_" + tick.ToString("D4", CultureInfo.InvariantCulture) + ".png";
                var pathA = Path.Combine(dirA, name);
                var pathB = Path.Combine(dirB, name);
                Assert.IsTrue(File.Exists(pathA), "missing " + pathA);
                Assert.IsTrue(File.Exists(pathB), "missing " + pathB);
                var bytesA = File.ReadAllBytes(pathA);
                var bytesB = File.ReadAllBytes(pathB);
                Assert.Greater(bytesA.Length, 8, "non-empty PNG " + name);
                Assert.AreEqual(0x89, bytesA[0]);
                Assert.AreEqual(0x50, bytesA[1]);
                Assert.AreEqual(0x4E, bytesA[2]);
                Assert.AreEqual(0x47, bytesA[3]);
                CollectionAssert.AreEqual(bytesA, bytesB, "PNG bytes are identical across runs for " + name);
            }
        }

        [Test]
        public void CommandLineParserAcceptsValidArgumentsAndAppliesDefaults()
        {
            var minimalOk = TlFrameRecorder.TryParseCommandLine(
                new[]
                {
                    "Unity",
                    "-batchmode",
                    "-projectPath", "C:/proj",
                    "-executeMethod", "Tl.Unity.Editor.TlFrameRecorder.RecordFromCommandLine",
                    "-tlRecordScene", "Assets/Scenes/Hello.unity",
                    "-tlRecordFrames", "240"
                },
                out var minimal,
                out var minimalError);

            Assert.IsTrue(minimalOk, minimalError);
            Assert.AreEqual("Assets/Scenes/Hello.unity", minimal.ScenePath);
            Assert.AreEqual(240, minimal.Frames);
            Assert.AreEqual(TlFrameRecorder.Settings.DefaultEvery, minimal.Every);
            Assert.AreEqual(TlFrameRecorder.Settings.DefaultWidth, minimal.Width);
            Assert.AreEqual(TlFrameRecorder.Settings.DefaultHeight, minimal.Height);
            Assert.IsNull(minimal.OutputFolder);

            var fullOk = TlFrameRecorder.TryParseCommandLine(
                new[]
                {
                    "-tlRecordScene", "Assets/Main.unity",
                    "-tlRecordFrames", "180",
                    "-tlRecordEvery", "3",
                    "-tlRecordOut", "Logs/Custom",
                    "-tlRecordWidth", "640",
                    "-tlRecordHeight", "360"
                },
                out var full,
                out var fullError);

            Assert.IsTrue(fullOk, fullError);
            Assert.AreEqual("Assets/Main.unity", full.ScenePath);
            Assert.AreEqual(180, full.Frames);
            Assert.AreEqual(3, full.Every);
            Assert.AreEqual("Logs/Custom", full.OutputFolder);
            Assert.AreEqual(640, full.Width);
            Assert.AreEqual(360, full.Height);
        }

        [Test]
        public void CommandLineParserRejectsBadArgumentsWithClearErrors()
        {
            AssertInvalidArgs(new[] { "-tlRecordFrames", "60" }, "-tlRecordScene");
            AssertInvalidArgs(new[] { "-tlRecordScene", "Assets/Hello.unity" }, "-tlRecordFrames");
            AssertInvalidArgs(new[] { "-tlRecordScene", "Assets/Hello.unity", "-tlRecordFrames", "0" }, "-tlRecordFrames");
            AssertInvalidArgs(new[] { "-tlRecordScene", "Assets/Hello.unity", "-tlRecordFrames", "abc" }, "-tlRecordFrames");
            AssertInvalidArgs(new[] { "-tlRecordScene", "Assets/Hello.unity", "-tlRecordFrames", "60", "-tlRecordEvery", "-1" }, "-tlRecordEvery");
            AssertInvalidArgs(new[] { "-tlRecordScene", "Assets/Hello.unity", "-tlRecordFrames", "60", "-tlRecordWidth", "0" }, "-tlRecordWidth");
            AssertInvalidArgs(new[] { "-tlRecordScene", "Assets/Hello.unity", "-tlRecordFrames", "60", "-tlRecordHeight", "-5" }, "-tlRecordHeight");
            AssertInvalidArgs(new[] { "-tlRecordScene", "Assets/Hello.unity", "-tlRecordFrames", "60", "-tlRecordOut" }, "-tlRecordOut");
            AssertInvalidArgs(new[] { "-tlRecordScene", "Assets/Hello.unity", "-tlRecordFrames", "60", "-tlRecordUnknown", "1" }, "-tlRecordUnknown");
        }

        [Test]
        public void CaptureFramerateStaysZeroWhenProjectTickRateIsRead()
        {
            var previous = (int)TlProjectSettings.instance.TicksPerSecond;
            try
            {
                TlProjectSettings.instance.SetTicksPerSecond(30);
                Assert.AreEqual(30u, TlBaking.TicksPerSecond);
                Assert.AreEqual(0, Time.captureFramerate);
            }
            finally
            {
                TlProjectSettings.instance.SetTicksPerSecond(previous);
            }
        }

        static void AssertInvalidArgs(string[] args, string expectedFlag)
        {
            var ok = TlFrameRecorder.TryParseCommandLine(args, out var settings, out var error);
            Assert.IsFalse(ok);
            Assert.IsNull(settings);
            StringAssert.Contains(expectedFlag, error);
        }

        static string NewTempDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "tl-record-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void DeleteDirectoryIfPresent(string dir)
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, true);
        }

        static string BuildTestScene()
        {
            if (Directory.Exists(TempAssetsFolder))
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(TempAssetsFolder);
            }
            AssetDatabase.CreateFolder("Assets", "TlFrameRecorderTestTmp");

            var timeline = TlTimelines.CreateAsset();
            AssetDatabase.CreateAsset(timeline, TempAssetsFolder + "/Move.playable");
            timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration = 2.5;
            var track = timeline.CreateTrack<TlTrack>(null, "Force");
            track.Template = new ForceClip_TlData();
            var clip = track.CreateClip<TlClip>();
            clip.displayName = "Push";
            clip.start = 0.5;
            clip.duration = 1.0;
            var data = (ForceClip_TlData)((TlClip)clip.asset).Data;
            data.Direction = new float3(1f, 0f, 0f);
            data.Strength = 7.5f;
            data.Falloff = AnimationCurve.Constant(0f, 1f, 1f);
            EditorUtility.SetDirty(clip.asset);
            EditorUtility.SetDirty(track);
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();

            var objects = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Actor";
            UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>());
            cube.transform.position = new Vector3(0f, 0.5f, 0f);
            var director = cube.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            director.playOnAwake = true;
            var objectsPath = TempAssetsFolder + "/Objects.unity";
            EditorSceneManager.SaveScene(objects, objectsPath);

            var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var camera = Camera.main;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.18f, 0.28f, 0.42f, 1f);
            camera.transform.position = new Vector3(3.75f, 4f, -10f);
            camera.transform.LookAt(new Vector3(3.75f, 0.5f, 0f));

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = new Vector3(3.75f, 0f, 0f);
            ground.transform.localScale = new Vector3(2f, 1f, 0.6f);

            var host = new GameObject("Objects");
            var subScene = host.AddComponent<SubScene>();
            subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(objectsPath);
            subScene.AutoLoadScene = true;

            var mainPath = TempAssetsFolder + "/Main.unity";
            EditorSceneManager.SaveScene(main, mainPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(objectsPath, ImportAssetOptions.ForceSynchronousImport);
            return mainPath;
        }

        static List<(int Frame, int Tick, float3 Position)> ParseCsv(string path)
        {
            var lines = File.ReadAllLines(path);
            Assert.Greater(lines.Length, 1);
            Assert.AreEqual("frame,tick,x,y,z", lines[0]);
            var rows = new List<(int, int, float3)>(lines.Length - 1);
            for (var i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;
                var parts = lines[i].Split(',');
                Assert.AreEqual(5, parts.Length, "csv row " + i + ": " + lines[i]);
                rows.Add((
                    int.Parse(parts[0], CultureInfo.InvariantCulture),
                    int.Parse(parts[1], CultureInfo.InvariantCulture),
                    new float3(
                        float.Parse(parts[2], CultureInfo.InvariantCulture),
                        float.Parse(parts[3], CultureInfo.InvariantCulture),
                        float.Parse(parts[4], CultureInfo.InvariantCulture))));
            }
            return rows;
        }
    }
}
