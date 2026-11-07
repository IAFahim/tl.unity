using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Tl.Unity;
using Unity.Entities;
using Unity.Scenes;
using Unity.Scenes.Editor;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Timeline;

namespace ForceDemo.Tests
{
    public class ForceDemoLiveEditTests
    {
        [UnityTest]
        public IEnumerator EditingStrengthInPlaySwapsTheTimelineAndKeepsItsTime()
        {
            var scene = Build();
            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(Path.GetDirectoryName(scene).Replace('\\', '/') + "/Push.playable");
            var clip = timeline.GetOutputTracks().OfType<TlTrack>().First().GetClips().First();
            clip.duration = 20;
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(scene);
            SubSceneUtility.EditScene(UnityEngine.Object.FindAnyObjectByType<SubScene>());
            yield return new EnterPlayMode();

            var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
            var query = entities.CreateEntityQuery(ComponentType.ReadOnly<TlTimelineSet>(), ComponentType.ReadOnly<Playback>(), ComponentType.ReadOnly<LocalTransform>());
            var deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline && (query.IsEmpty || entities.GetBuffer<Playback>(query.GetSingletonEntity()).Length == 0))
                yield return null;
            Assert.IsFalse(query.IsEmpty, "the Cube entity appears");
            var entity = query.GetSingletonEntity();
            yield return Wait(0.5f);
            var speedBefore = 0f;
            yield return Speed(entities, entity, v => speedBefore = v);
            var id = entities.GetBuffer<TlTimelineSet>(entity)[0].Id;
            var content = entities.GetBuffer<TlTimelineSet>(entity)[0].Blob.Value.Content;
            var tickBefore = entities.GetBuffer<Playback>(entity)[0].Tick;

            var serialized = new SerializedObject(clip.asset);
            serialized.FindProperty("Data.Strength").floatValue = 12f;
            serialized.ApplyModifiedProperties();
            var edited = Time.realtimeSinceStartup;
            var latency = float.NaN;
            deadline = edited + 10f;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                var set = entities.GetBuffer<TlTimelineSet>(entity);
                if (set[0].Id != 0 && set[0].Blob.Value.Content != content)
                {
                    latency = Time.realtimeSinceStartup - edited;
                    break;
                }
            }
            var registered = entities.GetBuffer<TlTimelineSet>(entity)[0];
            var tickAfter = entities.GetBuffer<Playback>(entity)[0].Tick;
            var speedAfter = 0f;
            yield return Speed(entities, entity, v => speedAfter = v);
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "ForceDemo live edit latency={0:0.000}s id={1}->{2} tick={3}->{4} speed={5:0.00}->{6:0.00}", latency, id, registered.Id, tickBefore, tickAfter, speedBefore, speedAfter));
            yield return new ExitPlayMode();
            Build();

            Assert.Less(latency, 1f, "the edit reaches the running world within a second");
            Assert.AreEqual(id, registered.Id, "the timeline keeps its id");
            Assert.GreaterOrEqual(tickAfter, tickBefore, "the playback keeps its time");
            Assert.Less(tickAfter - tickBefore, 60 + (int)(latency * 60f) + 5, "the playback was not restarted");
            Assert.Greater(speedAfter, speedBefore * 2f, "Strength 12 pushes about three times faster than 4");
        }

        static IEnumerator Speed(EntityManager entities, Entity entity, Action<float> result)
        {
            var x0 = entities.GetComponentData<LocalTransform>(entity).Position.x;
            var t0 = Time.realtimeSinceStartup;
            yield return Wait(0.25f);
            result((entities.GetComponentData<LocalTransform>(entity).Position.x - x0) / (Time.realtimeSinceStartup - t0));
        }

        static IEnumerator Wait(float seconds)
        {
            var until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
                yield return null;
        }

        static string Build()
        {
            var builder = Type.GetType("ForceDemoBuilder, Assembly-CSharp-Editor");
            Assert.IsNotNull(builder, "ForceDemoBuilder is compiled into Assembly-CSharp-Editor");
            return (string)builder.GetMethod("Build").Invoke(null, null);
        }
    }
}
