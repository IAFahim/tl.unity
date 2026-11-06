using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Tl.Unity.Editor
{
    public static class KindCapacityBake
    {
        public static string FindTlb()
        {
            var configured = Environment.GetEnvironmentVariable("TL_TLB_PATH");
            return string.IsNullOrEmpty(configured) ? "tlb" : configured;
        }

        public static string BuildArguments(string libraryPath, string gameNamespace)
        {
            return "kinds --library \"" + libraryPath + "\" --namespace " + gameNamespace;
        }

        [MenuItem("Tools/Tl/Bake Kind Capacities (tlb)")]
        public static void BakeFromMenu()
        {
            var guids = AssetDatabase.FindAssets("t:TimelineKindLibraryAsset");
            var baked = 0;
            foreach (var guid in guids)
            {
                var asset = AssetDatabase.LoadAssetAtPath<TimelineKindLibraryAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset == null)
                    continue;
                try
                {
                    if (Bake(asset.LibraryJsonPath, asset.KindsGcsPath, asset.GameNamespace))
                        baked++;
                }
                catch (Exception error)
                {
                    UnityEngine.Debug.LogError("Tl Kind Capacities: bake failed for '" + asset.name + "': " + error.Message);
                }
            }
            UnityEngine.Debug.Log("Tl Kind Capacities: baked " + baked + " of " + guids.Length + " libraries.");
        }

        public static bool Bake(string libraryPath, string outputPath, string gameNamespace)
        {
            if (string.IsNullOrEmpty(libraryPath))
                throw new InvalidOperationException("Tl Kind Capacities: library path is empty.");
            if (string.IsNullOrEmpty(outputPath))
                throw new InvalidOperationException("Tl Kind Capacities: output path is empty.");
            var text = RunTlb(FindTlb(), BuildArguments(libraryPath, gameNamespace));
            if (!WriteOnlyOnChange(outputPath, text))
            {
                UnityEngine.Debug.Log("Tl Kind Capacities: '" + outputPath + "' unchanged.");
                return false;
            }
            AssetDatabase.Refresh();
            UnityEngine.Debug.Log("Tl Kind Capacities: wrote '" + outputPath + "'.");
            return true;
        }

        public static string RunTlb(string command, string arguments)
        {
            var start = new ProcessStartInfo(command, arguments);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using (var process = Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("'" + command + " " + arguments + "' exited " + process.ExitCode + ": " + stderr);
                return Normalize(stdout);
            }
        }

        public static bool WriteOnlyOnChange(string path, string text)
        {
            var normalized = Normalize(text);
            if (File.Exists(path) && Normalize(File.ReadAllText(path)) == normalized)
                return false;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, normalized);
            return true;
        }

        static string Normalize(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n");
        }
    }
}
