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
public static class CrowdDemoBuilder
{
    public const string SceneName = "Crowd Demo.unity";
    public const string ObjectsName = "Crowd Demo Objects.unity";
    const string Opened = "Tl.CrowdDemo.Opened";

    static CrowdDemoBuilder()
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

    [MenuItem("Tl/Samples/Build Crowd Demo")]
    public static void BuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        EditorSceneManager.OpenScene(Build());
    }

    public static string Build()
    {
        var root = Root() ?? throw new InvalidOperationException("CrowdDemoBuilder.cs was not found under Assets.");
        var folder = root + "/Scenes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(root, "Scenes");
        var dance = CreateDance(folder + "/Dance.playable");
        var dancer = CreateDancer(folder, dance);

        var objects = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        dance = AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/Dance.playable");
        dancer = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Dancer.prefab");
        var crowd = new GameObject("Crowd").AddComponent<CrowdAuthoring>();
        crowd.Prefab = dancer;
        crowd.Dance = dance;
        crowd.Count = 10000;
        crowd.Spacing = 1.2f;
        EditorSceneManager.SaveScene(objects, folder + "/" + ObjectsName);

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var camera = Camera.main;
        camera.transform.position = new Vector3(0f, 38f, -32f);
        camera.transform.LookAt(new Vector3(0f, 0f, 40f));
        camera.farClipPlane = 600f;
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        UnityEngine.Object.DestroyImmediate(ground.GetComponent<Collider>());
        ground.transform.position = new Vector3(0f, -0.5f, 180f);
        ground.transform.localScale = new Vector3(42f, 1f, 42f);
        new GameObject("Overlay").AddComponent<CrowdOverlay>();
        var host = new GameObject("Crowd Demo Objects");
        var subScene = host.AddComponent<SubScene>();
        subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(folder + "/" + ObjectsName);
        subScene.AutoLoadScene = true;
        var scene = folder + "/" + SceneName;
        EditorSceneManager.SaveScene(main, scene);
        AssetDatabase.SaveAssets();
        return scene;
    }

    static GameObject CreateDancer(string folder, TimelineAsset dance)
    {
        var path = folder + "/Dancer.prefab";
        AssetDatabase.DeleteAsset(path);
        var dancer = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        dancer.name = "Dancer";
        UnityEngine.Object.DestroyImmediate(dancer.GetComponent<Collider>());
        dancer.transform.localScale = new Vector3(0.4f, 0.5f, 0.4f);
        var renderer = dancer.GetComponent<MeshRenderer>();
        var materialPath = folder + "/Dancer.mat";
        AssetDatabase.DeleteAsset(materialPath);
        var material = new Material(renderer.sharedMaterial) { name = "Dancer", color = Color.white };
        AssetDatabase.CreateAsset(material, materialPath);
        renderer.sharedMaterial = material;
        var director = dancer.AddComponent<PlayableDirector>();
        director.playableAsset = dance;
        director.playOnAwake = false;
        dancer.AddComponent<DancerAuthoring>();
        var prefab = PrefabUtility.SaveAsPrefabAsset(dancer, path);
        UnityEngine.Object.DestroyImmediate(dancer);
        return prefab;
    }

    static TimelineAsset CreateDance(string path)
    {
        AssetDatabase.DeleteAsset(path);
        var timeline = TlTimelines.CreateAsset();
        AssetDatabase.CreateAsset(timeline, path);
        var bump = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.5f, 1f), new Keyframe(1f, 0f));
        var hop = timeline.CreateTrack<TlTrack>(null, "Hop");
        hop.Template = new HopClip_TlData();
        foreach (var start in new[] { 0.0, 1.0 })
        {
            var clip = hop.CreateClip<TlClip>();
            clip.start = start;
            clip.duration = 0.5;
            var data = (HopClip_TlData)((TlClip)clip.asset).Data;
            data.Height = 0.6f;
            data.Shape = bump;
            EditorUtility.SetDirty(clip.asset);
        }
        var spin = timeline.CreateTrack<TlTrack>(null, "Spin");
        spin.Template = new SpinClip_TlData();
        var turn = spin.CreateClip<TlClip>();
        turn.start = 0.5;
        turn.duration = 0.5;
        var turnData = (SpinClip_TlData)((TlClip)turn.asset).Data;
        turnData.Turns = 1f;
        turnData.Ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        EditorUtility.SetDirty(turn.asset);
        var glow = timeline.CreateTrack<TlTrack>(null, "Glow");
        glow.Template = new GlowClip_TlData();
        var wave = glow.CreateClip<TlClip>();
        wave.start = 0;
        wave.duration = 2;
        var waveData = (GlowClip_TlData)((TlClip)wave.asset).Data;
        waveData.Strength = 1f;
        waveData.Wave = bump;
        EditorUtility.SetDirty(wave.asset);
        timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
        timeline.fixedDuration = 2;
        TlInspectorModel.SetSettings(timeline, TlEnd.Loop, null, 0);
        EditorUtility.SetDirty(hop);
        EditorUtility.SetDirty(spin);
        EditorUtility.SetDirty(glow);
        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
        return timeline;
    }

    static string Root()
    {
        foreach (var guid in AssetDatabase.FindAssets("CrowdDemoBuilder t:MonoScript"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == nameof(CrowdDemoBuilder))
                return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace(Path.DirectorySeparatorChar, '/');
        }
        return null;
    }
}
