using System;
using System.IO;
using Demo;
using Tl.Unity;
using Tl.Unity.Editor;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class EcsDemoSetup
{
    const string LibraryName = "DemoLibrary.asset";
    const string ContentName = "DemoContent.unity";
    const string SceneName = "Demo.unity";
    const string StageFolder = ".tl/stage";

    public static void Prepare()
    {
        var root = Root();
        var library = AssetDatabase.LoadAssetAtPath<TimelineLibraryAsset>(root + "/" + LibraryName);
        var created = library == null;
        if (created)
            library = ScriptableObject.CreateInstance<TimelineLibraryAsset>();
        library.Folder = root + "/Timelines";
        library.TimelineIds = "Demo.Timelines";
        library.GameAssembly = "Demo";
        library.Namespace = "Demo";
        library.KindsFile = root + "/Kinds.g.cs";
        library.Tlb = TlbPath();
        library.KindSets = KindSets.Exact;
        if (created)
            AssetDatabase.CreateAsset(library, root + "/" + LibraryName);
        else
            EditorUtility.SetDirty(library);
        AssetDatabase.SaveAssets();
        var stage = Path.GetFullPath(StageFolder);
        if (Directory.Exists(stage))
            Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        var game = KindCapacityBake.Stage("Demo");
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(game), "*.dll"))
            File.Copy(file, Path.Combine(stage, Path.GetFileName(file)), true);
        Debug.Log("EcsDemo prepared " + library.Folder + ", staged " + stage);
    }

    public static void Build()
    {
        var root = Root();
        var library = AssetDatabase.LoadAssetAtPath<TimelineLibraryAsset>(root + "/" + LibraryName);
        var bytes = AssetDatabase.LoadAssetAtPath<TextAsset>(root + "/Timelines/game.bytes");
        if (library == null || bytes == null)
            throw new InvalidOperationException("run Prepare and bake the library first: " + LibraryName + " or Timelines/game.bytes is missing");

        var content = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var walker = new GameObject("Walker");
        walker.AddComponent<WalkerAuthoring>();
        var player = walker.AddComponent<TimelinePlayerAuthoring>();
        player.Library = library;
        player.StartWith = new[] { "Run" };
        player.MayStart = new[] { "Dash", "Stomp" };
        var dummy = new GameObject("Dummy");
        dummy.AddComponent<WalkerAuthoring>();
        dummy.AddComponent<DamageableAuthoring>();
        dummy.AddComponent<TimelinePlayerAuthoring>().Library = library;
        EditorSceneManager.SaveScene(content, root + "/" + ContentName);

        var main = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var host = new GameObject("DemoContent");
        var subScene = host.AddComponent<SubScene>();
        subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(root + "/" + ContentName);
        subScene.AutoLoadScene = true;
        new GameObject("Library").AddComponent<LibraryAuthoring>().Library = bytes;
        EditorSceneManager.SaveScene(main, root + "/" + SceneName);
        AssetDatabase.SaveAssets();
        Debug.Log("EcsDemo scene " + root + "/" + SceneName);
    }

    static string Root()
    {
        foreach (var guid in AssetDatabase.FindAssets("EcsDemoSetup t:MonoScript"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == "EcsDemoSetup")
                return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace(Path.DirectorySeparatorChar, '/');
        }
        throw new InvalidOperationException("EcsDemoSetup.cs not found under Assets");
    }

    static string TlbPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("TL_TLB");
        if (!string.IsNullOrEmpty(fromEnv))
            return fromEnv.Replace(Path.DirectorySeparatorChar, '/');
        var file = Path.GetFullPath(".tl/tlb.path");
        if (File.Exists(file))
            return File.ReadAllText(file).Trim().Replace(Path.DirectorySeparatorChar, '/');
        return "tlb";
    }
}
