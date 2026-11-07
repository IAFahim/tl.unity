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
        const float VisibleGap = 1.5f;
        const int ShotWidth = 640;
        const int ShotHeight = 360;

        [UnityTest]
        public IEnumerator TheCubeIsPushedAlongXByTheForceClip()
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
            Assert.IsTrue(entities.HasComponent(entity, ComponentType.ReadWrite(Game("ForceLog"))), "ForceLog buffer");
            Assert.IsTrue(entities.HasComponent(entity, ComponentType.ReadWrite(Game("ForceFalloff"))), "ForceFalloff");
            Assert.IsTrue(entities.HasComponent(entity, ComponentType.ReadWrite(Game("TlUnityGenerated.ForceKindEntry"))), "ForceKind entry buffer");

            var graphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && Camera.main != null;
            Texture2D startShot = null;
            var shotX = float.NaN;
            var started = false;
            deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (graphics && startShot == null)
                {
                    var x = entities.GetComponentData<LocalTransform>(entity).Position.x;
                    var shot = Render();
                    if (IsCube(shot, new Vector3(x, 0.5f, 0f)))
                    {
                        startShot = shot;
                        shotX = x;
                    }
                    else
                        UnityEngine.Object.Destroy(shot);
                }
                yield return null;
                var playing = false;
                foreach (var playback in entities.GetBuffer<Playback>(entity))
                    playing |= playback.Timeline != 0;
                if (playing)
                    started = true;
                else if (started)
                    break;
            }
            for (var i = 0; i < 5; i++)
                yield return null;
            var end = entities.GetComponentData<LocalTransform>(entity).Position;
            var endShot = graphics ? Render() : null;
            var startFile = Save(startShot, "ForceDemo-start.png");
            var endFile = Save(endShot, "ForceDemo-end.png");
            var cubeAtStart = IsCube(startShot, new Vector3(shotX, 0.5f, 0f));
            var goneFromStart = endShot != null && !IsCube(endShot, new Vector3(shotX, 0.5f, 0f));
            var cubeAtEnd = IsCube(endShot, new Vector3(end.x, 0.5f, 0f));
            var moved = end.x - StartX;
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "ForceDemo result started={0} endX={1:0.0000} movedX={2:0.0000} expected={3:0.0000} y={4:0.0000} z={5:0.0000} graphics={6} shotX={7:0.000} cubeAtStart={8} goneFromStart={9} cubeAtEnd={10} startShot={11} endShot={12}",
                started, end.x, moved, ExpectedPush, end.y, end.z, SystemInfo.graphicsDeviceType, shotX, cubeAtStart, goneFromStart, cubeAtEnd, startFile ?? "none", endFile ?? "none"));
            Assert.IsTrue(started, "the Push timeline started on its own (Play On Awake)");
            Assert.AreEqual(ExpectedPush, moved, Tolerance, "Strength 4 for 2 s with a falling linear curve pushes about 4 m");
            Assert.AreEqual(0.5f, end.y, 1e-4f);
            Assert.AreEqual(0f, end.z, 1e-4f);
            if (graphics)
            {
                Assert.IsTrue(cubeAtStart, "the camera shows the cube before it moved away");
                Assert.Greater(end.x - shotX, VisibleGap, "the first picture was taken early enough to show the move");
                Assert.IsTrue(goneFromStart, "the camera shows the cube gone from where the first picture had it");
                Assert.IsTrue(cubeAtEnd, "the camera shows the cube where it stopped");
            }
            UnityEngine.Object.Destroy(startShot);
            UnityEngine.Object.Destroy(endShot);
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

        static bool IsCube(Texture2D shot, Vector3 world)
        {
            var camera = Camera.main;
            if (shot == null || camera == null || float.IsNaN(world.x))
                return false;
            camera.aspect = ShotWidth / (float)ShotHeight;
            var view = camera.WorldToViewportPoint(world);
            camera.ResetAspect();
            var x = Mathf.Clamp(Mathf.RoundToInt(view.x * ShotWidth), 0, ShotWidth - 1);
            var y = Mathf.Clamp(Mathf.RoundToInt(view.y * ShotHeight), 0, ShotHeight - 1);
            var pixel = shot.GetPixel(x, y);
            return pixel.b - pixel.r > 0.25f;
        }
    }
}
#endif
