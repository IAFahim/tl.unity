using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    [InitializeOnLoad]
#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public static class TlPreview
    {
        sealed class Session : IDisposable
        {
            public World Baked;
            public BlobAssetStore Blobs;
            public long Version;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public uint LastTick = uint.MaxValue;

            public void Dispose()
            {
                if (Baked != null && Baked.IsCreated)
                    Baked.Dispose();
                if (Blobs.IsCreated)
                    Blobs.Dispose();
            }
        }

        public struct Result
        {
            public bool Ran;
            public int Steps;
            public uint Tick;
            public float3 Position;
            public quaternion Rotation;
            public float Scale;
        }

        static readonly Dictionary<PlayableDirector, Session> s_Sessions = new Dictionary<PlayableDirector, Session>();
        static List<Type> s_Systems;

        static TlPreview()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            TlPreviewHook.Evaluate = OnEvaluate;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.playModeStateChanged += _ => Clear();
            EditorApplication.update += ForgetStopped;
        }

        public static int TickOf(PlayableDirector director) => (int)TlTimelinePacker.Ticks(director.time, TlProjectSettings.instance.TicksPerSecond);

        static void OnEvaluate(PlayableDirector director)
        {
            if (director == null || EditorApplication.isPlayingOrWillChangePlaymode || !AnimationMode.InAnimationMode())
                return;
            var session = SessionOf(director, out var rebaked);
            if (session == null)
                return;
            var tick = (uint)TickOf(director);
            if (!rebaked && tick == session.LastTick)
                return;
            session.LastTick = tick;
            var result = Simulate(session, tick);
            if (result.Ran)
                Write(director.transform, result);
        }

        public static Result Evaluate(PlayableDirector director, double time)
        {
            var session = SessionOf(director, out _);
            if (session == null)
                return default;
            return Simulate(session, TlTimelinePacker.Ticks(time, TlProjectSettings.instance.TicksPerSecond));
        }

        public static void Clear()
        {
            foreach (var session in s_Sessions.Values)
                session.Dispose();
            s_Sessions.Clear();
        }

        static void ForgetStopped()
        {
            if (s_Sessions.Count > 0 && !AnimationMode.InAnimationMode())
                Clear();
        }

        static Session SessionOf(PlayableDirector director, out bool rebaked)
        {
            rebaked = false;
            var version = VersionOf(director);
            if (s_Sessions.TryGetValue(director, out var session) && session.Version == version)
                return session;
            var rest = session;
            var position = rest != null ? rest.Position : director.transform.localPosition;
            var rotation = rest != null ? rest.Rotation : director.transform.localRotation;
            var scale = rest != null ? rest.Scale : director.transform.localScale;
            session?.Dispose();
            s_Sessions.Remove(director);
            var fresh = new Session { Version = version, Position = position, Rotation = rotation, Scale = scale };
            try
            {
                fresh.Blobs = new BlobAssetStore(128);
                fresh.Baked = new World("Tl Preview Bake", WorldFlags.Editor);
                Bake(fresh.Baked, director.gameObject, fresh.Blobs);
            }
            catch (Exception error)
            {
                fresh.Dispose();
                Debug.LogWarning("Tl could not preview " + director.name + ": " + (error.InnerException ?? error).Message, director);
                return null;
            }
            s_Sessions[director] = fresh;
            rebaked = true;
            return fresh;
        }

        static long VersionOf(PlayableDirector director)
        {
            long version = EditorUtility.GetDirtyCount(director) + EditorUtility.GetDirtyCount(TlProjectSettings.instance);
            var also = director.GetComponent<TlAlsoPlays>();
            if (also != null)
                version += EditorUtility.GetDirtyCount(also);
            foreach (var timeline in TlInspectorModel.Plays(director, also))
            {
                version = version * 31 + EditorUtility.GetDirtyCount(timeline) + timeline.GetHashCode();
                var settings = TlDirectorTimelines.Settings(timeline);
                if (settings != null)
                    version = version * 31 + EditorUtility.GetDirtyCount(settings);
                foreach (var track in timeline.GetOutputTracks())
                {
                    if (!(track is TlTrack))
                        continue;
                    version = version * 31 + EditorUtility.GetDirtyCount(track) + (track.mutedInHierarchy ? 1 : 0);
                    foreach (var clip in track.GetClips())
                        version = version * 31 + EditorUtility.GetDirtyCount(clip.asset) + Mathf.RoundToInt((float)(clip.start * 1000.0)) * 7 + Mathf.RoundToInt((float)(clip.duration * 1000.0));
                }
            }
            return version;
        }

        static Result Simulate(Session session, uint tick)
        {
            using var world = new World("Tl Preview", WorldFlags.Editor);
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, Systems());
            var source = session.Baked.EntityManager;
            using (var all = source.UniversalQuery.ToEntityArray(Allocator.Temp))
                world.EntityManager.CopyEntitiesFrom(source, all);
            var entities = world.EntityManager;
            using var query = entities.CreateEntityQuery(ComponentType.ReadOnly<TlTimelineSet>(), ComponentType.ReadWrite<LocalTransform>(), ComponentType.ReadOnly<Playback>());
            if (query.IsEmpty)
                return default;
            var entity = query.GetSingletonEntity();
            entities.SetComponentData(entity, LocalTransform.FromPositionRotationScale(session.Position, session.Rotation, session.Scale.x));
            var init = world.GetExistingSystemManaged<InitializationSystemGroup>();
            var group = world.GetExistingSystemManaged<TlSimulationGroup>();
            var delta = 1f / TlProjectSettings.instance.TicksPerSecond;
            var result = new Result { Ran = true };
            var started = false;
            ushort timeline = 0;
            var handedOver = false;
            var held = entities.GetComponentData<LocalTransform>(entity);
            var limit = (int)tick + 64;
            for (var step = 0; step < limit; step++)
            {
                world.SetTime(new TimeData(step * (double)delta, delta));
                init.Update();
                group?.Update();
                entities.CompleteAllTrackedJobs();
                result.Steps = step + 1;
                var playbacks = entities.GetBuffer<Playback>(entity);
                if (playbacks.Length == 0 || playbacks[0].Timeline == 0)
                {
                    if (started)
                        break;
                    continue;
                }
                started = true;
                var playback = playbacks[0];
                if (timeline == 0)
                    timeline = playback.Timeline;
                else if (playback.Timeline != timeline)
                {
                    handedOver = true;
                    break;
                }
                if ((playback.Flags & global::Tl.PlaybackFlags.StartPending) != 0)
                    continue;
                result.Tick = playback.Tick;
                held = entities.GetComponentData<LocalTransform>(entity);
                if (playback.Tick >= tick)
                    break;
            }
            var transform = handedOver ? held : entities.GetComponentData<LocalTransform>(entity);
            result.Position = transform.Position;
            result.Rotation = transform.Rotation;
            result.Scale = transform.Scale;
            return result;
        }

        static void Write(Transform target, Result result)
        {
            Set(target, "m_LocalPosition.x", result.Position.x);
            Set(target, "m_LocalPosition.y", result.Position.y);
            Set(target, "m_LocalPosition.z", result.Position.z);
            Set(target, "m_LocalRotation.x", result.Rotation.value.x);
            Set(target, "m_LocalRotation.y", result.Rotation.value.y);
            Set(target, "m_LocalRotation.z", result.Rotation.value.z);
            Set(target, "m_LocalRotation.w", result.Rotation.value.w);
            Set(target, "m_LocalScale.x", result.Scale);
            Set(target, "m_LocalScale.y", result.Scale);
            Set(target, "m_LocalScale.z", result.Scale);
            target.localPosition = result.Position;
            target.localRotation = result.Rotation;
            target.localScale = Vector3.one * result.Scale;
        }

        static void Set(Transform target, string property, float value)
        {
            var binding = EditorCurveBinding.FloatCurve("", typeof(Transform), property);
            if (AnimationMode.IsPropertyAnimated(target, property))
                return;
            var serialized = new SerializedObject(target).FindProperty(property);
            var modification = new PropertyModification { target = target, propertyPath = property, value = serialized == null ? value.ToString(CultureInfo.InvariantCulture) : serialized.floatValue.ToString("R", CultureInfo.InvariantCulture) };
            AnimationMode.AddPropertyModification(binding, modification, true);
        }

        static List<Type> Systems()
        {
            if (s_Systems != null)
                return s_Systems;
            var always = new HashSet<Type> { typeof(TlLibrarySystem), typeof(TlTimelineRegisterSystem), typeof(TlPlayOnAwakeSystem), typeof(TlSimulationGroup), typeof(FixedStepSimulationSystemGroup), typeof(SimulationSystemGroup), typeof(InitializationSystemGroup) };
            var result = new List<Type>();
            var indices = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.Default);
            for (var i = 0; i < indices.Length; i++)
            {
                var type = TypeManager.GetSystemType(indices[i]);
                if (type != null && (always.Contains(type) || UnderTl(type)))
                    result.Add(type);
            }
            foreach (var type in always)
                if (!result.Contains(type))
                    result.Add(type);
            s_Systems = result;
            return result;
        }

        static bool UnderTl(Type type)
        {
            for (var depth = 0; depth < 16 && type != null; depth++)
            {
                var groups = type.GetCustomAttributes(typeof(UpdateInGroupAttribute), true);
                if (groups.Length == 0)
                    return false;
                type = ((UpdateInGroupAttribute)groups[0]).GroupType;
                if (type == typeof(TlSimulationGroup))
                    return true;
            }
            return false;
        }

        static void Bake(World world, GameObject root, BlobAssetStore blobs)
        {
            var hybrid = typeof(Baker<>).Assembly;
            var utility = hybrid.GetType("Unity.Entities.BakingUtility", true);
            var flags = Enum.Parse(utility.GetNestedType("BakingFlags", BindingFlags.Public | BindingFlags.NonPublic), "AssignName");
            var settings = Activator.CreateInstance(hybrid.GetType("Unity.Entities.BakingSettings", true), flags, blobs);
            utility.GetMethod("BakeGameObjects", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { world, new[] { root }, settings });
        }
    }
}
