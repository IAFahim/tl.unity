#if UNITY_EDITOR
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Tl.Unity;
using Unity.Entities;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ForceDemo.Tests
{
    public class ForceDemoTests
    {
        const float StartX = 0f;
        const float ExpectedPush = 4.0333f;
        const float Tolerance = 0.15f;
        const float PictureTolerance = 0.02f;
        const int ShotWidth = 640;
        const int ShotHeight = 360;

        [UnityTest]
        public IEnumerator TheCubeIsPushedThereAndBackAndTheCameraFollows()
        {
            var scene = ScenePath();
            Assert.IsNotNull(scene, "Build the demo first: Tl > Samples > Build Force Demo.");
            var warnings = 0;
            var errors = 0;
            var firstProblem = "";
            void Count(string message, string stack, LogType type)
            {
                if (type == LogType.Warning)
                    warnings++;
                else if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    errors++;
                else
                    return;
                if (firstProblem.Length == 0)
                    firstProblem = message;
            }
            Application.logMessageReceived += Count;
            var asyncShaders = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            try
            {
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new LoadSceneParameters(LoadSceneMode.Single));
                var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
                using var query = entities.CreateEntityQuery(ComponentType.ReadOnly(Game("Force")), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<TlTimelineSet>());
                var deadline = Time.realtimeSinceStartup + 120f;
                while (query.IsEmpty && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.IsFalse(query.IsEmpty, "the Cube entity with Force appears after the SubScene loads");
                var entity = query.GetSingletonEntity();
                Assert.IsTrue(entities.HasComponent(entity, ComponentType.ReadWrite(Game("ForceLog"))), "ForceLog buffer");
                Assert.IsTrue(entities.HasComponent(entity, ComponentType.ReadWrite(Game("ForceFalloff"))), "ForceFalloff");
                Assert.IsTrue(entities.HasComponent(entity, ComponentType.ReadWrite(Game("TlUnityGenerated.ForceKindEntry"))), "ForceKind entry buffer");
                var set = entities.GetBuffer<TlTimelineSet>(entity);
                Assert.AreEqual(2, set.Length, "Push and its Play next timeline Return are both baked onto the cube");
                var push = set[0].Id;
                var back = set[1].Id;

                var graphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && Camera.main != null;
                var pushedX = float.NaN;
                var backX = float.NaN;
                var picture = "skipped";
                var pictureError = float.NaN;
                string shotFile = null;
                var cameraLag = float.NaN;
                deadline = Time.realtimeSinceStartup + 60f;
                var laps = 0;
                var inGap = false;
                while (Time.realtimeSinceStartup < deadline && (float.IsNaN(backX) || graphics && picture != "ok" && laps < 3))
                {
                    yield return null;
                    var playbacks = entities.GetBuffer<Playback>(entity);
                    if (playbacks.Length == 0 || playbacks[0].Timeline == 0)
                        continue;
                    var playback = playbacks[0];
                    var x = entities.GetComponentData<LocalTransform>(entity).Position.x;
                    var gap = playback.Timeline == push && playback.Tick >= 165 && playback.Tick <= 178;
                    if (gap && !inGap)
                    {
                        laps++;
                        if (float.IsNaN(pushedX))
                        {
                            pushedX = x;
                            cameraLag = Camera.main != null ? Camera.main.transform.position.x - x : float.NaN;
                        }
                        if (graphics && picture != "ok")
                        {
                            var shot = Render();
                            pictureError = Centroid(shot, new Vector3(x, 0.5f, 0f));
                            picture = pictureError <= PictureTolerance ? "ok" : "off";
                            shotFile = Save(shot, "ForceDemo-pushed.png");
                            UnityEngine.Object.Destroy(shot);
                        }
                    }
                    inGap = gap;
                    if (playback.Timeline == back && playback.Tick >= 125 && playback.Tick <= 175 && !float.IsNaN(pushedX) && float.IsNaN(backX))
                        backX = x;
                }
                var line = string.Format(CultureInfo.InvariantCulture, "TL-DEMO force x={0:0.0000} back={1:0.0000} expected={2:0.0000} warnings={3} errors={4} picture={5} pictureError={6:0.0000} cameraLag={7:0.000} pictureLap={8} shot={9}",
                    pushedX - StartX, backX, ExpectedPush, warnings, errors, picture, pictureError, cameraLag, laps, shotFile ?? "none");
                Debug.Log(line);
                Assert.AreEqual(ExpectedPush, pushedX - StartX, 0.0005f, "Strength 4 for 2 s with a falling linear curve pushes 4.0333 m, then the 1 s gap holds it there");
                Assert.AreEqual(0f, backX, 0.0005f, "Return (Play next) pushes the cube back to where it started");
                Assert.AreEqual(0, warnings, "no warnings while the demo plays: " + firstProblem);
                Assert.AreEqual(0, errors, "no errors while the demo plays: " + firstProblem);
                if (graphics)
                {
                    Assert.AreEqual(0f, cameraLag, 0.05f, "the follow camera has settled over the cube");
                    Assert.AreEqual("ok", picture, "the cube is drawn within 2% of where the camera projects x 4.0333");
                }
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncShaders;
                Application.logMessageReceived -= Count;
            }
        }

        [UnityTest]
        public IEnumerator TheControlCubeDashesWhenAJobSeesItFarEnough()
        {
            var scene = ScenePath("Control Demo");
            Assert.IsNotNull(scene, "Build the demo first: Tl > Samples > Build Control Demo.");
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new LoadSceneParameters(LoadSceneMode.Single));
            var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = entities.CreateEntityQuery(ComponentType.ReadOnly(Game("DashWhenFar")), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<Playback>());
            var deadline = Time.realtimeSinceStartup + 120f;
            while (query.IsEmpty && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsFalse(query.IsEmpty, "the Control cube appears");
            var entity = query.GetSingletonEntity();
            var dashAtX = float.NaN;
            var started = false;
            deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                var live = 0;
                foreach (var playback in entities.GetBuffer<Playback>(entity))
                    if (playback.Timeline != 0)
                        live++;
                if (live == 2 && float.IsNaN(dashAtX))
                    dashAtX = entities.GetComponentData<LocalTransform>(entity).Position.x;
                if (live > 0)
                    started = true;
                else if (started)
                    break;
            }
            var end = entities.GetComponentData<LocalTransform>(entity).Position;
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "ControlDemo result dashAtX={0:0.0000} endX={1:0.0000} endZ={2:0.0000}", dashAtX, end.x, end.z));
            Assert.AreEqual(2f, dashAtX, 0.15f, "the job started Dash when the cube passed x 2");
            Assert.AreEqual(ExpectedPush, end.x, Tolerance, "Push still pushes about 4 m along X");
            Assert.AreEqual(3.05f, end.z, Tolerance, "Dash pushed about 3 m along Z");
        }

        [UnityTest]
        public IEnumerator TheCubeWalksBackWhenItIsRewoundByReverseOrByTheWorld()
        {
            var scene = ScenePath();
            Assert.IsNotNull(scene, "Build the demo first: Tl > Samples > Build Force Demo.");
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new LoadSceneParameters(LoadSceneMode.Single));
            var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = entities.CreateEntityQuery(ComponentType.ReadOnly(Game("Force")), ComponentType.ReadOnly<LocalTransform>(), ComponentType.ReadOnly<TlTimelineSet>());
            var deadline = Time.realtimeSinceStartup + 120f;
            while (query.IsEmpty && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsFalse(query.IsEmpty, "the Cube entity with Force appears after the SubScene loads");
            var entity = query.GetSingletonEntity();
            var panel = UnityEngine.Object.FindFirstObjectByType<TlPanel>();
            Assert.IsNotNull(panel, "the Force demo scene has a Tl Panel");
            while (entities.GetComponentData<LocalTransform>(entity).Position.x < 1.5f && Time.realtimeSinceStartup < deadline)
                yield return null;
            var pushed = entities.GetComponentData<LocalTransform>(entity).Position.x;
            Assert.GreaterOrEqual(pushed, 1.5f, "the push moves the cube forward first");
            panel.WorldForward = false;
            var until = Time.realtimeSinceStartup + 0.6f;
            while (Time.realtimeSinceStartup < until)
                yield return null;
            var rewound = entities.GetComponentData<LocalTransform>(entity).Position.x;
            Assert.Less(rewound, pushed - 0.2f, "the world rewind walks the cube back: " + pushed + " -> " + rewound);
            panel.WorldForward = true;
            Assert.IsTrue(panel.Restart());
            while (entities.GetComponentData<LocalTransform>(entity).Position.x < 1.5f && Time.realtimeSinceStartup < deadline)
                yield return null;
            var ahead = entities.GetComponentData<LocalTransform>(entity).Position.x;
            Assert.GreaterOrEqual(ahead, 1.5f, "the push moves the cube forward again after a restart");
            Assert.IsTrue(panel.ToggleReverse());
            until = Time.realtimeSinceStartup + 0.4f;
            while (Time.realtimeSinceStartup < until)
                yield return null;
            var back = entities.GetComponentData<LocalTransform>(entity).Position.x;
            Assert.Less(back, ahead - 0.1f, "Reverse on the timeline also walks the cube back: " + ahead + " -> " + back);
        }

        static Type Game(string name)
        {
            var type = Type.GetType(name + ", Assembly-CSharp");
            Assert.IsNotNull(type, name + " is compiled into Assembly-CSharp");
            return type;
        }

        static string ScenePath(string name = "Force Demo")
        {
            foreach (var guid in AssetDatabase.FindAssets("t:SceneAsset " + name))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == name)
                    return path;
            }
            return null;
        }

        static Texture2D Render()
        {
            var camera = Camera.main;
            var target = RenderTexture.GetTemporary(ShotWidth, ShotHeight, 24);
            var previous = RenderTexture.active;
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            var shot = new Texture2D(ShotWidth, ShotHeight, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
            shot.Apply();
            camera.targetTexture = null;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            return shot;
        }

        static string Save(Texture2D shot, string file)
        {
            if (shot == null)
                return null;
            var path = Path.GetFullPath(Path.Combine("Logs", file));
            File.WriteAllBytes(path, shot.EncodeToPNG());
            return path;
        }

        static float Centroid(Texture2D shot, Vector3 world)
        {
            var camera = Camera.main;
            camera.aspect = ShotWidth / (float)ShotHeight;
            var view = camera.WorldToViewportPoint(world);
            camera.ResetAspect();
            double sx = 0, sy = 0;
            var count = 0;
            for (var y = 0; y < ShotHeight; y++)
                for (var x = 0; x < ShotWidth; x++)
                {
                    var pixel = shot.GetPixel(x, y);
                    if (pixel.r < 0.35f && pixel.b - pixel.r > 0.35f && pixel.b - pixel.g > 0.15f)
                    {
                        sx += x;
                        sy += y;
                        count++;
                    }
                }
            if (count < 50)
                return float.PositiveInfinity;
            var dx = (float)(sx / count) / ShotWidth - view.x;
            var dy = (float)(sy / count) / ShotHeight - view.y;
            return Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
        }
    }
}
#endif
