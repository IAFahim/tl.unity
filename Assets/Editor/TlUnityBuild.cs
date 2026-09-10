using System;
using TlUnity.PlayerProbe;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class TlUnityBuild
{
    public static void Build()
    {
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        BuildPlayer();
    }

    public static void BuildIl2Cpp()
    {
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
        BuildPlayer();
    }

    private static void BuildPlayer()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Tl Unity Player Gate").AddComponent<GatePlayer>();
        const string scenePath = "Assets/TlUnityPlayer.unity";
        EditorSceneManager.SaveScene(scene, scenePath);
        var output = Environment.GetEnvironmentVariable("TL_UNITY_PLAYER_OUTPUT");
        if (string.IsNullOrEmpty(output))
            output = "Build/TlUnityPlayer";
        var options = new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = output,
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.None
        };
        var report = BuildPipeline.BuildPlayer(options);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
