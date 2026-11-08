using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DemoHubBuilder
{
    public const string SceneName = "Tl Demos.unity";

    static readonly Dictionary<string, string> Notes = new Dictionary<string, string>
    {
        { "Force Demo", "A cube pushed there and back by one Force track. Follow camera, grid floor, live readout. The five-file contract." },
        { "Control Demo", "A Burst job starts a second timeline (Dash) from a clip field when the cube passes x 2." },
        { "Events Demo", "Strike clips raise events: sparks, damage on a dummy through a kind, a Hurt timeline on the dummy, a score." },
        { "Crowd Demo", "10k dancers on one Dance timeline, each from its own start tick. Buttons for 1k, 10k, 100k." },
        { "Motion Demo", "Motion pack only: a cube glides, a sphere breathes and changes colour, a capsule spins and hops with a Priority override." },
        { "Stats Demo", "Stats pack: a duel timeline of hits, poison and a heal plus a Haste buff on Health and Attack." },
        { "Inventory Demo", "Inventory pack: give, take and craft clips plus two pickup markers fill a bag over 600 steps." },
    };

    [MenuItem("Tl/Samples/Build Demo Hub")]
    public static void BuildAndOpen()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        EditorSceneManager.OpenScene(Build());
    }

    public static string Build()
    {
        var root = Root() ?? throw new InvalidOperationException("DemoHubBuilder.cs was not found under Assets.");
        var demos = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:SceneAsset"))
        {
            var found = AssetDatabase.GUIDToAssetPath(guid);
            var name = Path.GetFileNameWithoutExtension(found);
            if (found.StartsWith("Assets/", StringComparison.Ordinal) && name.EndsWith(" Demo", StringComparison.Ordinal))
                demos.Add(found);
        }
        demos = demos.OrderBy(p => Order(Path.GetFileNameWithoutExtension(p))).ThenBy(p => p, StringComparer.Ordinal).ToList();
        var folder = root + "/Scenes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(root, "Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var hub = new GameObject("Tl Demo Hub").AddComponent<TlDemoHub>();
        hub.Demos = demos.Select(Path.GetFileNameWithoutExtension).ToArray();
        hub.Notes = hub.Demos.Select(d => Notes.TryGetValue(d, out var note) ? note : "").ToArray();
        var camera = Camera.main;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
        var path = folder + "/" + SceneName;
        EditorSceneManager.SaveScene(scene, path);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(path, true) }.Concat(demos.Select(d => new EditorBuildSettingsScene(d, true))).ToArray();
        AssetDatabase.SaveAssets();
        Debug.Log("TL-HUB scene=" + path.Replace(' ', '+') + " demos=" + string.Join(",", hub.Demos.Select(d => d.Replace(' ', '+'))));
        return path;
    }

    static int Order(string name)
    {
        var index = Notes.Keys.ToList().IndexOf(name);
        return index < 0 ? int.MaxValue : index;
    }

    static string Root()
    {
        foreach (var guid in AssetDatabase.FindAssets("DemoHubBuilder t:MonoScript"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == nameof(DemoHubBuilder))
                return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace(Path.DirectorySeparatorChar, '/');
        }
        return null;
    }
}
