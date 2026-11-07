using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildKindsSample
{
    const string Scene = "Assets/KindsSample/KindsSample.unity";

    [MenuItem("Tools/Tl/Build Kinds Sample (IL2CPP)")]
    public static void BuildIl2Cpp() => Build(ScriptingImplementation.IL2CPP);

    [MenuItem("Tools/Tl/Build Kinds Sample (CoreCLR)")]
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

    static void Build(ScriptingImplementation backend)
    {
        if (!Directory.Exists(Path.GetDirectoryName(Scene)))
            Directory.CreateDirectory(Path.GetDirectoryName(Scene));
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("KindsSample").AddComponent<KindsSample>();
        EditorSceneManager.SaveScene(scene, Scene);
        var target = Application.platform == RuntimePlatform.WindowsEditor ? BuildTarget.StandaloneWindows64
            : Application.platform == RuntimePlatform.OSXEditor ? BuildTarget.StandaloneOSX : BuildTarget.StandaloneLinux64;
        var file = target == BuildTarget.StandaloneWindows64 ? "KindsSample.exe" : target == BuildTarget.StandaloneOSX ? "KindsSample.app" : "KindsSample.x86_64";
        var output = Path.Combine("Builds", "KindsSample", backend.ToString(), file);
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = output,
            target = target,
            options = BuildOptions.IncludeTestAssemblies,
        });
        Debug.Log("Tl kinds sample build: " + report.summary.result + " backend=" + backend + " output=" + output + " errors=" + report.summary.totalErrors + " warnings=" + report.summary.totalWarnings);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded && Application.isBatchMode)
            EditorApplication.Exit(1);
    }
}
