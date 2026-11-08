using System;
using System.IO;
using Tl.Generated;
using Tl.Pack.Inventory;
using Tl.Unity;
using Tl.Unity.Editor;
using TlPacks.Inventory;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

[InitializeOnLoad]
public static class InventoryDemoBuilder
{
    public const string SceneName = "Inventory Demo.unity";
    public const string ObjectsName = "Inventory Demo Objects.unity";
    const string Opened = "Tl.InventoryDemo.Opened";

    static InventoryDemoBuilder()
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

    [MenuItem("Tl/Samples/Build Inventory Demo")]
    public static void BuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        EditorSceneManager.OpenScene(Build());
    }

    public static string Build()
    {
        var root = Root() ?? throw new InvalidOperationException("InventoryDemoBuilder.cs was not found under Assets.");
        var folder = root + "/Scenes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(root, "Scenes");
        CreateLootRun(folder + "/Loot Run.playable");

        var objects = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var run = AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/Loot Run.playable");
        var backpack = GameObject.CreatePrimitive(PrimitiveType.Cube);
        backpack.name = "Backpack";
        UnityEngine.Object.DestroyImmediate(backpack.GetComponent<Collider>());
        backpack.transform.position = new Vector3(0f, 0.5f, 0f);
        backpack.transform.localScale = new Vector3(0.8f, 1f, 0.5f);
        var renderer = backpack.GetComponent<MeshRenderer>();
        var materialPath = folder + "/Backpack.mat";
        AssetDatabase.DeleteAsset(materialPath);
        var material = new Material(renderer.sharedMaterial) { name = "Backpack", color = new Color(0.55f, 0.36f, 0.2f) };
        AssetDatabase.CreateAsset(material, materialPath);
        renderer.sharedMaterial = material;
        var director = backpack.AddComponent<PlayableDirector>();
        director.playableAsset = run;
        director.playOnAwake = true;
        backpack.AddComponent<TlBagAuthoring>();
        EditorSceneManager.SaveScene(objects, folder + "/" + ObjectsName);

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var camera = Camera.main;
        camera.transform.position = new Vector3(0f, 1.6f, -4f);
        camera.transform.LookAt(new Vector3(0f, 0.5f, 0f));
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        UnityEngine.Object.DestroyImmediate(ground.GetComponent<Collider>());
        ground.transform.localScale = new Vector3(0.6f, 1f, 0.6f);
        new GameObject("Overlay").AddComponent<BagOverlay>();
        var host = new GameObject("Inventory Demo Objects");
        var subScene = host.AddComponent<SubScene>();
        subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(folder + "/" + ObjectsName);
        subScene.AutoLoadScene = true;
        var scene = folder + "/" + SceneName;
        EditorSceneManager.SaveScene(main, scene);
        AssetDatabase.SaveAssets();
        return scene;
    }

    static void CreateLootRun(string path)
    {
        AssetDatabase.DeleteAsset(path);
        var timeline = TlTimelines.CreateAsset();
        AssetDatabase.CreateAsset(timeline, path);
        timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
        timeline.fixedDuration = 600 / 60.0;
        TlInspectorModel.SetSettings(timeline, TlEnd.Hold, null, 0);
        var give = Track(timeline, "Give", new Tl_Pack_Inventory_GiveClip_TlData());
        Give(give, 0, Item.Wood, 5);
        Give(give, 60, Item.Stone, 3);
        Give(give, 120, Item.Potion, 2);
        Give(give, 540, Item.Sword, 1);
        Give(Track(timeline, "Take", new Tl_Pack_Inventory_GiveClip_TlData()), 360, Item.Potion, -1);
        var craft = Clip<Tl_Pack_Inventory_CraftClip_TlData>(Track(timeline, "Craft", new Tl_Pack_Inventory_CraftClip_TlData()), 180, 300);
        craft.Input = Item.Wood;
        craft.InputCount = 4;
        craft.Output = Item.Plank;
        craft.OutputCount = 2;
        Pickup(timeline, 420, Item.Coin, 10);
        Pickup(timeline, 480, Item.Gem, 1);
        foreach (var track in timeline.GetOutputTracks())
        {
            EditorUtility.SetDirty(track);
            foreach (var clip in track.GetClips())
                EditorUtility.SetDirty(clip.asset);
        }
        foreach (var marker in timeline.markerTrack.GetMarkers())
            EditorUtility.SetDirty((UnityEngine.Object)marker);
        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
    }

    static TlTrack Track(TimelineAsset timeline, string name, TlClipData template)
    {
        var track = timeline.CreateTrack<TlTrack>(null, name);
        track.Template = template;
        return track;
    }

    static T Clip<T>(TlTrack track, int start, int end) where T : TlClipData
    {
        var clip = track.CreateClip<TlClip>();
        clip.start = start / 60.0;
        clip.duration = (end - start) / 60.0;
        return (T)((TlClip)clip.asset).Data;
    }

    static void Give(TlTrack track, int tick, Item item, int count)
    {
        var data = Clip<Tl_Pack_Inventory_GiveClip_TlData>(track, tick, tick + 1);
        data.Item = item;
        data.Count = count;
    }

    static void Pickup(TimelineAsset timeline, int tick, Item item, int count)
    {
        if (timeline.markerTrack == null)
            timeline.CreateMarkerTrack();
        var marker = timeline.markerTrack.CreateMarker<TlMarker>(tick / 60.0);
        marker.name = "Pickup " + item;
        marker.Data = new Tl_Pack_Inventory_PickupMarker_TlMarkerData { Item = item, Count = count };
    }

    static string Root()
    {
        foreach (var guid in AssetDatabase.FindAssets("InventoryDemoBuilder t:MonoScript"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == nameof(InventoryDemoBuilder))
                return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace(Path.DirectorySeparatorChar, '/');
        }
        return null;
    }
}
