using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    public static class TlExport
    {
        [MenuItem("Tl/Export Library")]
        static void ExportSelected()
        {
            var timelines = Selection.GetFiltered<TimelineAsset>(SelectionMode.Assets);
            if (timelines.Length == 0)
            {
                EditorUtility.DisplayDialog("Export Library", "Select one or more Timeline assets in the Project window first.", "OK");
                return;
            }
            var path = EditorUtility.SaveFilePanel("Export Library", "", "game.tlib", "tlib");
            if (string.IsNullOrEmpty(path))
                return;
            var names = Export(timelines, path);
            Debug.Log("Tl exported " + names.Count + " timelines to " + path + ": " + string.Join(", ", names));
        }

        [MenuItem("Tl/Export")]
        static void ExportJsonSelected()
        {
            var timelines = Selection.GetFiltered<TimelineAsset>(SelectionMode.Assets);
            if (timelines.Length == 0)
            {
                EditorUtility.DisplayDialog("Export", "Select one or more Timeline assets in the Project window first.", "OK");
                return;
            }
            var directory = EditorUtility.SaveFolderPanel("Export JSON", "", "");
            if (string.IsNullOrEmpty(directory))
                return;
            var names = ExportJson(timelines, directory);
            Debug.Log("Tl exported " + names.Count + " timelines and tl.project.json to " + directory + ": " + string.Join(", ", names));
        }

        public static List<string> ExportJson(IReadOnlyList<TimelineAsset> timelines, string directory)
        {
            var rate = TlProjectSettings.instance.TicksPerSecond;
            var baker = new TlBaker(null, Entity.Null, new HashSet<ComponentType>(), null);
            var names = new List<string>();
            var channels = 0;
            Directory.CreateDirectory(directory);
            foreach (var timeline in TlInspectorModel.Closure(timelines))
            {
                if (TlInspectorModel.TlTracks(timeline) == 0)
                    continue;
                if (names.Contains(timeline.name))
                    throw new InvalidOperationException("Tl: two timelines are named " + timeline.name + "; rename one before exporting.");
                File.WriteAllText(Path.Combine(directory, timeline.name + ".json"), TlTimelinePacker.Json(timeline, rate, ref baker, out var count));
                channels = Math.Max(channels, count);
                names.Add(timeline.name);
            }
            if (names.Count == 0)
                throw new InvalidOperationException("Tl: nothing to export.");
            var files = names.OrderBy(n => n, StringComparer.Ordinal).Select(n => n + ".json").ToList();
            File.WriteAllText(Path.Combine(directory, "tl.project.json"), Tl.Gen.Tlb.TlJson.ExportProject(files, Enumerable.Range(1, channels).Select(c => TlProjectSettings.instance.ChannelName(c)).ToList(), rate));
            return names;
        }

        [MenuItem("Tl/Import")]
        static void ImportProject()
        {
            var path = EditorUtility.OpenFilePanel("Import tl.project.json", "", "json");
            if (string.IsNullOrEmpty(path))
                return;
            var folder = EditorUtility.SaveFolderPanel("Import into", Application.dataPath, "");
            if (string.IsNullOrEmpty(folder))
                return;
            var assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full != assets && !full.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                EditorUtility.DisplayDialog("Import", "Choose a folder inside this project's Assets folder.", "OK");
                return;
            }
            var relative = ("Assets" + full.Substring(assets.Length)).Replace('\\', '/');
            var timelines = ImportJson(path, relative);
            AssetDatabase.SaveAssets();
            Debug.Log("Tl imported " + timelines.Count + " timelines into " + relative + ": " + string.Join(", ", timelines.Select(t => t.name)));
        }

        public static List<TimelineAsset> ImportJson(string projectPath, string folder = null)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            var patterns = new List<string>();
            var channels = new List<string>();
            uint rate;
            using (var project = Tl.Gen.Tlb.TlJson.ReadProject(File.ReadAllBytes(projectPath)))
            {
                var root = project.RootElement;
                rate = root.TryGetProperty("tickRate", out var tick) ? tick.GetUInt32() : 60u;
                foreach (var entry in root.GetProperty("timelines").EnumerateArray())
                    patterns.Add(entry.GetString());
                if (root.TryGetProperty("channels", out var names))
                    foreach (var entry in names.EnumerateArray())
                        channels.Add(entry.GetString());
            }
            if (rate != TlProjectSettings.instance.TicksPerSecond)
                Debug.LogWarning("Tl: " + projectPath + " runs at " + rate + " ticks per second and this project at " + TlProjectSettings.instance.TicksPerSecond + ". The timelines keep their times in seconds and export at this project's rate.");
            var sources = new List<(string name, string file, string json)>();
            foreach (var file in Files(directory, patterns))
            {
                var json = File.ReadAllText(file);
                var name = NameOf(json, file);
                if (sources.Any(s => s.name == name))
                    throw new InvalidOperationException("Tl: two timelines in " + projectPath + " are named " + name + ".");
                sources.Add((name, file, json));
            }
            if (sources.Count == 0)
                throw new InvalidOperationException("Tl: " + projectPath + " names no timeline files.");
            sources.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            var manifest = Tl.Gen.Tlb.LibraryBake.Manifest(sources.Select(s => s.name).ToList(), channels, rate);
            var clips = Mirrors(false);
            var markers = Mirrors(true);
            var resolver = Tl.Gen.Tlb.BakerAssemblyResolver.FromAssemblies(clips.Keys.Concat(markers.Keys).Select(t => t.Assembly).Append(typeof(TlNoSettings<>).Assembly).Distinct());
            var typed = new List<Tl.Gen.Tlb.TypedTimeline>();
            foreach (var source in sources)
            {
                try
                {
                    typed.Add(Tl.Gen.Tlb.TimelineJson.Read(Tl.Gen.Tlb.TimelineBaker.BakeJson(source.json, resolver, manifest: manifest), resolver, manifest));
                }
                catch (Tl.Gen.Tlb.BakeDiagnosticException error)
                {
                    throw new InvalidOperationException("Tl: " + source.file + ": " + error.Message, error);
                }
            }
            if (folder != null)
                foreach (var source in sources)
                    if (File.Exists(folder + "/" + source.name + ".playable"))
                        throw new InvalidOperationException("Tl: " + folder + "/" + source.name + ".playable already exists. Import into a folder without these timelines.");
            var made = new List<TimelineAsset>();
            foreach (var source in sources)
            {
                var timeline = TlTimelines.CreateAsset();
                timeline.name = source.name;
                if (folder != null)
                    AssetDatabase.CreateAsset(timeline, folder + "/" + source.name + ".playable");
                made.Add(timeline);
            }
            for (var i = 0; i < made.Count; i++)
                Build(made[i], typed[i], made, channels, rate, clips, markers);
            return made;
        }

        static void Build(TimelineAsset timeline, Tl.Gen.Tlb.TypedTimeline typed, List<TimelineAsset> made, List<string> channels, uint rate, Dictionary<Type, Type> clips, Dictionary<Type, Type> markers)
        {
            timeline.durationMode = TimelineAsset.DurationMode.FixedLength;
            timeline.fixedDuration = typed.Duration / (double)rate;
            foreach (var track in typed.Tracks)
            {
                if (!track.Type.IsGenericType || track.Type.GetGenericTypeDefinition() != typeof(TlNoSettings<>))
                    Debug.LogWarning("Tl: the track " + (track.Name ?? track.Type.Name) + " in " + timeline.name + " has " + track.Type.Name + " settings, and a Unity Tl track has none. Its clips are imported without them.", timeline);
                foreach (var group in track.Clips.GroupBy(c => c.Type))
                {
                    if (!clips.TryGetValue(group.Key, out var mirror))
                    {
                        Debug.LogWarning("Tl: no Unity clip type plays " + group.Key.FullName + ", so its clips on the track " + (track.Name ?? group.Key.Name) + " in " + timeline.name + " are skipped.", timeline);
                        continue;
                    }
                    var template = (TlClipData)Activator.CreateInstance(mirror);
                    var lane = timeline.CreateTrack<TlTrack>(null, track.Name ?? TlMenu.ShortName(template.MenuName));
                    lane.Template = template;
                    foreach (var clip in group)
                    {
                        var item = lane.CreateClip<TlClip>();
                        item.start = clip.Start / (double)rate;
                        item.duration = (clip.End - clip.Start) / (double)rate;
                        if (clip.Name != null)
                            item.displayName = clip.Name;
                        var data = (TlClipData)Activator.CreateInstance(mirror);
                        Fill(data, Unpack(clip.Type, clip.Data), made, typed.Texts, timeline, "the clip at " + item.start.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s");
                        ((TlClip)item.asset).Data = data;
                        EditorUtility.SetDirty(item.asset);
                    }
                    EditorUtility.SetDirty(lane);
                }
            }
            foreach (var marker in typed.Markers)
            {
                if (!markers.TryGetValue(marker.Type, out var mirror))
                {
                    Debug.LogWarning("Tl: no Unity marker type plays " + marker.Type.FullName + ", so the marker at tick " + marker.Tick + " in " + timeline.name + " is skipped.", timeline);
                    continue;
                }
                if (timeline.markerTrack == null)
                    timeline.CreateMarkerTrack();
                var item = timeline.markerTrack.CreateMarker<TlMarker>(marker.Tick / (double)rate);
                if (AssetDatabase.Contains(timeline) && !AssetDatabase.Contains(item))
                    AssetDatabase.AddObjectToAsset(item, timeline);
                var data = (TlMarkerData)Activator.CreateInstance(mirror);
                Fill(data, Unpack(marker.Type, marker.Data), made, typed.Texts, timeline, "the marker at tick " + marker.Tick);
                item.Data = data;
                EditorUtility.SetDirty(item);
            }
            var end = typed.Next != null ? TlEnd.Next : typed.Loops ? TlEnd.Loop : typed.Hold ? TlEnd.Hold : TlEnd.Stop;
            var next = typed.Next == null ? null : made.First(t => t.name == typed.Next);
            var channel = typed.Channel == null ? 0 : channels.IndexOf(typed.Channel) + 1;
            if (channel > 15)
            {
                Debug.LogWarning("Tl: " + timeline.name + " plays on channel " + typed.Channel + ", which is channel " + channel + " in its project, and Unity has channels 1 to 15. It plays on no channel.", timeline);
                channel = 0;
            }
            else if (channel > 0 && TlProjectSettings.instance.ChannelName(channel) != typed.Channel)
                Debug.LogWarning("Tl: " + timeline.name + " plays on channel " + typed.Channel + ", which is channel " + channel + " here and is named " + TlProjectSettings.instance.ChannelName(channel) + " in the Tl project settings. Rename it there to export the same name.", timeline);
            TlInspectorModel.SetSettings(timeline, end, next, channel);
            EditorUtility.SetDirty(timeline);
        }

        static void Fill(TlClipData data, object value, List<TimelineAsset> made, List<string> texts, TimelineAsset timeline, string where)
        {
            foreach (var field in data.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                var source = value.GetType().GetField(field.Name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (source == null)
                    continue;
                var read = source.GetValue(value);
                if (field.FieldType == source.FieldType)
                    field.SetValue(data, read);
                else if (read is TlCurve curve && field.FieldType == typeof(AnimationCurve))
                    field.SetValue(data, TlCurveAuthoring.Curve(curve));
                else if (read is TlText text && field.FieldType == typeof(string))
                    field.SetValue(data, text.IsNone ? "" : texts.FirstOrDefault(t => TlText.Of(t) == text) ?? "");
                else if (read is TimelineId id && field.FieldType == typeof(TimelineAsset))
                    field.SetValue(data, id.IsNone || id.Value > made.Count ? null : made[id.Value - 1]);
                else if (read is TlTimeline reference && field.FieldType == typeof(TimelineAsset))
                {
                    var target = reference.IsValid ? Find(reference, made) : null;
                    if (reference.IsValid && target == null)
                        Debug.LogWarning("Tl: " + field.Name + " on " + where + " in " + timeline.name + " names a timeline asset that is not in the import or this project, so it is left empty.", timeline);
                    field.SetValue(data, target);
                }
                else
                    Debug.LogWarning("Tl: " + field.Name + " on " + where + " in " + timeline.name + " is " + source.FieldType.Name + " in the clip and " + field.FieldType.Name + " in Unity, so it is left at its default.", timeline);
            }
        }

        static TimelineAsset Find(TlTimeline reference, List<TimelineAsset> made)
        {
            foreach (var timeline in made)
                if (TlTimelinePacker.AssetOf(timeline).ToAssetId().Equals(reference.Asset) || new global::Unity.Entities.Hash128(UnityEngine.Hash128.Compute(timeline.name).ToString()).ToAssetId().Equals(reference.Asset))
                    return timeline;
            foreach (var guid in AssetDatabase.FindAssets("t:TimelineAsset"))
                if (new global::Unity.Entities.Hash128(guid).ToAssetId().Equals(reference.Asset))
                    return AssetDatabase.LoadAssetAtPath<TimelineAsset>(AssetDatabase.GUIDToAssetPath(guid));
            return null;
        }

        static object Unpack(Type type, byte[] bytes)
        {
            return typeof(TlExport).GetMethod(nameof(UnpackAs), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).MakeGenericMethod(type).Invoke(null, new object[] { bytes });
        }

        static object UnpackAs<T>(byte[] bytes) where T : struct => System.Runtime.InteropServices.MemoryMarshal.Read<T>(bytes);

        static Dictionary<Type, Type> Mirrors(bool marker)
        {
            var mirrors = new Dictionary<Type, Type>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<TlClipData>().OrderBy(t => t.Namespace == "Tl.Generated" ? 0 : 1).ThenBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (type.IsAbstract || type.ContainsGenericParameters || type.GetConstructor(Type.EmptyTypes) == null || typeof(TlMarkerData).IsAssignableFrom(type) != marker)
                    continue;
                var clip = ((TlClipData)Activator.CreateInstance(type)).ClipType;
                if (clip != null && !mirrors.ContainsKey(clip))
                    mirrors.Add(clip, type);
            }
            return mirrors;
        }

        static IEnumerable<string> Files(string directory, List<string> patterns)
        {
            var files = new List<string>();
            foreach (var pattern in patterns)
            {
                var path = Path.Combine(directory, pattern);
                var found = pattern.IndexOfAny(new[] { '*', '?' }) < 0
                    ? new[] { path }
                    : Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path), pattern.Contains("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                foreach (var file in found.Select(f => Path.GetFullPath(f)))
                {
                    if (!File.Exists(file))
                        throw new InvalidOperationException("Tl: the project names " + pattern + ", which does not exist.");
                    if (!files.Contains(file))
                        files.Add(file);
                }
            }
            return files;
        }

        static string NameOf(string json, string file)
        {
            using (var document = System.Text.Json.JsonDocument.Parse(json))
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String && name.GetString().Length > 0)
                    return name.GetString();
            return Path.GetFileNameWithoutExtension(file);
        }

        public static List<string> Export(IReadOnlyList<TimelineAsset> timelines, string path)
        {
            var rate = TlProjectSettings.instance.TicksPerSecond;
            var catalog = TlTimelinePacker.Catalog;
            var baker = new TlBaker(null, Entity.Null, new HashSet<ComponentType>(), null);
            var blobs = new List<BlobAssetReference<TlTimelineBlob>>();
            try
            {
                foreach (var timeline in TlInspectorModel.Closure(timelines))
                    if (TlInspectorModel.TlTracks(timeline) > 0)
                        blobs.Add(TlTimelinePacker.Pack(timeline, rate, catalog, ref baker, timeline, out _));
                using var library = new TlLibrary();
                var errors = new List<string>();
                if (!library.Add(blobs, errors))
                    throw new InvalidOperationException(errors.Count > 0 ? errors[0] : "Tl: nothing to export.");
                File.WriteAllBytes(path, library.Bytes);
                var names = new List<string>();
                foreach (var pair in library.Ids.OrderBy(p => p.Value))
                    foreach (var blob in blobs)
                        if (blob.Value.Asset == pair.Key)
                        {
                            names.Add(TlTimelineBlobs.NameOf(ref blob.Value));
                            break;
                        }
                return names;
            }
            finally
            {
                foreach (var blob in blobs)
                    blob.Dispose();
            }
        }
    }
}
