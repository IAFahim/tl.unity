#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Tl;
using Tl.Mathematics;
using Tl.Pack.Motion;
using Tl.Unity;
using Pose = Tl.Pack.Motion.Pose;
using Playback = Tl.Unity.Playback;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MotionDemo.Tests
{
    public class MotionDemoTests
    {
        static TlCurve Ramp()
        {
            var curve = default(TlCurve);
            for (var i = 0; i < TlCurve.Samples; i++) curve[i] = i / (float)(TlCurve.Samples - 1);
            return curve;
        }

        static TlCurve Triangle()
        {
            var curve = default(TlCurve);
            for (var i = 0; i < TlCurve.Samples; i++) curve[i] = 1f - Math.Abs(2f * i / (TlCurve.Samples - 1) - 1f);
            return curve;
        }

        static float P(int within, int length) => length == 0 ? 0f : within / (float)length;

        static float3 GlideAt(int tick) => tick < 60
            ? Ease.Lerp(new float3(-2f, 0f, 0f), new float3(2f, 0f, 0f), Ramp().Evaluate(P(tick, 60)))
            : Ease.Lerp(new float3(2f, 0f, 0f), new float3(-2f, 0f, 0f), Ramp().Evaluate(P(tick - 60, 60)));

        static float3 HopAt(int tick) => tick >= 40 && tick < 80
            ? Ease.Lerp(new float3(4f, 0f, 0f), new float3(5.5f, 0f, 0f), Ramp().Evaluate(P(tick - 40, 40)))
            : Ease.Lerp(new float3(4f, 0f, 0f), new float3(4f, 1f, 0f), Triangle().Evaluate(P(tick, 120)));

        static float3 SwellAt(int tick) => Ease.Lerp(new float3(1f, 1f, 1f), new float3(1.5f, 1.5f, 1.5f), Triangle().Evaluate(P(tick, 90)));

        static bool Same(float3 a, float3 b)
            => BitConverter.SingleToInt32Bits(a.x) == BitConverter.SingleToInt32Bits(b.x)
            && BitConverter.SingleToInt32Bits(a.y) == BitConverter.SingleToInt32Bits(b.y)
            && BitConverter.SingleToInt32Bits(a.z) == BitConverter.SingleToInt32Bits(b.z);

        [UnityTest]
        public IEnumerator CubeAndCapsulePositionsEqualTheDotNetReference()
        {
            var scene = ScenePath("Motion Demo");
            Assert.IsNotNull(scene, "Build the demo first: Tl > Samples > Build Motion Demo.");
            var problems = 0;
            var firstProblem = "";
            void Count(string message, string stack, LogType type)
            {
                if (type == LogType.Log)
                    return;
                problems++;
                if (firstProblem.Length == 0)
                    firstProblem = message;
            }
            Application.logMessageReceived += Count;
            try
            {
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scene, new LoadSceneParameters(LoadSceneMode.Single));
                var entities = World.DefaultGameObjectInjectionWorld.EntityManager;
                using var players = entities.CreateEntityQuery(ComponentType.ReadOnly<Pose>(), ComponentType.ReadOnly<Playback>());
                using var tinted = entities.CreateEntityQuery(ComponentType.ReadOnly<Tint>(), ComponentType.ReadOnly<Pose>(), ComponentType.ReadOnly<Playback>());
                var deadline = Time.realtimeSinceStartup + 120f;
                while (players.CalculateEntityCount() < 3 && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.AreEqual(3, players.CalculateEntityCount(), "Cube, Sphere and Capsule get Pose after the SubScene loads");
                Assert.AreEqual(1, tinted.CalculateEntityCount(), "only the Sphere plays a Tint clip, so only it gets Tint");
                var sphere = tinted.GetSingletonEntity();
                Entity cube = default, capsule = default;
                while ((cube == Entity.Null || capsule == Entity.Null) && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    using var all = players.ToEntityArray(Allocator.Temp);
                    foreach (var entity in all)
                    {
                        var has = entities.GetComponentData<Pose>(entity).Has;
                        if (has == 1)
                            cube = entity;
                        else if (has == 5)
                            capsule = entity;
                    }
                }
                Assert.AreNotEqual(Entity.Null, cube, "the Cube's Glide wrote a position");
                Assert.AreNotEqual(Entity.Null, capsule, "the Capsule's Hop wrote a position and a yaw");
                var seen = new HashSet<int>();
                var samples = 0;
                var mismatches = 0;
                var mismatch = "";
                deadline = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < deadline && !(seen.Contains(29) && seen.Contains(59) && seen.Contains(89) && seen.Contains(119) && samples >= 200))
                {
                    yield return null;
                    var cubeTick = TickOf(entities, cube);
                    var capsuleTick = TickOf(entities, capsule);
                    var sphereTick = TickOf(entities, sphere);
                    if (cubeTick < 0 || capsuleTick < 0 || sphereTick < 0)
                        continue;
                    var cubePose = entities.GetComponentData<Pose>(cube);
                    var capsulePose = entities.GetComponentData<Pose>(capsule);
                    var spherePose = entities.GetComponentData<Pose>(sphere);
                    if (cubePose.Has == 0)
                        continue;
                    samples++;
                    seen.Add(cubeTick);
                    var ok = Same(GlideAt(cubeTick), cubePose.Position) && Same(HopAt(capsuleTick), capsulePose.Position) && Same(SwellAt(sphereTick), spherePose.Scale)
                        && cubePose.Has == 1 && spherePose.Has == 2 && capsulePose.Has == 5;
                    if (!ok)
                    {
                        mismatches++;
                        if (mismatch.Length == 0)
                            mismatch = string.Format(CultureInfo.InvariantCulture, "tick {0}: cube {1} want {2}; capsule tick {3} {4} want {5}", cubeTick, cubePose.Position.x, GlideAt(cubeTick).x, capsuleTick, capsulePose.Position.x, HopAt(capsuleTick).x);
                    }
                }
                var tint = entities.GetComponentData<Tint>(sphere);
                Debug.Log(string.Format(CultureInfo.InvariantCulture, "TL-DEMO motion samples={0} mismatches={1} ticks29-59-89-119={2} sphereTint={3},{4},{5} problems={6} first={7} {8}",
                    samples, mismatches, seen.Contains(29) && seen.Contains(59) && seen.Contains(89) && seen.Contains(119), tint.Color.R, tint.Color.G, tint.Color.B, problems, mismatch, firstProblem));
                Assert.AreEqual(0, mismatches, mismatch);
                Assert.GreaterOrEqual(samples, 200);
                Assert.IsTrue(seen.Contains(29) && seen.Contains(59) && seen.Contains(89) && seen.Contains(119), "the .NET checkpoints (steps 30, 60, 90, 120) were sampled");
                Assert.AreEqual(1, tint.Has);
                Assert.AreEqual(0, problems, firstProblem);
            }
            finally
            {
                Application.logMessageReceived -= Count;
            }
        }

        static int TickOf(EntityManager entities, Entity entity)
        {
            var playbacks = entities.GetBuffer<Playback>(entity);
            for (var i = 0; i < playbacks.Length; i++)
                if (playbacks[i].Timeline != 0)
                    return playbacks[i].Tick;
            return -1;
        }

        static string ScenePath(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets(name + " t:Scene"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == name)
                    return path;
            }
            return null;
        }
    }
}
#endif
