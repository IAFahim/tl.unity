using System;
using System.IO;
using Tl.Unity;
using UnityEngine;

public sealed class ScenePlayerSample : MonoBehaviour
{
    public const string Folder = "TlScenes";

    static readonly Type[] Keep = { typeof(Tl.Pack.Inventory.Bag), typeof(Tl.Pack.Stats.Health), typeof(Tl.Pack.Stats.Attack) };

    static readonly (string Library, string Scene, int Steps, ulong Expected)[] Cases =
    {
        ("inventory.tlib", "inventory.scene.bin", 1500, 6806057278687917896UL),
        ("inventory.tlib", "inventory-fast.scene.bin", 300, 17812265086550969911UL),
        ("inventory.tlib", "inventory-mixed.scene.bin", 700, 15172252175353637725UL),
        ("stats.tlib", "stats.scene.bin", 600, 13171269908726092120UL),
    };

    void Start()
    {
        var ok = Keep.Length == 3;
        var folder = Path.Combine(Application.streamingAssetsPath, Folder);
        foreach (var (library, scene, steps, expected) in Cases)
        {
            ulong hash;
            try
            {
                hash = TlSceneRun.Run(TlSceneData.FromBytes(File.ReadAllBytes(Path.Combine(folder, scene))), File.ReadAllBytes(Path.Combine(folder, library)), steps);
            }
            catch (Exception error)
            {
                Debug.Log("Tl scene player " + scene + " FAIL " + error);
                ok = false;
                continue;
            }
            var match = hash == expected;
            ok &= match;
            Debug.Log("Tl scene player " + scene + " steps=" + steps + " hash=" + hash + " expected=" + expected + " match=" + match + " backend=" + (Application.isEditor ? "editor" : "player"));
        }
        Debug.Log("Tl scene player done ok=" + ok);
        if (Application.isBatchMode)
            Application.Quit(ok ? 0 : 3);
    }
}
