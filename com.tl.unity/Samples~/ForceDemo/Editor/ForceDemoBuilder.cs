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
    public const string ReturnName = "Return.playable";
    static readonly Vector3 CameraOffset = new Vector3(0f, 3f, -7f);
    public const string ControlSceneName = "Control Demo.unity";
    public const string ControlObjectsName = "Control Demo Objects.unity";
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
        var back = CreateForce(folder + "/" + ReturnName, new float3(-1f, 0f, 0f), 4f, 2);
        timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
        timeline.fixedDuration = 3;
        back.durationMode = TimelineAsset.DurationMode.FixedLength;
        back.fixedDuration = 3;
        TlInspectorModel.SetSettings(timeline, TlEnd.Next, back, 0);
        TlInspectorModel.SetSettings(back, TlEnd.Next, timeline, 0);
        EditorUtility.SetDirty(timeline);
        EditorUtility.SetDirty(back);
        AssetDatabase.SaveAssets();

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
        cube.AddComponent<FollowTargetAuthoring>();
        var rig = new GameObject("Camera Rig");
        rig.transform.position = cube.transform.position + CameraOffset;
        rig.transform.LookAt(cube.transform.position);
        rig.AddComponent<FollowCameraAuthoring>().Offset = CameraOffset;
        var rigPosition = rig.transform.position;
        var rigRotation = rig.transform.rotation;
        EditorSceneManager.SaveScene(objects, folder + "/" + ObjectsName);

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var camera = Camera.main;
        camera.transform.SetPositionAndRotation(rigPosition, rigRotation);
        camera.gameObject.AddComponent<CameraFromEntity>();
        new GameObject("Overlay").AddComponent<ForceOverlay>();
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Grid Floor";
        UnityEngine.Object.DestroyImmediate(ground.GetComponent<Collider>());
        ground.transform.position = new Vector3(2f, 0f, 0f);
        ground.transform.localScale = new Vector3(2f, 1f, 0.6f);
        var floor = ground.GetComponent<MeshRenderer>();
        floor.sharedMaterial = Grid(floor.sharedMaterial, folder);
        Numbers(main);
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

    [MenuItem("Tl/Samples/Build Control Demo")]
    public static void BuildControlAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        EditorSceneManager.OpenScene(BuildControl());
    }

    public static string BuildControl()
    {
        var root = Root() ?? throw new InvalidOperationException("ForceDemoBuilder.cs was not found under Assets.");
        var folder = root + "/Scenes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(root, "Scenes");
        var dash = CreateForce(folder + "/Dash.playable", new float3(0f, 0f, 1f), 6f, 1);
        var push = CreateForce(folder + "/Push Then Dash.playable", new float3(1f, 0f, 0f), 4f, 2);
        var track = push.CreateTrack<TlTrack>(null, "Dash When Far");
        track.Template = new DashWhenFarClip_TlData();
        var clip = track.CreateClip<TlClip>();
        clip.start = 0;
        clip.duration = 2;
        var data = (DashWhenFarClip_TlData)((TlClip)clip.asset).Data;
        data.Dash = dash;
        data.AtX = 2f;
        EditorUtility.SetDirty(clip.asset);
        EditorUtility.SetDirty(track);
        EditorUtility.SetDirty(push);
        AssetDatabase.SaveAssets();
        return BuildScene(folder, push, ControlObjectsName, ControlSceneName, "Control Demo Objects");
    }

    static TimelineAsset CreateForce(string path, float3 direction, float strength, double seconds)
    {
        AssetDatabase.DeleteAsset(path);
        var timeline = TlTimelines.CreateAsset();
        AssetDatabase.CreateAsset(timeline, path);
        var track = timeline.CreateTrack<TlTrack>(null, "Force");
        track.Template = new ForceClip_TlData();
        var clip = track.CreateClip<TlClip>();
        clip.start = 0;
        clip.duration = seconds;
        var data = (ForceClip_TlData)((TlClip)clip.asset).Data;
        data.Direction = direction;
        data.Strength = strength;
        data.Falloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
        EditorUtility.SetDirty(clip.asset);
        EditorUtility.SetDirty(track);
        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
        return timeline;
    }

    static string BuildScene(string folder, TimelineAsset timeline, string objectsName, string sceneName, string hostName)
    {
        var objects = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Cube";
        UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>());
        cube.transform.position = new Vector3(0f, 0.5f, 0f);
        var renderer = cube.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = Crate(renderer.sharedMaterial, folder + "/" + hostName + ".mat");
        var director = cube.AddComponent<PlayableDirector>();
        director.playableAsset = timeline;
        director.playOnAwake = true;
        EditorSceneManager.SaveScene(objects, folder + "/" + objectsName);

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var camera = Camera.main;
        camera.transform.position = new Vector3(2f, 3.5f, -7f);
        camera.transform.LookAt(new Vector3(2f, 0.5f, 0f));
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = new Vector3(2f, 0f, 0f);
        ground.transform.localScale = new Vector3(1.2f, 1f, 0.6f);
        var host = new GameObject(hostName);
        var subScene = host.AddComponent<SubScene>();
        subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(folder + "/" + objectsName);
        subScene.AutoLoadScene = true;
        var scene = folder + "/" + sceneName;
        EditorSceneManager.SaveScene(main, scene);
        AssetDatabase.SaveAssets();
        if (GraphicsSettings.currentRenderPipeline == null)
            Debug.LogWarning(NoPipeline);
        return scene;
    }

    static Material Grid(Material source, string folder)
    {
        const int size = 128;
        var path = folder + "/Grid.png";
        var texture = new Texture2D(size, size, TextureFormat.RGB24, false);
        var floor = new Color(0.32f, 0.34f, 0.37f);
        var line = new Color(0.85f, 0.87f, 0.9f);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                texture.SetPixel(x, y, x < 2 || y < 2 || x >= size - 2 || y >= size - 2 ? line : floor);
        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.anisoLevel = 8;
        importer.SaveAndReimport();
        var materialPath = folder + "/Grid.mat";
        AssetDatabase.DeleteAsset(materialPath);
        var material = new Material(source) { name = "Grid", mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path), mainTextureScale = new Vector2(20f, 6f) };
        AssetDatabase.CreateAsset(material, materialPath);
        return material;
    }

    static void Numbers(UnityEngine.SceneManagement.Scene scene)
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var root = new GameObject("Grid Numbers");
        for (var m = -2; m <= 8; m++)
        {
            var label = new GameObject(m + " m");
            label.transform.SetParent(root.transform, false);
            label.transform.SetPositionAndRotation(new Vector3(m, 0.01f, -1f), Quaternion.Euler(90f, 0f, 0f));
            var text = label.AddComponent<TextMesh>();
            text.text = m.ToString();
            text.font = font;
            text.fontSize = 48;
            text.characterSize = 0.06f;
            text.anchor = TextAnchor.MiddleCenter;
            text.color = Color.white;
            label.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }
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
