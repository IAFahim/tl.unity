using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

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
        const string scenePath = "Assets/TlUnityPlayer.unity";
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
