using System;
using System.IO;
using Tl.Unity.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildScenePlayer
{
    const string Scene = "Assets/ScenePlayer/ScenePlayer.unity";

    [MenuItem("Tools/Tl/Build Scene Player (IL2CPP)")]
    public static void BuildIl2Cpp() => Build(ScriptingImplementation.IL2CPP);

    [MenuItem("Tools/Tl/Build Scene Player (CoreCLR)")]
    public static void BuildCoreClr() => Build(CoreClr());

    public static void Build()
    {
        var args = Environment.GetCommandLineArgs();
        var backend = ScriptingImplementation.IL2CPP;
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "-tlBackend" && args[i + 1].Equals("CoreCLR", StringComparison.OrdinalIgnoreCase))
                backend = CoreClr();
        Build(backend);
    }

    static ScriptingImplementation CoreClr()
    {
        foreach (ScriptingImplementation value in Enum.GetValues(typeof(ScriptingImplementation)))
            if (value.ToString().IndexOf("CoreCLR", StringComparison.OrdinalIgnoreCase) >= 0)
                return value;
        throw new InvalidOperationException("this editor has no CoreCLR player backend");
    }

    static void Write(string folder, string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(folder, name), bytes);

    static string Demo(Type pack, string demo, string file)
    {
        var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(pack.Assembly);
        var path = package == null ? null : Path.Combine(package.resolvedPath, "..", "..", "demos", demo, file);
        if (path == null || !File.Exists(path))
            throw new FileNotFoundException("the tl demos folder is not next to the " + pack.Name + " pack: " + path);
        return path;
    }

    static void Blob(string folder, string name, string json, byte[] library)
        => Write(folder, name, TlSceneRunner.Data(TlSceneRunner.Read(File.ReadAllBytes(json), library)).ToBytes());

    static void Build(ScriptingImplementation backend)
    {
        var folder = Path.Combine("Assets", "StreamingAssets", ScenePlayerSample.Folder);
        Directory.CreateDirectory(folder);
        var inventory = Tl.Pack.Inventory.Tests.InventoryLibraryBytes.Bytes();
        var stats = Tl.Pack.Stats.Tests.StatsLibraryBytes.Bytes();
        Write(folder, "inventory.tlib", inventory);
        Write(folder, "stats.tlib", stats);
        var bag = typeof(Tl.Pack.Inventory.Bag);
        Blob(folder, "inventory.scene.bin", Demo(bag, "inventory", "inventory.scene.json"), inventory);
        Blob(folder, "inventory-fast.scene.bin", Demo(bag, "inventory", "inventory-fast.scene.json"), inventory);
        Blob(folder, "inventory-mixed.scene.bin", Demo(bag, "inventory", "inventory-mixed.scene.json"), inventory);
        Blob(folder, "stats.scene.bin", Demo(typeof(Tl.Pack.Stats.Health), "stats", "stats.scene.json"), stats);
        AssetDatabase.Refresh();
        Directory.CreateDirectory(Path.GetDirectoryName(Scene));
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("ScenePlayer").AddComponent<ScenePlayerSample>();
        EditorSceneManager.SaveScene(scene, Scene);
        var target = Application.platform == RuntimePlatform.WindowsEditor ? BuildTarget.StandaloneWindows64
            : Application.platform == RuntimePlatform.OSXEditor ? BuildTarget.StandaloneOSX : BuildTarget.StandaloneLinux64;
        var file = target == BuildTarget.StandaloneWindows64 ? "ScenePlayer.exe" : target == BuildTarget.StandaloneOSX ? "ScenePlayer.app" : "ScenePlayer.x86_64";
        var output = Path.Combine("Builds", "ScenePlayer", backend.ToString(), file);
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = output,
            target = target,
            options = BuildOptions.IncludeTestAssemblies,
        });
        Debug.Log("Tl scene player build: " + report.summary.result + " backend=" + backend + " output=" + output + " errors=" + report.summary.totalErrors + " warnings=" + report.summary.totalWarnings);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded && Application.isBatchMode)
            EditorApplication.Exit(1);
    }
}
