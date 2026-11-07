using KindGame;
using UnityEngine;

public sealed class KindsSample : MonoBehaviour
{
    int _failures;

    void Check(bool condition, string what, string detail)
    {
        if (condition)
            return;
        _failures++;
        Debug.Log("Tl kinds runner FAIL " + what + ": " + detail);
    }

    void Start()
    {
        var boost = BoostSection();
        var lift = LiftSection();
        ulong script;
        using (var game = new KindWorld(KindScript.Rows))
            script = KindScript.Play(game, KindScript.Steps);
        ulong events;
        using (var game = new KindWorld(KindScript.Rows, true))
            events = KindScript.PlayEvents(game, KindScript.Steps);
        var scriptMatch = script == KindScript.Pinned;
        var eventsMatch = events == KindScript.EventsPinned;
        Debug.Log("Tl kinds runner boost=" + boost + " lift=" + lift
            + " script hash=" + script + " expected=" + KindScript.Pinned + " match=" + scriptMatch
            + " events hash=" + events + " expected=" + KindScript.EventsPinned + " match=" + eventsMatch
            + " backend=" + (Application.isEditor ? "editor" : "player"));
        var ok = boost && lift && scriptMatch && eventsMatch && _failures == 0;
        if (Application.isBatchMode)
            Application.Quit(ok ? 0 : 3);
    }

    bool BoostSection()
    {
        var before = _failures;
        var ok = true;
        using (var game = new KindWorld(1))
        {
            game.Start(0, KindWorld.Run);
            game.Step();
            ok &= Expect(game.Motion(0).X == 2f, "walk", game.Motion(0).X.ToString());
            game.Start(0, KindWorld.SpeedBoost);
            game.Step();
            ok &= Expect(game.Motion(0).X == 7f, "boost", game.Motion(0).X.ToString());
            ok &= Expect(game.Entries<LocomotionEntry>(0) == 2, "entries", game.Entries<LocomotionEntry>(0).ToString());
            for (var s = 0; s < 19; s++)
                game.Step();
            ok &= Expect(game.Motion(0).X == 102f, "boosted total", game.Motion(0).X.ToString());
            game.Step();
            ok &= Expect(game.Motion(0).X == 104f, "run takes over", game.Motion(0).X.ToString());
            ok &= Expect(game.Entries<LocomotionEntry>(0) == 1, "run entry", game.Entries<LocomotionEntry>(0).ToString());
            Debug.Log("Tl kinds runner boost: X=" + game.Motion(0).X + " entries=" + game.Entries<LocomotionEntry>(0));
        }
        return ok && _failures == before;
    }
    bool LiftSection()
    {
        var before = _failures;
        using (var game = new KindWorld(1))
        {
            game.Start(0, KindWorld.Jump);
            game.Step();
            game.Step();
            game.Start(0, KindWorld.PotionHop);
            for (var s = 0; s < 4; s++)
                game.Step();
            Check(game.Motion(0).Y == 44f, "lifts add up", game.Motion(0).Y.ToString());
            Check(game.Stamina(0) == -1, "jump counter", game.Stamina(0).ToString());
            game.Step();
            Check(game.Stamina(0) == 9, "marker pulse", game.Stamina(0).ToString());
            for (var s = 0; s < 8; s++)
                game.Step();
            Check(game.Motion(0).Y == 84f, "lift total", game.Motion(0).Y.ToString());
            Check(game.Motion(0).X == 12f, "jump walk", game.Motion(0).X.ToString());
            Check(game.Stamina(0) == 9, "stat holds", game.Stamina(0).ToString());
            var slots = game.Slots(0);
            Check(slots.Length == 0 || slots[0].Timeline == 0, "slots freed", slots.Length.ToString());
            Debug.Log("Tl kinds runner lift: Y=" + game.Motion(0).Y + " X=" + game.Motion(0).X + " stamina=" + game.Stamina(0));
        }
        return _failures == before;
    }

    bool Expect(bool condition, string what, string detail)
    {
        Check(condition, what, detail);
        return condition;
    }
}
