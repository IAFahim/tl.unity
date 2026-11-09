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

public static class HelloTlBuilder
{
    const string TimelineFile = "Teleport.playable";
    const string ObjectsFile = "Hello Objects.unity";
    const string SceneFile = "Hello.unity";

    [MenuItem("Tl Hello/Build Scene")]
    public static void BuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        EditorSceneManager.OpenScene(Build());
    }

    [MenuItem("Tl Hello/Export Library (HelloTl.tlib)")]
    public static void ExportLibrary()
    {
        var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(Folder() + "/" + TimelineFile);
        if (timeline == null)
            throw new InvalidOperationException("Run Tl Hello > Build Scene first.");
        var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "HelloTl.tlib"));
        TlExport.Export(new[] { timeline }, path);
        Debug.Log("Hello Tl: exported the library to " + path + ". Copy it next to the .NET program as HelloTl.tlib.");
    }

    public static string Build()
    {
        var folder = Folder();
        var timeline = CreateTimeline(folder + "/" + TimelineFile);

        var objects = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var player = GameObject.CreatePrimitive(PrimitiveType.Cube);
        player.name = "Player";
        UnityEngine.Object.DestroyImmediate(player.GetComponent<Collider>());
        player.transform.position = new Vector3(0f, 0.5f, 0f);
        var material = new Material(player.GetComponent<MeshRenderer>().sharedMaterial) { name = "Player", color = new Color(0.2f, 0.45f, 0.9f) };
        AssetDatabase.DeleteAsset(folder + "/Player.mat");
        AssetDatabase.CreateAsset(material, folder + "/Player.mat");
        player.GetComponent<MeshRenderer>().sharedMaterial = material;
        var director = player.AddComponent<PlayableDirector>();
        director.playableAsset = timeline;
        director.playOnAwake = true;
        EditorSceneManager.SaveScene(objects, folder + "/" + ObjectsFile);

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var camera = Camera.main;
        camera.transform.position = new Vector3(6.5f, 6f, -13f);
        camera.transform.LookAt(new Vector3(6.5f, 0.5f, 0f));
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = new Vector3(6.5f, 0f, 0f);
        ground.transform.localScale = new Vector3(2f, 1f, 0.6f);
        new GameObject("Readout").AddComponent<PlayerReadout>();
        var host = new GameObject("Hello Objects");
        var subScene = host.AddComponent<SubScene>();
        subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(folder + "/" + ObjectsFile);
        subScene.AutoLoadScene = true;
        var scene = folder + "/" + SceneFile;
        EditorSceneManager.SaveScene(main, scene);
        AssetDatabase.SaveAssets();
        if (GraphicsSettings.currentRenderPipeline == null)
            Debug.LogWarning("This project uses the Built-in render pipeline, so the SubScene cube stays invisible. Create the project from the Universal 3D template.");
        return scene;
    }

    static TimelineAsset CreateTimeline(string path)
    {
        AssetDatabase.DeleteAsset(path);
        var timeline = TlTimelines.CreateAsset();
        AssetDatabase.CreateAsset(timeline, path);
        timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
        timeline.fixedDuration = 2.5;
        var track = timeline.CreateTrack<TlTrack>(null, "Teleport");
        track.Template = new HelloTl_TeleportClip_TlData();
        Add(track, "Jump and stay", 0.5, 5f, false);
        Add(track, "Jump and come back", 1.5, 8f, true);
        EditorUtility.SetDirty(track);
        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
        return timeline;
    }

    static void Add(TlTrack track, string name, double start, float distance, bool comeBack)
    {
        var clip = track.CreateClip<TlClip>();
        clip.displayName = name;
        clip.start = start;
        clip.duration = 0.5;
        var data = (HelloTl_TeleportClip_TlData)((TlClip)clip.asset).Data;
        data.Distance = distance;
        data.ComeBack = comeBack;
        EditorUtility.SetDirty(clip.asset);
    }

    static string Folder()
    {
        foreach (var guid in AssetDatabase.FindAssets("HelloTlBuilder t:MonoScript"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == nameof(HelloTlBuilder))
                return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace(Path.DirectorySeparatorChar, '/');
        }
        throw new InvalidOperationException("HelloTlBuilder.cs was not found under Assets.");
    }
}
