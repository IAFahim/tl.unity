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
        var options = new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = OutputPath(),
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.None
        };
        var report = BuildPipeline.BuildPlayer(options);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    private static string OutputPath()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 0; index + 1 < arguments.Length; index++)
            if (arguments[index] == "-buildOutput")
                return arguments[index + 1];
        var output = Environment.GetEnvironmentVariable("TL_UNITY_PLAYER_OUTPUT");
        return string.IsNullOrEmpty(output) ? "Build/TlUnityPlayer" : output;
    }
}
