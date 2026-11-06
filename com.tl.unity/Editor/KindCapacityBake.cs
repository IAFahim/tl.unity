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
            var arguments = "kinds --library " + Quote(folder) + " --namespace " + ns + " --out " + Quote(output) + " --assembly " + Quote(assembly) + " --diagnostics json";
            var command = string.IsNullOrEmpty(tlb) ? "tlb" : tlb;
            if (command.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                arguments = Quote(command) + " " + arguments;
                command = "dotnet";
            }
            var start = new ProcessStartInfo(command, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            string stdout, stderr;
            int exit;
            try
            {
                using var process = Process.Start(start);
                stdout = process.StandardOutput.ReadToEnd();
                stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                exit = process.ExitCode;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("could not run '" + command + "' (" + error.Message + "). Set Tlb on the library asset to the tlb command or to Tl.Bake.dll.");
            }
            if (exit != 0)
                throw new InvalidOperationException("tlb exited " + exit + ": " + (stderr.Length > 0 ? stderr : stdout).Trim());
            return JsonUtility.FromJson<Result>(stdout.Trim());
        }

        static string Quote(string path) => "\"" + path + "\"";
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
                try
                {
                    KindCapacityBake.Bake(library);
                }
                catch (Exception error)
                {
                    Debug.LogError("Tl kind capacities: library '" + library.name + "': " + error.Message, library);
                }
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
            AssemblyReloadEvents.afterAssemblyReload += KindCapacityBake.BakeAll;
        }
    }
}
