using System;
using System.IO;
using Tl;
using Tl.Generated;
using Tl.Mathematics;
using Tl.Pack.Motion;
using Tl.Unity;
using Tl.Unity.Editor;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

[InitializeOnLoad]
public static class MotionDemoBuilder
{
    public const string SceneName = "Motion Demo.unity";
    public const string ObjectsName = "Motion Demo Objects.unity";
    const string Opened = "Tl.MotionDemo.Opened";

    static MotionDemoBuilder()
    {
        if (Application.isBatchMode || AssetDatabase.IsAssetImportWorkerProcess() || SessionState.GetBool(Opened, false))
            return;
        SessionState.SetBool(Opened, true);
        EditorApplication.delayCall += OpenOnFirstImport;
    }

    static void OpenOnFirstImport()
    {
        var root = Root();
        if (root == null || File.Exists(root + "/Scenes/" + SceneName))
            return;
        BuildAndOpen();
    }

    [MenuItem("Tl/Samples/Build Motion Demo")]
    public static void BuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        EditorSceneManager.OpenScene(Build());
    }

    public static AnimationCurve Ramp() => Curve(i => i / (float)(TlCurve.Samples - 1));

    public static AnimationCurve Triangle() => Curve(i => 1f - Math.Abs(2f * i / (TlCurve.Samples - 1) - 1f));

    static AnimationCurve Curve(Func<int, float> value)
    {
        var keys = new Keyframe[TlCurve.Samples];
        for (var i = 0; i < keys.Length; i++)
            keys[i] = new Keyframe(i / (float)(TlCurve.Samples - 1), value(i));
        var curve = new AnimationCurve(keys);
        for (var i = 0; i < keys.Length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
        }
        return curve;
    }

    public static string Build()
    {
        var root = Root() ?? throw new InvalidOperationException("MotionDemoBuilder.cs was not found under Assets.");
        var folder = root + "/Scenes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(root, "Scenes");
        CreateGlide(folder + "/Glide.playable");
        CreateBreathe(folder + "/Breathe.playable");
        CreateHop(folder + "/Hop.playable");

        var objects = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Player(PrimitiveType.Cube, "Cube", new Vector3(-2f, 0f, 0f), new Color(0.2f, 0.5f, 0.9f), folder + "/Cube.mat", AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/Glide.playable"));
        Player(PrimitiveType.Sphere, "Sphere", new Vector3(0f, 0.5f, 2.5f), new Color(1f, 0f, 0f), folder + "/Sphere.mat", AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/Breathe.playable"));
        Player(PrimitiveType.Capsule, "Capsule", new Vector3(4f, 0f, 0f), new Color(0.3f, 0.8f, 0.35f), folder + "/Capsule.mat", AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/Hop.playable"));
        EditorSceneManager.SaveScene(objects, folder + "/" + ObjectsName);

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var camera = Camera.main;
        camera.transform.position = new Vector3(1.5f, 4f, -8f);
        camera.transform.LookAt(new Vector3(1.5f, 0.3f, 0.8f));
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        UnityEngine.Object.DestroyImmediate(ground.GetComponent<Collider>());
        ground.transform.position = new Vector3(1.5f, -0.75f, 1f);
        ground.transform.localScale = new Vector3(1.4f, 1f, 0.8f);
        new GameObject("Overlay").AddComponent<MotionOverlay>();
        var host = new GameObject("Motion Demo Objects");
        var subScene = host.AddComponent<SubScene>();
        subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(folder + "/" + ObjectsName);
        subScene.AutoLoadScene = true;
        var scene = folder + "/" + SceneName;
        EditorSceneManager.SaveScene(main, scene);
        AssetDatabase.SaveAssets();
        return scene;
    }

    static void Player(PrimitiveType type, string name, Vector3 position, Color color, string materialPath, TimelineAsset timeline)
    {
        var shape = GameObject.CreatePrimitive(type);
        shape.name = name;
        UnityEngine.Object.DestroyImmediate(shape.GetComponent<Collider>());
        shape.transform.position = position;
        var renderer = shape.GetComponent<MeshRenderer>();
        AssetDatabase.DeleteAsset(materialPath);
        var material = new Material(renderer.sharedMaterial) { name = Path.GetFileNameWithoutExtension(materialPath), color = color };
        AssetDatabase.CreateAsset(material, materialPath);
        renderer.sharedMaterial = material;
        var director = shape.AddComponent<PlayableDirector>();
        director.playableAsset = timeline;
        director.playOnAwake = true;
    }

    static TimelineAsset Timeline(string path, int ticks)
    {
        AssetDatabase.DeleteAsset(path);
        var timeline = TlTimelines.CreateAsset();
        AssetDatabase.CreateAsset(timeline, path);
        timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
        timeline.fixedDuration = ticks / 60.0;
        TlInspectorModel.SetSettings(timeline, TlEnd.Loop, null, 0);
        return timeline;
    }

    static T Clip<T>(TlTrack track, int start, int end) where T : TlClipData
    {
        var clip = track.CreateClip<TlClip>();
        clip.start = start / 60.0;
        clip.duration = (end - start) / 60.0;
        EditorUtility.SetDirty(clip.asset);
        return (T)((TlClip)clip.asset).Data;
    }

    static TlTrack Track(TimelineAsset timeline, string name, TlClipData template)
    {
        var track = timeline.CreateTrack<TlTrack>(null, name);
        track.Template = template;
        return track;
    }

    static void Save(TimelineAsset timeline)
    {
        foreach (var track in timeline.GetOutputTracks())
        {
            EditorUtility.SetDirty(track);
            foreach (var clip in track.GetClips())
                EditorUtility.SetDirty(clip.asset);
        }
        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
    }

    static void CreateGlide(string path)
    {
        var timeline = Timeline(path, 120);
        var move = Track(timeline, "Move", new Tl_Pack_Motion_MoveClip_TlData());
        var right = Clip<Tl_Pack_Motion_MoveClip_TlData>(move, 0, 60);
        right.From = new float3(-2f, 0f, 0f);
        right.To = new float3(2f, 0f, 0f);
        right.Ease = Ramp();
        var left = Clip<Tl_Pack_Motion_MoveClip_TlData>(move, 60, 120);
        left.From = new float3(2f, 0f, 0f);
        left.To = new float3(-2f, 0f, 0f);
        left.Ease = Ramp();
        Save(timeline);
    }

    static void CreateBreathe(string path)
    {
        var timeline = Timeline(path, 90);
        var swell = Clip<Tl_Pack_Motion_ScaleClip_TlData>(Track(timeline, "Scale", new Tl_Pack_Motion_ScaleClip_TlData()), 0, 90);
        swell.From = new float3(1f, 1f, 1f);
        swell.To = new float3(1.5f, 1.5f, 1.5f);
        swell.Ease = Triangle();
        var glow = Clip<Tl_Pack_Motion_TintClip_TlData>(Track(timeline, "Tint", new Tl_Pack_Motion_TintClip_TlData()), 0, 90);
        glow.From = new Rgba(1f, 0f, 0f, 1f);
        glow.To = new Rgba(1f, 1f, 0f, 1f);
        glow.Ease = Triangle();
        Save(timeline);
    }

    static void CreateHop(string path)
    {
        var timeline = Timeline(path, 120);
        var turn = Clip<Tl_Pack_Motion_SpinClip_TlData>(Track(timeline, "Spin", new Tl_Pack_Motion_SpinClip_TlData()), 0, 120);
        turn.FromYaw = 0f;
        turn.ToYaw = 360f;
        turn.Ease = Ramp();
        var jump = Clip<Tl_Pack_Motion_MoveClip_TlData>(Track(timeline, "Jump", new Tl_Pack_Motion_MoveClip_TlData()), 0, 120);
        jump.From = new float3(4f, 0f, 0f);
        jump.To = new float3(4f, 1f, 0f);
        jump.Ease = Triangle();
        var pull = Clip<Tl_Pack_Motion_MoveClip_TlData>(Track(timeline, "Pull", new Tl_Pack_Motion_MoveClip_TlData()), 40, 80);
        pull.From = new float3(4f, 0f, 0f);
        pull.To = new float3(5.5f, 0f, 0f);
        pull.Ease = Ramp();
        pull.Priority = 5;
        Save(timeline);
    }

    static string Root()
    {
        foreach (var guid in AssetDatabase.FindAssets("MotionDemoBuilder t:MonoScript"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == nameof(MotionDemoBuilder))
                return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace(Path.DirectorySeparatorChar, '/');
        }
        return null;
    }
}
