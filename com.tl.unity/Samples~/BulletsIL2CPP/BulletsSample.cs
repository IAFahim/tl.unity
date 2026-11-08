using KindGame;
using UnityEngine;

public sealed class BulletsSample : MonoBehaviour
{
    public const ulong Pinned = 3081412629212866412UL;

    int _failures;

    static ulong MixInt(ulong h, int v) { var s = new global::Tl.TlStateHash { Value = h }; s.Int(v); return s.Value; }

    void Check(bool condition, string what, string detail)
    {
        if (condition)
            return;
        _failures++;
        Debug.Log("Tl bullets FAIL " + what + ": " + detail);
    }

    void Start()
    {
        ulong hash;
        using (var game = new KindWorld(KindScript.Rows))
            hash = Play(game);
        var match = hash == Pinned;
        Debug.Log("Tl bullets hash=" + hash + " expected=" + Pinned + " match=" + match
            + " backend=" + (Application.isEditor ? "editor" : "player"));
        if (Application.isBatchMode)
            Application.Quit(match && _failures == 0 ? 0 : 3);
    }

    ulong Play(KindWorld game)
    {
        var h = global::Tl.TlStateHash.Seed;
        game.Start(0, KindWorld.Stomp);
        game.Step();
        h = Hash(game, h);
        game.Damage(1, 7);
        game.Step();
        h = Hash(game, h);
        Check(game.Health(1) == 993, "game pulse", game.Health(1).ToString());
        game.Damage(0, 10);
        game.Damage(0, 10);
        game.Step();
        h = Hash(game, h);
        Check(game.Health(0) == 955, "timeline plus game pulses", game.Health(0).ToString());
        game.Step();
        h = Hash(game, h);
        game.Step();
        h = Hash(game, h);
        Check(game.Health(0) == 955, "holds", game.Health(0).ToString());
        game.Step();
        h = Hash(game, h);
        Check(game.Health(0) == 945, "second hit", game.Health(0).ToString());
        Check(game.Health(2) == 1000, "untouched", game.Health(2).ToString());
        Check(game.Tick.DroppedPulses == 0, "dropped", game.Tick.DroppedPulses.ToString());
        return h;
    }

    ulong Hash(KindWorld game, ulong h)
    {
        for (var e = 0; e < KindScript.Rows; e++)
            h = MixInt(h, game.Health(e));
        return h;
    }
}
