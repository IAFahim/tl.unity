using System;
using System.IO;
using Tl.Generated;
using Tl.Unity;
using Tl.Unity.Editor;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

[InitializeOnLoad]
public static class EventsDemoBuilder
{
    public const string SceneName = "Events Demo.unity";
    public const string ObjectsName = "Events Demo Objects.unity";
    const string Opened = "Tl.EventsDemo.Opened";
    static readonly int[] StrikeTicks = { 20, 50, 80 };

    static EventsDemoBuilder()
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

    [MenuItem("Tl/Samples/Build Events Demo")]
    public static void BuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        EditorSceneManager.OpenScene(Build());
    }

    public static string Build()
    {
        var root = Root() ?? throw new InvalidOperationException("EventsDemoBuilder.cs was not found under Assets.");
        var folder = root + "/Scenes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(root, "Scenes");
        var spark = CreateSpark(folder);
        var hurt = CreateHurt(folder + "/Hurt.playable");
        var combo = CreateCombo(folder + "/Combo.playable");

        var objects = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        hurt = AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/Hurt.playable");
        combo = AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/Combo.playable");
        spark = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Spark.prefab");
        var dummy = Shape(PrimitiveType.Cylinder, "Dummy", new Vector3(0.6f, 1f, 0f), new Color(0.75f, 0.55f, 0.3f), folder + "/Dummy.mat");
        dummy.transform.localScale = new Vector3(1f, 1f, 1f);
        var dummyDirector = dummy.AddComponent<PlayableDirector>();
        dummyDirector.playableAsset = hurt;
        dummyDirector.playOnAwake = false;
        dummy.AddComponent<ArenaDummyAuthoring>().Health = 100;

        var knight = Shape(PrimitiveType.Capsule, "Knight", new Vector3(-1.2f, 1f, 0f), new Color(0.8f, 0.2f, 0.2f), folder + "/Knight.mat");
        var knightDirector = knight.AddComponent<PlayableDirector>();
        knightDirector.playableAsset = combo;
        knightDirector.playOnAwake = true;
        var attacker = knight.AddComponent<ArenaAttackerAuthoring>();
        attacker.Target = dummy;
        attacker.Spark = spark;
        attacker.Hurt = hurt;
        EditorSceneManager.SaveScene(objects, folder + "/" + ObjectsName);

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var camera = Camera.main;
        camera.transform.position = new Vector3(-0.3f, 2.4f, -5.5f);
        camera.transform.LookAt(new Vector3(-0.3f, 0.9f, 0f));
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        UnityEngine.Object.DestroyImmediate(ground.GetComponent<Collider>());
        ground.transform.localScale = new Vector3(0.8f, 1f, 0.6f);
        new GameObject("Overlay").AddComponent<EventsOverlay>();
        new GameObject("Tl Panel").AddComponent<Tl.Unity.TlPanel>();
        var host = new GameObject("Events Demo Objects");
        var subScene = host.AddComponent<SubScene>();
        subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(folder + "/" + ObjectsName);
        subScene.AutoLoadScene = true;
        var scene = folder + "/" + SceneName;
        EditorSceneManager.SaveScene(main, scene);
        AssetDatabase.SaveAssets();
        return scene;
    }

    static GameObject Shape(PrimitiveType type, string name, Vector3 position, Color color, string materialPath)
    {
        var shape = GameObject.CreatePrimitive(type);
        shape.name = name;
        UnityEngine.Object.DestroyImmediate(shape.GetComponent<Collider>());
        shape.transform.position = position;
        var renderer = shape.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = Paint(renderer.sharedMaterial, color, materialPath);
        return shape;
    }

    static Material Paint(Material source, Color color, string path)
    {
        AssetDatabase.DeleteAsset(path);
        var material = new Material(source) { name = Path.GetFileNameWithoutExtension(path), color = color };
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    static GameObject CreateSpark(string folder)
    {
        var path = folder + "/Spark.prefab";
        AssetDatabase.DeleteAsset(path);
        var spark = Shape(PrimitiveType.Sphere, "Spark", Vector3.zero, new Color(1f, 0.85f, 0.2f), folder + "/Spark.mat");
        spark.transform.localScale = Vector3.one * 0.25f;
        var material = spark.GetComponent<MeshRenderer>().sharedMaterial;
        material.EnableKeyword("_EMISSION");
        material.SetColor("_EmissionColor", new Color(1f, 0.7f, 0.1f) * 2f);
        EditorUtility.SetDirty(material);
        spark.AddComponent<ArenaSparkAuthoring>();
        var prefab = PrefabUtility.SaveAsPrefabAsset(spark, path);
        UnityEngine.Object.DestroyImmediate(spark);
        return prefab;
    }

    static TimelineAsset CreateHurt(string path)
    {
        AssetDatabase.DeleteAsset(path);
        var timeline = TlTimelines.CreateAsset();
        AssetDatabase.CreateAsset(timeline, path);
        var track = timeline.CreateTrack<TlTrack>(null, "Squash");
        track.Template = new SquashClip_TlData();
        var clip = track.CreateClip<TlClip>();
        clip.start = 0;
        clip.duration = 0.3;
        var data = (SquashClip_TlData)((TlClip)clip.asset).Data;
        data.Amount = 0.35f;
        data.Shape = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.25f, 1f), new Keyframe(1f, 0f));
        EditorUtility.SetDirty(clip.asset);
        EditorUtility.SetDirty(track);
        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
        return timeline;
    }

    static TimelineAsset CreateCombo(string path)
    {
        AssetDatabase.DeleteAsset(path);
        var timeline = TlTimelines.CreateAsset();
        AssetDatabase.CreateAsset(timeline, path);
        var track = timeline.CreateTrack<TlTrack>(null, "Strike");
        track.Template = new StrikeClip_TlData();
        foreach (var tick in StrikeTicks)
        {
            var clip = track.CreateClip<TlClip>();
            clip.start = tick / 60.0;
            clip.duration = 10 / 60.0;
            var data = (StrikeClip_TlData)((TlClip)clip.asset).Data;
            data.Amount = 6;
            data.Reach = 0.6f;
            EditorUtility.SetDirty(clip.asset);
        }
        timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
        timeline.fixedDuration = 2;
        TlInspectorModel.SetSettings(timeline, TlEnd.Loop, null, 0);
        EditorUtility.SetDirty(track);
        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
        return timeline;
    }

    static string Root()
    {
        foreach (var guid in AssetDatabase.FindAssets("EventsDemoBuilder t:MonoScript"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == nameof(EventsDemoBuilder))
                return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace(Path.DirectorySeparatorChar, '/');
        }
        return null;
    }
}
