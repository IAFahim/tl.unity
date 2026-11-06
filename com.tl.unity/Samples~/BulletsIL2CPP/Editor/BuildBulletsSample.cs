using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BuildBulletsSample
{
    const string Scene = "Assets/BulletsSample/BulletsSample.unity";
    const string Output = "Builds/BulletsSample/BulletsSample.x86_64";

    [MenuItem("Tools/Tl/Build Bullets Sample (Linux IL2CPP)")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("BulletsSample").AddComponent<BulletsSample>();
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
        Debug.Log("Tl bullets sample build: " + report.summary.result + " errors=" + report.summary.totalErrors + " warnings=" + report.summary.totalWarnings);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded && Application.isBatchMode)
            EditorApplication.Exit(1);
    }
}
