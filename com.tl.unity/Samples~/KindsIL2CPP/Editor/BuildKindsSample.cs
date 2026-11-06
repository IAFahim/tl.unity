using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BuildKindsSample
{
    const string Scene = "Assets/KindsSample/KindsSample.unity";
    const string Output = "Builds/KindsSample/KindsSample.x86_64";

    [MenuItem("Tools/Tl/Build Kinds Sample (Linux IL2CPP)")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("KindsSample").AddComponent<KindsSample>();
        EditorSceneManager.SaveScene(scene, Scene);
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
        Directory.CreateDirectory(Path.GetDirectoryName(Output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = Output,
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.IncludeTestAssemblies,
        });
        Debug.Log("Tl kinds sample build: " + report.summary.result + " errors=" + report.summary.totalErrors + " warnings=" + report.summary.totalWarnings);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded && Application.isBatchMode)
            EditorApplication.Exit(1);
    }
}
