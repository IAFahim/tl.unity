using KindGame;
using UnityEngine;

public sealed class KindsSample : MonoBehaviour
{
    void Start()
    {
        ulong hash;
        using (var game = new KindWorld(KindScript.Rows))
            hash = KindScript.Play(game, KindScript.Steps);
        var match = hash == KindScript.Pinned;
        Debug.Log("Tl kinds hash=" + hash + " expected=" + KindScript.Pinned + " match=" + match + " backend=" + (Application.isEditor ? "editor" : "player"));
        if (Application.isBatchMode)
            Application.Quit(match ? 0 : 3);
    }
}
