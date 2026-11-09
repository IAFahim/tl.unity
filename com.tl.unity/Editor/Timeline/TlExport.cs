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
