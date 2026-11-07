using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Tl.Unity;
using Tl.Unity.Editor;
using Unity.Entities;
using Unity.Scenes;
using Unity.Scenes.Editor;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.Timeline;

namespace ForceDemo.Tests
{
    public class ForceDemoPreviewTests
    {
        static readonly int[] Ticks = { 30, 60, 90 };

        [UnityTest]
        public IEnumerator ScrubbingInEditModeShowsWhatPlayDoes()
        {
            var scene = Build();
            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(Path.GetDirectoryName(scene).Replace('\\', '/') + "/Push.playable");
            var clip = timeline.GetOutputTracks().OfType<TlTrack>().First().GetClips().First();
            EditorSceneManager.OpenScene(scene);
            SubSceneUtility.EditScene(UnityEngine.Object.FindAnyObjectByType<SubScene>());
            yield return null;
            var director = UnityEngine.Object.FindAnyObjectByType<PlayableDirector>();
            Assert.IsNotNull(director, "the Cube with its Playable Director is loaded");
            var rest = director.transform.localPosition;
            var preview = new Dictionary<int, float>();
            float end, stronger, undone;
            string shot = null;
            AnimationMode.StartAnimationMode();
            try
            {
                foreach (var tick in Ticks)
                    preview[tick] = Scrub(director, tick / 60.0);
                end = Scrub(director, timeline.duration);
                shot = Shot("ForceDemo-preview.png", director.gameObject.scene);
                Undo.IncrementCurrentGroup();
                var serialized = new SerializedObject(clip.asset);
                serialized.FindProperty("Data.Strength").floatValue = 8f;
                serialized.ApplyModifiedProperties();
                stronger = Scrub(director, timeline.duration);
                Undo.PerformUndo();
                undone = Scrub(director, timeline.duration);
            }
            finally
            {
                AnimationMode.StopAnimationMode();
            }
            var restored = director.transform.localPosition;
            var sceneDirty = director.gameObject.scene.isDirty;

            var play = new Dictionary<int, float>();
            yield return new EnterPlayMode();
            var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
            var query = entities.CreateEntityQuery(ComponentType.ReadOnly<TlTimelineSet>(), ComponentType.ReadOnly<Playback>(), ComponentType.ReadOnly<LocalTransform>());
            var deadline = Time.realtimeSinceStartup + 60f;
            var started = false;
            var playEnd = float.NaN;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                if (query.IsEmpty)
                    continue;
                var entity = query.GetSingletonEntity();
                var playbacks = entities.GetBuffer<Playback>(entity);
                var x = entities.GetComponentData<LocalTransform>(entity).Position.x;
                if (playbacks.Length == 0 || playbacks[0].Timeline == 0)
                {
                    if (!started)
                        continue;
                    playEnd = x;
                    break;
                }
                started = true;
                var at = playbacks[0].Tick;
                if ((playbacks[0].Flags & global::Tl.PlaybackFlags.StartPending) == 0 && Ticks.Contains(at) && !play.ContainsKey(at))
                    play[at] = x;
            }
            yield return new ExitPlayMode();

            var line = string.Format(CultureInfo.InvariantCulture, "ForceDemo preview end={0:0.0000} play={1:0.0000} strength8={2:0.0000} undone={3:0.0000} rest={4} restored={5} sceneDirty={6} shot={7}", end, playEnd, stronger, undone, rest, restored, sceneDirty, shot ?? "none");
            foreach (var tick in Ticks)
                line += string.Format(CultureInfo.InvariantCulture, " tick{0}: preview={1:0.0000} play={2}", tick, preview[tick], play.TryGetValue(tick, out var p) ? p.ToString("0.0000", CultureInfo.InvariantCulture) : "missed");
            Debug.Log(line);
            Assert.AreEqual(4.0333f, end, 0.01f, "the end of Push in Edit mode");
            Assert.AreEqual(playEnd, end, 1e-3f, "Edit mode ends where Play ends");
            foreach (var tick in Ticks)
                if (play.TryGetValue(tick, out var x))
                    Assert.AreEqual(x, preview[tick], 1e-3f, "same x at tick " + tick);
            Assert.GreaterOrEqual(play.Count, 1, "Play was sampled at one of the compared ticks");
            Assert.AreEqual(8.0667f, stronger, 0.02f, "Strength 8 previews twice as far");
            Assert.AreEqual(end, undone, 1e-4f, "Undo brings the preview back");
            Assert.AreEqual(rest, restored, "leaving the preview puts the cube back");
            Assert.IsFalse(sceneDirty, "the preview leaves no change in the scene");
        }

        static float Scrub(PlayableDirector director, double time)
        {
            director.time = time;
            director.Evaluate();
            return director.transform.localPosition.x;
        }

        static string Shot(string file, UnityEngine.SceneManagement.Scene subScene)
        {
            var camera = Camera.main;
            if (camera == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return null;
            var target = RenderTexture.GetTemporary(640, 360, 24);
            var previous = RenderTexture.active;
            camera.targetTexture = target;
            var mask = EditorSceneManager.GetSceneCullingMask(subScene);
            EditorSceneManager.SetSceneCullingMask(subScene, EditorSceneManager.DefaultSceneCullingMask);
            camera.Render();
            EditorSceneManager.SetSceneCullingMask(subScene, mask);
            RenderTexture.active = target;
            var texture = new Texture2D(640, 360, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, 640, 360), 0, 0);
            texture.Apply();
            camera.targetTexture = null;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            var path = Path.GetFullPath(Path.Combine("Logs", file));
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            return path;
        }

        static string Build()
        {
            var builder = Type.GetType("ForceDemoBuilder, Assembly-CSharp-Editor");
            Assert.IsNotNull(builder, "ForceDemoBuilder is compiled into Assembly-CSharp-Editor");
            return (string)builder.GetMethod("Build").Invoke(null, null);
        }
    }
}
