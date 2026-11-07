using System;
using System.IO;
using Tl.Generated;
using Tl.Unity;
using Tl.Unity.Editor;
using Unity.Mathematics;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Timeline;

[InitializeOnLoad]
public static class ForceDemoBuilder
{
    public const string SceneName = "Force Demo.unity";
    public const string ObjectsName = "Force Demo Objects.unity";
    public const string TimelineName = "Push.playable";
    const string Opened = "Tl.ForceDemo.Opened";
    const string NoPipeline = "The Force demo cube lives in a SubScene, and Entities Graphics draws SubScene objects only with URP or HDRP. This project uses the Built-in Render Pipeline, so the cube moves but stays invisible. Create the project from the Universal 3D template, or install Universal RP and set it in Project Settings > Graphics.";

    static ForceDemoBuilder()
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

    [MenuItem("Tl/Samples/Build Force Demo")]
    public static void BuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        EditorSceneManager.OpenScene(Build());
    }

    public static string Build()
    {
        var root = Root() ?? throw new InvalidOperationException("ForceDemoBuilder.cs was not found under Assets.");
        var folder = root + "/Scenes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(root, "Scenes");
        var timeline = CreatePush(folder + "/" + TimelineName);

        var objects = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Cube";
        UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>());
        cube.transform.position = new Vector3(0f, 0.5f, 0f);
        var renderer = cube.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = Crate(renderer.sharedMaterial, folder + "/Crate.mat");
        var director = cube.AddComponent<PlayableDirector>();
        director.playableAsset = timeline;
        director.playOnAwake = true;
        EditorSceneManager.SaveScene(objects, folder + "/" + ObjectsName);

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var camera = Camera.main;
        camera.transform.position = new Vector3(2f, 3.5f, -7f);
        camera.transform.LookAt(new Vector3(2f, 0.5f, 0f));
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = new Vector3(2f, 0f, 0f);
        ground.transform.localScale = new Vector3(1.2f, 1f, 0.6f);
        var host = new GameObject("Force Demo Objects");
        var subScene = host.AddComponent<SubScene>();
        subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(folder + "/" + ObjectsName);
        subScene.AutoLoadScene = true;
        var scene = folder + "/" + SceneName;
        EditorSceneManager.SaveScene(main, scene);
        AssetDatabase.SaveAssets();
        if (GraphicsSettings.currentRenderPipeline == null)
            Debug.LogWarning(NoPipeline);
        return scene;
    }

    static Material Crate(Material source, string path)
    {
        AssetDatabase.DeleteAsset(path);
        var material = new Material(source) { name = "Crate", color = new Color(0.2f, 0.45f, 0.9f) };
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    static TimelineAsset CreatePush(string path)
    {
        AssetDatabase.DeleteAsset(path);
        var timeline = TlTimelines.CreateAsset();
        AssetDatabase.CreateAsset(timeline, path);
        var track = timeline.CreateTrack<TlTrack>(null, "Force");
        track.Template = new ForceClip_TlData();
        var clip = track.CreateClip<TlClip>();
        clip.start = 0;
        clip.duration = 2;
        var data = (ForceClip_TlData)((TlClip)clip.asset).Data;
        data.Direction = new float3(1f, 0f, 0f);
        data.Strength = 4f;
        data.Falloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
        EditorUtility.SetDirty(clip.asset);
        EditorUtility.SetDirty(track);
        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
        return timeline;
    }

    static string Root()
    {
        foreach (var guid in AssetDatabase.FindAssets("ForceDemoBuilder t:MonoScript"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == nameof(ForceDemoBuilder))
                return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace(Path.DirectorySeparatorChar, '/');
        }
        return null;
    }
}
