using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Tl.Unity.Editor
{
    public static class KindCapacityBake
    {
        [Serializable]
        public sealed class KindRow
        {
            public string name;
            public int worst;
            public int capacity;
            public int entryBytes;
            public int inlineBytes;
            public string setBy;
        }

        [Serializable]
        public sealed class Result
        {
            public int timelines;
            public KindRow[] kinds = Array.Empty<KindRow>();
            public string kindsFile;
            public bool kindsWritten;

            public string Report()
            {
                var text = new StringBuilder();
                foreach (var kind in kinds)
                    text.Append("  ").Append(kind.name).Append(": capacity ").Append(kind.capacity).Append(" (worst ").Append(kind.worst)
                        .Append(", set by ").Append(kind.setBy).Append("), ").Append(kind.inlineBytes).Append(" B inline\n");
                return text.ToString();
            }
        }

        public static string StageDirectory => Path.GetFullPath("Temp/TlKindBake");

        [MenuItem("Tools/Tl/Bake Kind Capacities")]
        public static void BakeAll()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(TimelineLibraryAsset)))
            {
                var library = AssetDatabase.LoadAssetAtPath<TimelineLibraryAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (library == null || string.IsNullOrEmpty(library.KindsFile))
                    continue;
                try
                {
                    Bake(library);
                }
                catch (Exception error)
                {
                    Debug.LogError("Tl kind capacities: library '" + library.name + "': " + error.Message, library);
                }
            }
        }

        public static Result Bake(TimelineLibraryAsset library)
        {
            var output = TimelineKindSets.PhysicalPath(library.KindsFile);
            var result = Run(library.Tlb, TimelineKindSets.PhysicalPath(library.Folder), library.Namespace, output, Stage(library.GameAssembly));
            if (result.kindsWritten)
            {
                AssetDatabase.Refresh();
                Debug.Log("Tl kind capacities: library '" + library.name + "' wrote " + library.KindsFile + "; scripts recompile.\n" + result.Report(), library);
            }
            else
                Debug.Log("Tl kind capacities: library '" + library.name + "' unchanged, no recompile.\n" + result.Report(), library);
            return result;
        }

        public static string Stage(string gameAssembly)
        {
            if (string.IsNullOrEmpty(gameAssembly))
                throw new InvalidOperationException("Game Assembly is empty.");
            var stage = StageDirectory;
            Directory.CreateDirectory(stage);
            Mirror(Path.GetFullPath("Library/ScriptAssemblies"), stage);
            Mirror(Path.Combine(EditorApplication.applicationContentsPath, "Managed", "UnityEngine"), stage);
            var game = Path.Combine(stage, gameAssembly + ".dll");
            if (!File.Exists(game))
                throw new FileNotFoundException("Script assembly '" + gameAssembly + "' is not compiled yet.", game);
            return game;
        }

        static void Mirror(string source, string stage)
        {
            if (!Directory.Exists(source))
                return;
            foreach (var file in Directory.GetFiles(source, "*.dll"))
            {
                var target = Path.Combine(stage, Path.GetFileName(file));
                var from = new FileInfo(file);
                var to = new FileInfo(target);
                if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc)
                    continue;
                File.Copy(file, target, true);
                File.SetLastWriteTimeUtc(target, from.LastWriteTimeUtc);
            }
        }

        public static Result Run(string tlb, string folder, string ns, string output, string assembly)
        {
            if (string.IsNullOrEmpty(output))
                throw new InvalidOperationException("Kinds File is empty.");
            if (string.IsNullOrEmpty(ns))
                throw new InvalidOperationException("Namespace is empty.");
            var command = string.IsNullOrEmpty(tlb) ? "tlb" : tlb;
            var start = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (command.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                start.FileName = "dotnet";
                start.ArgumentList.Add(command);
            }
            else
                start.FileName = command;
            foreach (var argument in new[] { "kinds", "--library", folder, "--namespace", ns, "--out", output, "--assembly", assembly, "--diagnostics", "json" })
                start.ArgumentList.Add(argument);
            string stdout, stderr;
            int exit;
            try
            {
                using var process = Process.Start(start);
                var errors = process.StandardError.ReadToEndAsync();
                stdout = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(120000))
                {
                    process.Kill();
                    throw new InvalidOperationException("tlb did not finish in 120 s.");
                }
                stderr = errors.Result;
                exit = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("could not run '" + command + "' (" + error.Message + "). Set Tlb on the library asset to the tlb command or to Tl.Bake.dll.");
            }
            if (exit != 0)
                throw new InvalidOperationException("tlb exited " + exit + ": " + (stderr.Length > 0 ? stderr : stdout).Trim());
            var json = string.Empty;
            foreach (var line in stdout.Split('\n'))
                if (line.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    json = line.Trim();
            var result = json.Length == 0 ? null : JsonUtility.FromJson<Result>(json);
            if (result == null || result.kinds == null)
                throw new InvalidOperationException("tlb printed no kinds report. It may be too old for the kinds command. Output: " + stdout.Trim());
            return result;
        }
    }

    sealed class KindCapacityPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(TimelineLibraryAsset)))
            {
                var library = AssetDatabase.LoadAssetAtPath<TimelineLibraryAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (library == null || string.IsNullOrEmpty(library.Folder) || string.IsNullOrEmpty(library.KindsFile))
                    continue;
                var folder = TimelineKindSets.PhysicalPath(library.Folder) + Path.DirectorySeparatorChar;
                if (!Touches(folder, imported) && !Touches(folder, deleted) && !Touches(folder, moved) && !Touches(folder, movedFrom))
                    continue;
                var pending = library;
                EditorApplication.delayCall += () =>
                {
                    if (pending == null)
                        return;
                    try
                    {
                        KindCapacityBake.Bake(pending);
                    }
                    catch (Exception error)
                    {
                        Debug.LogError("Tl kind capacities: library '" + pending.name + "': " + error.Message, pending);
                    }
                };
            }
        }

        static bool Touches(string folder, string[] paths)
        {
            foreach (var path in paths)
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && TimelineKindSets.PhysicalPath(path).StartsWith(folder, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }

    [InitializeOnLoad]
    static class KindCapacityAfterCompile
    {
        static KindCapacityAfterCompile()
        {
            AssemblyReloadEvents.afterAssemblyReload += () =>
            {
                if (AssetDatabase.IsAssetImportWorkerProcess() || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                KindCapacityBake.BakeAll();
            };
        }
    }
}
