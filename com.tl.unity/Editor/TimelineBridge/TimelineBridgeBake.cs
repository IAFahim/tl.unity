using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine.Timeline;
using Debug = UnityEngine.Debug;

namespace Tl.Unity.Editor
{
    public sealed class TimelineBridgeResult
    {
        public readonly List<string> JsonFiles = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public string JsonFolder;
        public string LibraryFile;
        public bool LibraryJsonChanged;
        public KindCapacityBake.Result Capacities;
    }

    public static class TimelineBridgeBake
    {
        public static string StageRoot => Path.GetFullPath("Temp/TlTimelineBridge");

        [MenuItem("Tools/Tl/Timeline Bridge/Bake All Bridges")]
        public static void BakeAll()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(TimelineBridgeAsset)))
            {
                var bridge = AssetDatabase.LoadAssetAtPath<TimelineBridgeAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (bridge != null)
                    BakeLogged(bridge);
            }
        }

        public static void BakeLogged(TimelineBridgeAsset bridge)
        {
            try
            {
                var result = Bake(bridge);
                var text = new StringBuilder("Tl timeline bridge '").Append(bridge.name).Append("': wrote ").Append(result.JsonFiles.Count).Append(" timelines and ").Append(result.LibraryFile).Append('.');
                foreach (var warning in result.Warnings)
                    text.Append("\n  ").Append(warning);
                if (result.Capacities != null)
                    text.Append(result.Capacities.kindsWritten ? "\nKind capacities changed; scripts recompile.\n" : "\nKind capacities unchanged.\n").Append(result.Capacities.Report());
                if (result.Warnings.Count > 0)
                    Debug.LogWarning(text.ToString(), bridge);
                else
                    Debug.Log(text.ToString(), bridge);
            }
            catch (Exception error)
            {
                Debug.LogError("Tl timeline bridge '" + bridge.name + "': " + error.Message, bridge);
            }
        }

        public static TimelineBridgeResult Bake(TimelineBridgeAsset bridge, bool refresh = true)
        {
            var library = bridge.Library;
            if (library == null)
                throw new InvalidOperationException("has no Library.");
            var folder = TimelineKindSets.PhysicalPath(library.Folder);
            var libraryJson = Path.Combine(folder, "library.json");
            if (!File.Exists(libraryJson))
                throw new FileNotFoundException("Library folder '" + library.Folder + "' has no library.json.", libraryJson);
            var result = new TimelineBridgeResult();
            var target = folder;
            if (bridge.Output == BridgeOutput.StagedJson)
            {
                target = Path.Combine(StageRoot, bridge.name);
                if (Directory.Exists(target))
                    Directory.Delete(target, true);
                CopyJson(folder, target);
            }
            result.JsonFolder = target;
            var tickRate = TickRate(File.ReadAllText(libraryJson));
            var names = new List<string>();
            var dataAssemblies = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (timeline, spec) in Specs(bridge))
            {
                var json = TimelineJson.Write(timeline, spec, tickRate, result.Warnings);
                AddAssemblies(timeline, dataAssemblies);
                var file = Path.Combine(target, spec.Name + ".json");
                if (!File.Exists(file) || File.ReadAllText(file) != json)
                    File.WriteAllText(file, json, new UTF8Encoding(false));
                result.JsonFiles.Add(file);
                names.Add(spec.Name + ".json");
            }
            var targetLibraryJson = Path.Combine(target, "library.json");
            var original = File.ReadAllText(targetLibraryJson);
            var patched = ListTimelines(original, names);
            if (patched != original)
            {
                File.WriteAllText(targetLibraryJson, patched, new UTF8Encoding(false));
                result.LibraryJsonChanged = true;
            }
            var gameDll = KindCapacityBake.Stage(library.GameAssembly);
            var arguments = new List<string> { "library", target, "--out", Path.Combine(folder, "game.tlib") };
            foreach (var assembly in AssemblyPaths(gameDll, dataAssemblies))
            {
                arguments.Add("--assembly");
                arguments.Add(assembly);
            }
            if (!string.IsNullOrEmpty(bridge.IdsFile))
            {
                arguments.Add("--ids");
                arguments.Add(TimelineKindSets.PhysicalPath(bridge.IdsFile));
            }
            if (!string.IsNullOrEmpty(library.Namespace))
            {
                arguments.Add("--namespace");
                arguments.Add(library.Namespace);
            }
            arguments.Add("--diagnostics");
            arguments.Add("json");
            Tlb(library.Tlb, arguments);
            result.LibraryFile = Path.Combine(folder, "game.tlib");
            if (!string.IsNullOrEmpty(library.KindsFile))
                result.Capacities = KindCapacityBake.Run(library.Tlb, target, library.Namespace, TimelineKindSets.PhysicalPath(library.KindsFile), gameDll);
            if (refresh)
                AssetDatabase.Refresh();
            return result;
        }

        public static List<(TimelineAsset Timeline, TimelineSpec Spec)> Specs(TimelineBridgeAsset bridge)
        {
            var specs = new List<(TimelineAsset, TimelineSpec)>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < bridge.Timelines.Count; i++)
            {
                var entry = bridge.Timelines[i];
                if (entry == null || entry.Timeline == null)
                    throw new InvalidOperationException("Timelines entry " + i + " has no Timeline asset.");
                var name = bridge.NameOf(entry.Timeline);
                var constant = TimelineBridgeAsset.ConstantName(name);
                if (constant.Length == 0 || !Regex.IsMatch(constant, "^[A-Za-z0-9_]+$"))
                    throw new InvalidOperationException("Timeline name '" + name + "' does not make a C# constant name.");
                if (!used.Add(constant))
                    throw new InvalidOperationException("Two timelines map to the constant name '" + constant + "'.");
                string next = null;
                if (entry.End == TimelineEnd.Next)
                {
                    next = entry.Next == null ? null : bridge.NameOf(entry.Next);
                    if (next == null)
                        throw new InvalidOperationException("Timeline '" + name + "' ends with Next, but its Next is not listed in the bridge.");
                }
                specs.Add((entry.Timeline, new TimelineSpec(name, entry.End, next, entry.Channel)));
            }
            return specs;
        }

        public static uint TickRate(string libraryJson)
        {
            var match = Regex.Match(libraryJson, "\"tickRate\"\\s*:\\s*([0-9]+)");
            return match.Success ? uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 60u;
        }

        public static string ListTimelines(string libraryJson, IReadOnlyList<string> files)
        {
            var array = Regex.Match(libraryJson, "(\"timelines\"\\s*:\\s*)\\[(.*?)\\]", RegexOptions.Singleline);
            var entries = new List<string>();
            if (array.Success)
                foreach (Match entry in Regex.Matches(array.Groups[2].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                    entries.Add(entry.Groups[1].Value);
            var added = false;
            foreach (var file in files)
            {
                if (entries.Exists(pattern => Glob(pattern, file)))
                    continue;
                entries.Add(file);
                added = true;
            }
            if (!added)
                return libraryJson;
            var list = new StringBuilder("[");
            for (var i = 0; i < entries.Count; i++)
                list.Append(i == 0 ? "\n    \"" : ",\n    \"").Append(entries[i]).Append('"');
            list.Append("\n  ]");
            if (array.Success)
                return libraryJson.Substring(0, array.Index) + array.Groups[1].Value + list + libraryJson.Substring(array.Index + array.Length);
            var open = libraryJson.IndexOf('{');
            if (open < 0)
                throw new InvalidOperationException("library.json has no root object.");
            var rest = libraryJson.Substring(open + 1).TrimStart();
            return libraryJson.Substring(0, open + 1) + "\n  \"timelines\": " + list + (rest.StartsWith("}", StringComparison.Ordinal) ? "\n" : ",\n  ") + rest;
        }

        public static bool Glob(string pattern, string file)
        {
            var regex = "^" + Regex.Escape(pattern.Replace('\\', '/')).Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$";
            return Regex.IsMatch(file.Replace('\\', '/'), regex);
        }

        public static string Tlb(string tlb, IReadOnlyList<string> arguments)
        {
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
            foreach (var argument in arguments)
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
            return stdout;
        }

        static void CopyJson(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from, "*.json", SearchOption.AllDirectories))
            {
                var relative = file.Substring(from.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var copy = Path.Combine(to, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy));
                File.Copy(file, copy, true);
            }
        }

        static void AddAssemblies(TimelineAsset timeline, SortedSet<string> names)
        {
            foreach (var track in timeline.GetOutputTracks())
            {
                if (track is TlTrackAsset tlTrack)
                {
                    names.Add(tlTrack.DataType.Assembly.GetName().Name);
                    foreach (var clip in tlTrack.GetClips())
                        if (clip.asset is TlClipAsset tlClip)
                            names.Add(tlClip.DataType.Assembly.GetName().Name);
                }
                foreach (var marker in track.GetMarkers())
                    if (marker is TlMarkerAsset tlMarker)
                        names.Add(tlMarker.DataType.Assembly.GetName().Name);
            }
            if (timeline.markerTrack != null)
                foreach (var marker in timeline.markerTrack.GetMarkers())
                    if (marker is TlMarkerAsset tlMarker)
                        names.Add(tlMarker.DataType.Assembly.GetName().Name);
        }

        static List<string> AssemblyPaths(string gameDll, SortedSet<string> names)
        {
            var paths = new List<string> { gameDll };
            var stage = Path.GetDirectoryName(gameDll);
            foreach (var name in names)
            {
                var path = Path.Combine(stage, name + ".dll");
                if (!File.Exists(path))
                    throw new FileNotFoundException("Data types come from assembly '" + name + "', which is not a compiled script assembly.", path);
                if (!string.Equals(path, gameDll, StringComparison.OrdinalIgnoreCase))
                    paths.Add(path);
            }
            return paths;
        }
    }

    sealed class TimelineBridgePostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            var playables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in imported)
                if (path.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
                    playables.Add(path);
            if (playables.Count == 0)
                return;
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(TimelineBridgeAsset)))
            {
                var bridge = AssetDatabase.LoadAssetAtPath<TimelineBridgeAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (bridge == null || !bridge.BakeOnSave || !Lists(bridge, playables))
                    continue;
                var pending = bridge;
                EditorApplication.delayCall += () =>
                {
                    if (pending != null)
                        TimelineBridgeBake.BakeLogged(pending);
                };
            }
        }

        static bool Lists(TimelineBridgeAsset bridge, HashSet<string> playables)
        {
            foreach (var entry in bridge.Timelines)
                if (entry != null && entry.Timeline != null && playables.Contains(AssetDatabase.GetAssetPath(entry.Timeline)))
                    return true;
            return false;
        }
    }
}
