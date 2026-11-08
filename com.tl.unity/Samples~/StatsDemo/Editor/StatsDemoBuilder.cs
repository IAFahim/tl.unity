using System;
using System.IO;
using Tl.Generated;
using Tl.Unity;
using Tl.Unity.Editor;
using TlPacks.Stats;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

[InitializeOnLoad]
public static class StatsDemoBuilder
{
    public const string SceneName = "Stats Demo.unity";
    public const string ObjectsName = "Stats Demo Objects.unity";
    const string Opened = "Tl.StatsDemo.Opened";

    static StatsDemoBuilder()
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

    [MenuItem("Tl/Samples/Build Stats Demo")]
    public static void BuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        EditorSceneManager.OpenScene(Build());
    }

    public static string Build()
    {
        var root = Root() ?? throw new InvalidOperationException("StatsDemoBuilder.cs was not found under Assets.");
        var folder = root + "/Scenes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(root, "Scenes");
        CreateHaste(folder + "/Haste.playable");
        CreateDuel(folder + "/Duel.playable");

        var objects = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var duel = AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/Duel.playable");
        var haste = AssetDatabase.LoadAssetAtPath<TimelineAsset>(folder + "/Haste.playable");
        var hero = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        hero.name = "Hero";
        UnityEngine.Object.DestroyImmediate(hero.GetComponent<Collider>());
        hero.transform.position = new Vector3(0f, 1f, 0f);
        var renderer = hero.GetComponent<MeshRenderer>();
        var materialPath = folder + "/Hero.mat";
        AssetDatabase.DeleteAsset(materialPath);
        var material = new Material(renderer.sharedMaterial) { name = "Hero", color = new Color(0.35f, 0.6f, 0.95f) };
        AssetDatabase.CreateAsset(material, materialPath);
        renderer.sharedMaterial = material;
        var director = hero.AddComponent<PlayableDirector>();
        director.playableAsset = duel;
        director.playOnAwake = true;
        hero.AddComponent<TlAlsoPlays>().Timelines.Add(haste);
        var stats = hero.AddComponent<TlStatsAuthoring>();
        stats.Health = 100;
        stats.MaxHealth = 100;
        stats.Attack = 10;
        var schedule = hero.AddComponent<HasteAuthoring>();
        schedule.Haste = haste;
        schedule.First = 100;
        schedule.Second = 250;
        EditorSceneManager.SaveScene(objects, folder + "/" + ObjectsName);

        var main = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var camera = Camera.main;
        camera.transform.position = new Vector3(0f, 1.8f, -5f);
        camera.transform.LookAt(new Vector3(0f, 1f, 0f));
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        UnityEngine.Object.DestroyImmediate(ground.GetComponent<Collider>());
        ground.transform.localScale = new Vector3(0.6f, 1f, 0.6f);
        new GameObject("Overlay").AddComponent<StatsOverlay>();
        var host = new GameObject("Stats Demo Objects");
        var subScene = host.AddComponent<SubScene>();
        subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(folder + "/" + ObjectsName);
        subScene.AutoLoadScene = true;
        var scene = folder + "/" + SceneName;
        EditorSceneManager.SaveScene(main, scene);
        AssetDatabase.SaveAssets();
        return scene;
    }

    static TimelineAsset Timeline(string path, int ticks, TlEnd end, int channel)
    {
        AssetDatabase.DeleteAsset(path);
        var timeline = TlTimelines.CreateAsset();
        AssetDatabase.CreateAsset(timeline, path);
        timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
        timeline.fixedDuration = ticks / 60.0;
        TlInspectorModel.SetSettings(timeline, end, null, channel);
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

    static void CreateDuel(string path)
    {
        var timeline = Timeline(path, 600, TlEnd.Hold, 0);
        var hits = Track(timeline, "Hit", new Tl_Pack_Stats_HitClip_TlData());
        foreach (var tick in new[] { 60, 120, 180 })
            Clip<Tl_Pack_Stats_HitClip_TlData>(hits, tick, tick + 1).Amount = 12;
        var poison = Clip<Tl_Pack_Stats_PoisonClip_TlData>(Track(timeline, "Poison", new Tl_Pack_Stats_PoisonClip_TlData()), 240, 480);
        poison.Amount = 1;
        poison.Every = 20;
        Clip<Tl_Pack_Stats_HealClip_TlData>(Track(timeline, "Heal", new Tl_Pack_Stats_HealClip_TlData()), 500, 501).Amount = 20;
        Save(timeline);
    }

    static void CreateHaste(string path)
    {
        var timeline = Timeline(path, 300, TlEnd.Stop, 1);
        Clip<Tl_Pack_Stats_BuffClip_TlData>(Track(timeline, "Buff", new Tl_Pack_Stats_BuffClip_TlData()), 0, 300).AttackBonus = 10;
        Save(timeline);
    }

    static string Root()
    {
        foreach (var guid in AssetDatabase.FindAssets("StatsDemoBuilder t:MonoScript"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == nameof(StatsDemoBuilder))
                return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace(Path.DirectorySeparatorChar, '/');
        }
        return null;
    }
}
