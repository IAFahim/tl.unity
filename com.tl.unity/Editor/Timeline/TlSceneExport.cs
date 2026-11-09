using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Tl.Gen.Tlb;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    public static class TlSceneExport
    {
        public const string SceneFile = "export.scene.json";

        [MenuItem("Tl/Export Scene JSON")]
        static void ExportSelected()
        {
            var objects = Selection.GetFiltered<GameObject>(SelectionMode.Editable | SelectionMode.TopLevel);
            if (objects.Length == 0)
            {
                EditorUtility.DisplayDialog("Export Scene JSON", "Select one or more objects with a Playable Director that plays Tl tracks in the Hierarchy first.", "OK");
                return;
            }
            var directory = EditorUtility.SaveFolderPanel("Export Scene JSON", "", "");
            if (string.IsNullOrEmpty(directory))
                return;
            Debug.Log("Tl exported the timelines, tl.project.json and the scene to " + Export(objects, directory));
        }

        public static string Export(IReadOnlyList<GameObject> objects, string directory)
        {
            var directors = objects.Select(o => o.GetComponent<PlayableDirector>()).Where(d => d != null && d.playableAsset is TimelineAsset).ToList();
            if (directors.Count == 0)
                throw new InvalidOperationException("Tl: none of the objects has a Playable Director with a Timeline.");
            var names = TlExport.ExportJson(directors.Select(d => (TimelineAsset)d.playableAsset).Distinct().ToList(), directory);
            var rate = TlProjectSettings.instance.TicksPerSecond;
            string[] channels;
            using (var project = TlJson.ReadProject(File.ReadAllBytes(Path.Combine(directory, "tl.project.json"))))
                channels = project.RootElement.GetProperty("channels").EnumerateArray().Select(e => e.GetString()).ToArray();
            var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
            var manifest = LibraryBake.Manifest(sorted, channels, rate);
            var scene = new TlScene { Project = "tl.project.json" };
            var columns = KindColumns();
            var hybrid = typeof(Baker<>).Assembly;
            var utility = hybrid.GetType("Unity.Entities.BakingUtility", true);
            var flags = Enum.Parse(utility.GetNestedType("BakingFlags", BindingFlags.Public | BindingFlags.NonPublic), "AssignName");
            using var world = new World("TlSceneExport");
            using var blobs = new BlobAssetStore(128);
            var settings = Activator.CreateInstance(hybrid.GetType("Unity.Entities.BakingSettings", true), flags, blobs);
            utility.GetMethod("BakeGameObjects", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { world, objects.ToArray(), settings });
            var manager = world.EntityManager;
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<TlTimelineSet>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                var item = new TlScene.Entity { Name = manager.GetName(entity) };
                foreach (var type in columns)
                {
                    var bytes = TlSceneRun.ReadState(manager, entity, type);
                    if (bytes != null)
                        item.States.Add((type, bytes));
                }
                if (manager.HasComponent<TlPlayOnAwake>(entity))
                {
                    var set = manager.GetBuffer<TlTimelineSet>(entity, true);
                    var start = manager.GetComponentData<TlPlayOnAwake>(entity);
                    item.Play.Add(((ushort)(sorted.IndexOf(BlobName(set[0])) + 1), start.At));
                }
                scene.Entities.Add(item);
            }
            var path = Path.Combine(directory, SceneFile);
            File.WriteAllText(path, scene.ToJson(manifest));
            return path;
        }

        static List<Type> KindColumns()
        {
            var registry = KindRegistry.Discover();
            var found = new Dictionary<string, Type>(StringComparer.Ordinal);
            for (var k = 0; k < registry.Count; k++)
                foreach (var column in registry[k].Columns)
                    found[(column.Namespace == null ? string.Empty : column.Namespace + ".") + column.Name] = column;
            return found.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToList();
        }

        static string BlobName(TlTimelineSet entry)
        {
            ref var bytes = ref entry.Blob.Value.Name;
            var text = new byte[bytes.Length];
            for (var i = 0; i < text.Length; i++)
                text[i] = bytes[i];
            return System.Text.Encoding.UTF8.GetString(text);
        }
    }
}
