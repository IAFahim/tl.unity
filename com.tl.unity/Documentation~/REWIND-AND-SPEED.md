# Rewind and speed: what comes back, and what does not

Three controls, all in the **Tl Panel** (`GameObject > Tl > Panel`) and in code (`TlRequests`):

- **Reverse** one timeline: that timeline runs backward, the others do not.
- **World rewind** (`World: rewinding`): the whole world runs backward, one tick per step.
- **Speed** per timeline or per channel, 0.25x to 4x (and up to 15.9x in code).

Tl runs the timeline backward for you. Whether *your game state* comes back depends on how you change it. This page is the rule, with what was measured.

## The rule in one line

**Tl takes back only what you add and subtract. It cannot take back what you overwrite.**

## 1. Pulses come back exactly

A pulse is a one-time change: a `[Pulse]` on clip start, clip end or a marker, or a game pulse (`KindPulses`). Every pulse knows which way time runs, so going backward undoes it.

Measured: the Inventory demo plays 450 steps, rewinds 450 steps and the bag is empty. The Stats demo plays 300 steps forward and 300 back: Health 100 and Attack 10 again, and Health at step 400 equals Health at step 200.

Write give/take, damage/heal, add/remove as pulses and you get rewind for free.

## 2. A value summed every tick goes back only if you sign it

A track that feeds a kind every tick (`[Feeds]`) is called with a `frame.Direction`: `+1` going forward, `-1` going backward. If your kind **adds** the value, multiply it by `frame.Direction` and rewind subtracts it again:

```csharp
[Feeds(typeof(PushKind))]
public static float3 Evaluate(in TlFrame<PushClip> frame)
    => frame.Clip.Direction * frame.Clip.Strength * frame.Clip.Falloff.Evaluate(frame.Progress) * frame.Direction;
```

Without `* frame.Direction` the value has the same sign both ways, so a rewind keeps adding. That is why the Force demo cube kept going forward until `Presentation/ForceDirection.cs` turned its force around.

Measured (.NET host, a push clip, six steps forward then backward): the running sum walks back step by step, but after a full rewind to tick 0 it is **not zero**. What is left is the contribution of the tick where time turned around (X 0.5714 left of 7.714). So a per-tick sum comes back **one tick short**. Pulses do not have this.

## 3. Things you integrate yourself do not come back exactly

`position += force * deltaTime` in your own system runs once per frame, and frame times differ, so going back does not retrace the same steps. The Force cube comes back close, not exact. If a value must come back exactly, change it with a pulse (a fixed amount per tick or per event) instead of per frame.

## 4. Do not clamp where Tl can undo

`Health = min(Health + heal, 100)` cannot be undone: after the clamp you no longer know how much was lost. Keep the raw sum in state and clamp when you draw (the Stats pack does this).

## 5. Values that are *set* follow the clip, not history

The Motion pack sets a position, scale, yaw or colour from the clip. Rewind plays each animation backward while the clip is active. It cannot restore the value from before the clip, because setting forgot it (measured: a 625-dancer crowd does not return to its earlier hashes after a rewind). Fine for animation, wrong for a bag.

## Speed

Speed is stored in sixteenths: 16 is 1x, 32 is 2x, 8 is half. A step crosses `(phase + n) / 16` ticks (1.5x crosses 1, 2, 1, 2).

- **Exact at every speed:** kinds and pulses. A playback at any speed applies what 1x applies over the same ticks, forward and backward, across a handover, a loop and a foreign feeder (tests at 0.5x, 1.5x, 2x, 2.5x, 3x, 4x, and `tl run`, Unity and the IL2CPP and CoreCLR players give the same hashes for sped-up scenes).
- **One limit in Unity:** `ITlTrack` event methods (`OnClipStart`, `OnClipEnd`, `ExecuteActive` with `Events<T>`) run once per step at the last tick. Above 1x a boundary crossed earlier in the same step raises no event. Use `[Pulse]` when the boundary must be seen at every speed.

## Quick check before you ship a kind

| Your change | Comes back on rewind? |
|---|---|
| Pulse (start, end, marker, game pulse) | yes, exactly |
| Per-tick sum signed with `frame.Direction` | yes, one tick short |
| Per-tick sum without `frame.Direction` | no, it keeps adding |
| Your own per-frame integration | close, not exact |
| Clamp, min, max in Apply | no |
| Set from the clip (Motion) | follows the clip, forgets what was before it |
