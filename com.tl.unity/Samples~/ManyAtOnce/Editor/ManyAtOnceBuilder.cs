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
using UnityEngine.Rendering;
using UnityEngine.Timeline;

public static class ManyAtOnceBuilder
{
    public const string SceneName = "Many At Once.unity";
    const string ObjectsName = "Many At Once Objects.unity";
    const string RideName = "Ride.playable";
    const string ShakeName = "Shake.playable";

    [MenuItem("Tl Many At Once/Build Scene")]
    public static void BuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        EditorSceneManager.OpenScene(Build());
    }

    public static string Build()
    {
        var root = Root();
        var folder = root + "/Scenes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(root, "Scenes");
        LoadOrCreateRide(folder + "/" + RideName);
        LoadOrCreateShake(folder + "/" + ShakeName);
        var material = LoadOrCreateMaterial(folder + "/Platform.mat");

        var objects = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var ride = AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/" + RideName);
        var shake = AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/" + ShakeName);
        var platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
        platform.name = "Platform";
        UnityEngine.Object.DestroyImmediate(platform.GetComponent<Collider>());
        platform.transform.localScale = new Vector3(2f, 0.2f, 2f);
        platform.GetComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GetAssetPath(material));
        var director = platform.AddComponent<PlayableDirector>();
        director.playableAsset = ride;
        director.playOnAwake = true;
        var alsoPlays = TlInspectorModel.AlsoPlays(director, true);
        alsoPlays.Timelines.Add(shake);
        alsoPlays.PlayOnAwake.Add(shake);
        EditorSceneManager.SaveScene(objects, folder + "/" + ObjectsName);

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var camera = Camera.main;
        camera.transform.position = new Vector3(7f, 4f, -9f);
        camera.transform.LookAt(new Vector3(0f, 2.5f, 0f));
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(0.6f, 1f, 0.6f);
        var host = new GameObject("Many At Once Objects");
        var subScene = host.AddComponent<SubScene>();
        subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(folder + "/" + ObjectsName);
        subScene.AutoLoadScene = true;
        var scene = folder + "/" + SceneName;
        EditorSceneManager.SaveScene(main, scene);
        AssetDatabase.SaveAssets();
        if (GraphicsSettings.currentRenderPipeline == null)
            Debug.LogWarning("This project uses the Built-in render pipeline, so the SubScene platform stays invisible. Create the project from the Universal 3D template.");
        return scene;
    }

    static void LoadOrCreateRide(string path)
    {
        if (AssetDatabase.LoadAssetAtPath<TimelineAsset>(path) != null)
            return;
        var ride = CreateTimeline(path, 3.0);
        var floor = HeightTrack(ride, "Floor");
        AddHeight(floor, "Ground", 0.0, 2.0, 1f);
        AddHeight(floor, "Top", 1.5, 1.5, 5f);
        var wave = HeightTrack(ride, "Wave");
        AddHeight(wave, "Wave", 0.5, 0.5, 0.5f);
        Save(ride);
    }

    static void LoadOrCreateShake(string path)
    {
        if (AssetDatabase.LoadAssetAtPath<TimelineAsset>(path) != null)
            return;
        var shake = CreateTimeline(path, 0.5);
        AddHeight(HeightTrack(shake, "Bump"), "Bump", 0.25, 0.25, 0.25f);
        Save(shake);
    }

    static TimelineAsset CreateTimeline(string path, double seconds)
    {
        var timeline = TlTimelines.CreateAsset();
        AssetDatabase.CreateAsset(timeline, path);
        timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
        timeline.fixedDuration = seconds;
        return timeline;
    }

    static TlTrack HeightTrack(TimelineAsset timeline, string name)
    {
        var track = timeline.CreateTrack<TlTrack>(null, name);
        track.Template = new ManyAtOnce_HeightClip_TlData();
        return track;
    }

    static void AddHeight(TlTrack track, string name, double start, double duration, float height)
    {
        var clip = track.CreateClip<TlClip>();
        clip.displayName = name;
        clip.start = start;
        clip.duration = duration;
        ((ManyAtOnce_HeightClip_TlData)((TlClip)clip.asset).Data).Height = height;
        EditorUtility.SetDirty(clip.asset);
        EditorUtility.SetDirty(track);
    }

    static void Save(TimelineAsset timeline)
    {
        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
    }

    static Material LoadOrCreateMaterial(string path)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null)
            return material;
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        material = new Material(cube.GetComponent<MeshRenderer>().sharedMaterial) { name = "Platform", color = new Color(0.95f, 0.6f, 0.2f) };
        UnityEngine.Object.DestroyImmediate(cube);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    static string Root()
    {
        foreach (var guid in AssetDatabase.FindAssets("ManyAtOnceBuilder t:MonoScript"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == nameof(ManyAtOnceBuilder))
                return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace(Path.DirectorySeparatorChar, '/');
        }
        throw new InvalidOperationException("ManyAtOnceBuilder.cs was not found under Assets.");
    }
}
