# Tl for Unity

Timelines that **run your game code**. A designer places clips on a Timeline. You write a few small structs and functions that say what each clip does. Tl plays them every tick, fast (Burst), the same way every time (deterministic), and can run them **backward** and at **any speed**.

This file is the whole course. Read it top to bottom, type the code, and by the end you will know how Tl works, how to design with it, what it cannot do, and where it will bite you. Every code block here was run unless it says otherwise.

**Watch first:** the [release demo (3 min)](https://github.com/IAFahim/tl/blob/main/demos/film/release.mp4) shows every demo running in Unity, and [Hello Tl in six steps](https://github.com/IAFahim/tl/blob/main/demos/film/tutorial.mp4) follows chapter 1. Both videos are themselves Tl timelines ([how they are made](https://github.com/IAFahim/tl/tree/main/demos/film)), and the same two films are built inside Unity as a Timeline drawn with UI Toolkit: [release](https://github.com/IAFahim/tl/blob/main/demos/film/release-unity.mp4), [tutorial](https://github.com/IAFahim/tl/blob/main/demos/film/tutorial-unity.mp4).

**Who it is for:** programmers. A designer needs none of this: they use Unity's Timeline window.

## Contents

0. [Why Tl, in one example](#0-why-tl-in-one-example)
1. [Run your first timeline (10 minutes)](#1-run-your-first-timeline-10-minutes)
2. [The model: what runs, in what order, where data lives](#2-the-model-what-runs-in-what-order-where-data-lives)
3. [The five pieces, one by one](#3-the-five-pieces-one-by-one)
4. [Pattern A: an instant event (pulse)](#4-pattern-a-an-instant-event-pulse)
5. [Pattern B: a value over time (feeds)](#5-pattern-b-a-value-over-time-feeds)
6. [Pattern C: both in one rule (health)](#6-pattern-c-both-in-one-rule-health)
7. [Many at once: timelines, tracks, blending, order](#7-many-at-once-timelines-tracks-blending-order)
8. [Designing the timeline side: end, channels, starting, curves](#8-designing-the-timeline-side-end-channels-starting-curves)
9. [Baking: how your data gets into the entity (the Unity way)](#9-baking-how-your-data-gets-into-the-entity-the-unity-way)
10. [Time: rewind, reverse, speed](#10-time-rewind-reverse-speed)
11. [Outside Unity, and testing](#11-outside-unity-and-testing)
12. [Sharp edges: what will bite you](#12-sharp-edges-what-will-bite-you)
13. [Known limits and open risks](#13-known-limits-and-open-risks)
14. [Having fun: things to build](#14-having-fun-things-to-build)
15. [Cheat sheet and exercises](#15-cheat-sheet-and-exercises)
16. [Install, demos, repository](#16-install-demos-repository)

---

## 0. Why Tl, in one example

You need: "at 0.5 s the player jumps 5 m forward and stays there; at 1.5 s he jumps 8 m and comes back at the end." Without Tl you write timers, a state machine and rewind bookkeeping, and every change needs a programmer.

With Tl the programmer writes **one small file once** (chapter 1). After that the designer places, moves and tunes those clips in the Timeline window, with no code. You get, for free:

- **Designers can change it without you.** Distance, timing and "come back" are Inspector fields on a clip.
- **Exact replay.** The same timeline gives the same numbers every run, in Unity, in a plain .NET program, and in IL2CPP and CoreCLR players (checked by comparing a hash of the whole state).
- **Rewind and speed.** Run the world backward, or one timeline at 0.25x to 4x, from code or the on-screen panel.
- **Speed.** 100,000 entities each playing a timeline cost about 32 ns per entity per tick with 0 bytes allocated (measured).

---

## 1. Run your first timeline (10 minutes)

**Setup:** Unity **7000.0.0a16**, project from the **Universal 3D (URP)** template. Package Manager > **+** > **Install package from git URL**:

```
https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main
```

> **Use exactly this editor.** 7000.0.0a16 is an alpha, so the Unity Hub does not offer it in its normal list; open
> `unityhub://7000.0.0a16/0745a91df592` (Hub installs it), then check **Help > About Unity** says `7000.0.0a16 (0745a91df592)`.
> Entities 7 and Entities Graphics 7 are built into that editor. Unity 6 (6000.x, what the Hub recommends) has no such packages
> (the registry stops at Entities Graphics 1.x), and installing Tl there fails with
> `com.unity.entities.graphics: Dependency was not resolved`. If you see that line, you are on the wrong editor.
> Other 7000.0 alphas are untested.

Then Package Manager > **Tl Unity Playback** > **Samples** > **Hello Tl** > **Import**. Menu **Tl Hello > Build Scene**, then press **Play**.

The Console prints:

```
Hello Tl: the player is at x = 5 (timeline tick 30)
Hello Tl: the player is at x = 13 (timeline tick 90)
Hello Tl: the player is at x = 5 (timeline tick 119)
```

That is the whole program. Read it. All of Tl's part is this one file, `Teleport.cs`, written in the order it runs:

```csharp
using Tl;

namespace HelloTl
{
    [TlClip("Hello/Teleport")]
    public struct TeleportClip
    {
        public float Distance;
        public bool ComeBack;
    }

    public struct TeleportTrack : ITlTrack<TeleportClip>
    {
        [Pulse(typeof(TeleportKind), On = ClipEvent.Start)]
        public static float JumpAtClipStart(in TlFrame<TeleportClip> frame)
            => frame.Direction * frame.Clip.Distance;

        [Pulse(typeof(TeleportKind), On = ClipEvent.End)]
        public static float ReturnAtClipEnd(in TlFrame<TeleportClip> frame)
            => frame.Clip.ComeBack ? -frame.Direction * frame.Clip.Distance : 0f;
    }

    [TlKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
    public struct TeleportKind
    {
        public static void Apply(in float jumpDistance, ref Teleported player)
            => player.Offset += jumpDistance;
    }

    [TlState]
    public partial struct Teleported
    {
        public float Offset;
    }
}
```

and your game code, `PlayerTeleportSystem.cs`, which moves the player to the number Tl computed:

```csharp
[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
public partial struct PlayerTeleportSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (playerTransform, teleported) in SystemAPI.Query<RefRW<LocalTransform>, RefRO<Teleported>>())
            playerTransform.ValueRW.Position.x = teleported.ValueRO.Offset;
    }
}
```

**Try this now:** select `Teleport.playable` (Window > Sequencing > Timeline, with the Player selected in `Hello Objects`). Change a clip's **Distance** or untick **Come Back** and press Play again. No code changed.

The scene has two clips on one track: ticks 30 to 60 (Distance 5, stay) and 90 to 120 (Distance 8, come back). Result: x is 0, then 5 at tick 30, then 13 at tick 90, then 5 at tick 119.

### Add a sideways move

Goal: while the player jumps 5 forward, it also moves 2 to the side, so the position goes from `(0, 0, 0)` to `(5, 0, -2)`. This is five small changes in `Teleport.cs` and one line in your system. Each change gives you one thing. Skip one and Tl still compiles, but that part stays 0 and nothing tells you why.

**1. Carry both numbers together.** Gives you: one move with two parts, so one kind and one write, not two of each.

```diff
+    public struct Move
+    {
+        public float Forward;
+        public float Side;
+    }
```

**2. Let each clip say how far to the side.** Gives you: a `Side` value on every clip, set per clip in your timeline (negative goes the other way).

```diff
     public struct TeleportClip
     {
         public float Distance;
         public bool ComeBack;
+        public float Side;
     }
```

**3. Make the track send a `Move`.** Gives you: the jump at the start of a clip and, when `ComeBack` is on, the same move undone at the end. `frame.Direction` makes rewinding work for free.

```diff
-        public static float JumpAtClipStart(in TlFrame<TeleportClip> frame)
-            => frame.Direction * frame.Clip.Distance;
+        public static Move JumpAtClipStart(in TlFrame<TeleportClip> frame)
+            => new Move { Forward = frame.Direction * frame.Clip.Distance, Side = frame.Direction * frame.Clip.Side };

-        public static float ReturnAtClipEnd(in TlFrame<TeleportClip> frame)
-            => frame.Clip.ComeBack ? -frame.Direction * frame.Clip.Distance : 0f;
+        public static Move ReturnAtClipEnd(in TlFrame<TeleportClip> frame)
+            => frame.Clip.ComeBack
+                ? new Move { Forward = -frame.Direction * frame.Clip.Distance, Side = -frame.Direction * frame.Clip.Side }
+                : default;
```

**4. Let the kind add both parts.** Gives you: the one place that writes into the column. Because both parts ride in one `Move`, you do not need a second kind.

```diff
-        public static void Apply(in float jumpDistance, ref Teleported player)
-            => player.Offset += jumpDistance;
+        public static void Apply(in Move move, ref Teleported player)
+        {
+            player.Offset += move.Forward;
+            player.Side += move.Side;
+        }
```

**5. Give the column a `Side`.** Gives you: the number your game reads.

```diff
     public partial struct Teleported
     {
         public float Offset;
+        public float Side;
     }
```

**Then, in your own system** (the one that moves the player), read it next to `Offset`. Gives you: the player actually moves sideways.

The sample's `foreach` has no braces, so add them or the new line will not compile:

```diff
 foreach (var (playerTransform, teleported) in SystemAPI.Query<RefRW<LocalTransform>, RefRO<Teleported>>())
+{
     playerTransform.ValueRW.Position.x = teleported.ValueRO.Offset;
+    playerTransform.ValueRW.Position.z = teleported.ValueRO.Side;
+}
```

Set `Side` on the clips in your timeline: `-2` on the first clip and `3` on the second (in JSON: `"Side": -2`). The result:

```
tick  position (x, y, z)
  30  (5, 0, -2)       clip 1 starts: 5 forward, 2 to the side
  90  (13, 0, 1)       clip 2 starts: 8 forward and 3 to the other side
 119  (5, 0, -2)       clip 2 ends with Come Back on: its move is undone
```

In the Hello Tl scene the cube starts at y = 0.5, so there you will see `(5, 0.5, -2)`. Only x and z are changed.

**If the side stays 0,** one of these is missing: the track still returns the old `float` (step 3), `Apply` does not write `Side` (step 4), your system does not read `Side`, or the clip's `Side` is still 0 in the timeline. The complete console version, with a test that checks the output above, is [`demos/hello-side`](https://github.com/IAFahim/tl/tree/main/demos/hello-side).



### An ability window: "can double jump now"

A clip can switch a flag on the player for as long as it plays. Tl only keeps the flag; your own system makes the decision (`if (abilities.ValueRO.CanDoubleJump && jumpPressed)`).

```csharp
using Tl;

namespace HelloAbility
{
    [TlClip("Hello/DoubleJumpWindow")]
    public struct DoubleJumpWindowClip
    {
        public bool CanDoubleJump;
    }

    public struct DoubleJumpWindowTrack : ITlTrack<DoubleJumpWindowClip>
    {
        [Feeds(typeof(DoubleJumpKind))]
        public static bool WhetherDoubleJumpIsAllowed(in TlFrame<DoubleJumpWindowClip> frame)
            => frame.Clip.CanDoubleJump;
    }

    [TlKind]
    public struct DoubleJumpKind
    {
        public static void Combine(ref bool total, in bool next) => total |= next;

        public static void Apply(in bool allowed, ref Abilities player)
            => player.CanDoubleJump = allowed;
    }

    [TlState]
    public partial struct Abilities
    {
        public bool CanDoubleJump;
    }
}
```

Where each thing lives: the clips (when the window starts and ends, and its value) are in the baked timeline, shared by everyone who plays it. The flag itself is the `CanDoubleJump` field of the player's own `Abilities` column. A `[Feeds]` method runs every tick while its clip plays and its result is combined (`Combine`, here OR) and written once by `Apply`.

The timeline has two clips: ticks 30 to 60 with `CanDoubleJump` ticked, and ticks 60 to 150 with it unticked. Result:

```
step  can double jump
   0  False
  30  True
  60  False
a player who never plays the timeline: False
```

- Before the window the flag is false, because a column starts at its default (zero). Nothing is "baked as false"; it is computed every tick.
- Keep the unticked clip, or give the kind an `Idle`. A column keeps the last value it was given when no clip covers a tick, so without one of the two the flag would stay true after the window. The unticked clip is data in the timeline. `Idle` is code on the kind: `public static void Idle(ref Abilities player) => player.CanDoubleJump = false;` runs once per tick for every player that has the column and no clip feeding the kind, so the flag is also false before the first clip and after the timeline ended, and the second clip is no longer needed. `Idle` takes the same columns an `Apply` takes after its first parameter and works on the plain .NET host and in Unity alike.
- In Unity a player needs the timeline (a Playable Director) to get the `Abilities` column at all; a player without one is never matched by a query for it.

The runnable, tested console version is [`demos/hello-ability`](https://github.com/IAFahim/tl/tree/main/demos/hello-ability). In Unity (7000.0.0a16, PlayMode, a SubScene with a Playable Director) both forms were run: the flag is false before tick 30, true from tick 30 to 59 and false from tick 60 with the unticked clip, and also false from tick 60 with `Idle` and no second clip, while a kind without `Idle` and without the unticked clip stays true to the end.
---

## 2. The model: what runs, in what order, where data lives

```
 DESIGNER (Unity Editor)
   types Distance = 5 on a Teleport clip         -> saved in the Timeline asset
        │
        ▼
 BAKING (when the SubScene bakes)
   the object with the Playable Director becomes an entity that holds the timeline,
   plus the columns your kinds write (here Teleported), starting at 0
        │
        ▼
 EVERY TICK, inside TlSimulationGroup        ┐
   1. TRACK   TeleportTrack.* reads a clip and returns a value        │  Tl runs these.
   2. KIND    Combine merges the values of the same tick (if any)     │  You write
              Apply writes the result into your column                │  the functions.
        │                                                             ┘
        ▼
 YOUR SYSTEM (an ISystem, after Tl: OrderLast)
   reads the column, moves the object
        │
        ▼
 DRAWN ON SCREEN
```

Three rules to keep in your head:

1. **Tracks run first, kinds second, your systems last.** A track only *produces* a value. It never touches your data.
2. **Data lives in components on the entity.** The `Teleported` column is a component. Tl's `Apply` writes it and your system reads the same memory. Nothing is copied or sent.
3. **Unity finds the entities by the types you list.** `SystemAPI.Query<RefRW<LocalTransform>, RefRO<Teleported>>()` means "every entity that has both". `ref`/`RefRW` is read-write, `in`/`RefRO` is read-only.

Two other ways to write your system, same result (`IJobEntity` runs on several threads, use it for many entities):

```csharp
[BurstCompile]
[UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
public partial struct PlayerMoveSystem : ISystem
{
    public void OnUpdate(ref SystemState state) => new MovePlayerByOffset().ScheduleParallel();
}

[BurstCompile]
public partial struct MovePlayerByOffset : IJobEntity
{
    void Execute(ref LocalTransform playerTransform, in Teleported teleported)
        => playerTransform.Position.x = teleported.Offset;
}
```

**Why `OrderLast`:** systems in a group do not run in a guaranteed order. `OrderLast = true` puts yours after Tl, so you read this tick's value. Unity also makes your system wait for Tl's jobs that write the column you read.

---

## 3. The five pieces, one by one

Each piece answers one question. If you ever wonder "where does this go?", ask the question.

### 3.1 Clip: "what does the designer type?"

```csharp
[TlClip("Hello/Teleport")]
public struct TeleportClip { public float Distance; public bool ComeBack; }
```

- Plain data, no logic. Every field becomes an Inspector field on the clip.
- The text in `[TlClip("...")]` is the name in the Timeline window's **+** menu. A slash makes a submenu.
- Fields must be **unmanaged**: numbers, bool, enums, structs of those, `float3`. No `string`, no classes. A `TlCurve` field shows as an `AnimationCurve` and is sampled to 16 points. For text, use a `TlText` field (see 8.5).
- Another timeline as a field: use `TlTimeline` (the designer drags a timeline in, you can start it from code, see 8.4).

### 3.2 Track: "what value does this clip produce?"

```csharp
public struct TeleportTrack : ITlTrack<TeleportClip> { ... }
```

- `ITlTrack<TeleportClip>` is the link: **this track handles that clip type**. One track type per clip type.
- A track has **no data of its own**. Everything it needs is on the clip. (See 13.)
- It contains static methods. Each carries an attribute that says where the value goes:
  - `[Feeds(typeof(SomeKind))]`: called **every tick the clip is active**, returns that tick's value.
  - `[Pulse(typeof(SomeKind), On = ...)]`: called **once at a point on the clip**, returns the value of that event.
- Name these methods for what they do (`JumpAtClipStart`). Their names are free.

`On =` takes `ClipEvent.Start` (the clip's first tick), `ClipEvent.End` (the clip's **last** tick, e.g. tick 119 for a clip ending at 120) or `ClipEvent.Marker` (a marker you place, see 8.5).

### 3.3 Kind: "how do values meet, and where do they end up?"

```csharp
[TlKind(Accepts = KindEntries.Pulses, Mode = KindMode.Each)]
public struct TeleportKind
{
    public static void Apply(in float jumpDistance, ref Teleported player) => player.Offset += jumpDistance;
}
```

A kind is one rule for one thing (teleport, health, wind). Many clips and tracks can feed it. Two functions:

- `Combine(ref T total, in T next)`: how two values merge (usually `total += next`). Only needed when values merge.
- `Apply(in T total, ref YourColumn column)`: writes the result into your column. The type `T` (here `float`) is whatever your tracks return. The second parameter names the column.

Tl wires track, kind and column **by types**, not by names or registration: the value type `T` must be the same in the track's return, `Combine` and `Apply`. `T` can be `int`, `float`, `float3` or any unmanaged struct.

**`Accepts`: what feeds this kind?** (`KindEntries`)

| Value | Meaning | Real use |
|---|---|---|
| `Spans` (default) | a value every tick while a clip is active (`[Feeds]`) | wind push, speed boost, light fade |
| `Pulses` | one-time events (`[Pulse]`) | give item, teleport, hit |
| `Both` | either | health: poison drains per tick, a potion heals once |

**`Mode`: what if several arrive on the same tick?** (`KindMode`)

| Value | Meaning | Real use |
|---|---|---|
| `Combine` (default) | merge everything into one value, then `Apply` once | "the total wind right now" |
| `Each` | `Apply` once **per pulse**, never merged | three hits on one tick must each count |

**`Order`: which kind runs first** (7.4). The attribute also has `Group`, `FoldsPulses`, `Inline` and `GamePulses`; they are tuning options not needed to start.

### 3.4 Column (`[TlState]`): "where is the result stored?"

```csharp
[TlState]
public partial struct Teleported { public float Offset; }
```

- A plain struct that Tl turns into an ECS component (that is why it is `partial`) and **adds to the entity for you**, with all fields 0.
- It must be `partial`, a `struct`, and not nested in another type (a namespace or the global namespace is fine). Otherwise you get error `TLUNITY9107`.
- It is added only to entities whose timelines actually reach a kind that writes it. A kind that no clip feeds adds nothing. Tl also keeps a value your own baker already set.
- It is optional. You can write to your own `IComponentData` instead; then your own baker must add it (the Force demo does this). `[TlState]` saves that step and also works in plain .NET.
- An entity that is fed **only by game code** (no timeline reaching the kind) never gets the column: add it yourself.
- **Starting value is 0.** To start a health at 100, add your own baker that adds `Health { Value = 100 }`. Tl keeps it.

### 3.5 Frame: "what do I know about this moment?"

`TlFrame<TClip>` is what your track methods receive.

| Member | Meaning |
|---|---|
| `frame.Clip` | **your clip struct**, with the designer's numbers |
| `frame.Direction` | `+1` going forward, `-1` when time runs backward (use it, chapter 10) |
| `frame.Progress` | 0 to 1 across the clip |
| `frame.Tick`, `frame.WithinClip`, `frame.ClipLength` | the timeline tick, ticks since the clip started, the clip's length |
| `frame.TrackIndex` | which track in the timeline (a number only) |

---

## 4. Pattern A: an instant event (pulse)

Use a pulse for things that **happen once**: give an item, teleport, deal a hit.

"Once" means once **per crossing of a point on the timeline**: once when the playhead reaches the clip's first tick, once at its last tick. If the timeline loops, it happens again on each lap. If time runs backward over it, it happens in reverse (`frame.Direction = -1`).

That is exactly the teleport in chapter 1. Why each part is there:

- `Mode = Each`: every jump must happen exactly once, so no merging.
- `frame.Direction * ...`: so a rewind undoes the jump (chapter 10).
- The second pulse returns `0` when `ComeBack` is off, so the player stays.

Result you will see: `0 → 5` at tick 30, `5 → 13` at tick 90, `13 → 5` at tick 119.

---

## 5. Pattern B: a value over time (feeds)

Use `[Feeds]` for things that **last**: a push, a boost, a slide. The track is called every tick the clip is active and returns this tick's share. The kind adds everything up and `Apply` writes it.

```csharp
using Tl;

namespace Game
{
    [TlClip("Player/Wind")]
    public struct WindClip
    {
        public float PushPerTick;
    }

    public struct WindTrack : ITlTrack<WindClip>
    {
        [Feeds(typeof(PushKind))]
        public static float WindClipToPushThisTick(in TlFrame<WindClip> frame)
            => frame.Clip.PushPerTick * frame.Direction;
    }

    [TlKind]
    public struct PushKind
    {
        public static void Combine(ref float pushFromClipsSoFar, in float pushFromAnotherClip)
            => pushFromClipsSoFar += pushFromAnotherClip;

        public static void Apply(in float totalPushFromAllClipsThisTick, ref PlayerPush player)
            => player.TotalPushedSoFar += totalPushFromAllClipsThisTick;
    }

    [TlState]
    public partial struct PlayerPush
    {
        public float TotalPushedSoFar;
    }
}
```

Put two Wind clips on **two tracks**: Track 1 `PushPerTick = 2` at ticks 10 to 50, Track 2 `PushPerTick = 3` at ticks 30 to 50. Result, measured:

```
tick  9 : 0
tick 29 : 40      (only clip 1: 2 per tick for 20 ticks)
tick 49 : 140     (from tick 30 both: 2 + 3 = 5 per tick for 20 ticks => +100)
```

**Why this helps:** two effects that overlap simply add. You wrote no code for "what if two winds overlap".

The value can be a struct. Replace `float` with your own `struct Push { public float X; public float Y; }` in the track's return, `Combine` and `Apply` and nothing else changes.

---

## 6. Pattern C: both in one rule (health)

A health has instant events (a hit, a potion) **and** a lasting one (poison). One kind takes both.

```csharp
using Tl;

namespace Game
{
    [TlClip("Stats/Hit")]    public struct HitClip    { public int Amount; }
    [TlClip("Stats/Heal")]   public struct HealClip   { public int Amount; }
    [TlClip("Stats/Poison")] public struct PoisonClip { public int Amount; public int Every; }

    public struct HitTrack : ITlTrack<HitClip>
    {
        [Pulse(typeof(Damage), On = ClipEvent.Start)]
        public static int HitAtClipStart(in TlFrame<HitClip> frame) => frame.Direction * frame.Clip.Amount;
    }

    public struct HealTrack : ITlTrack<HealClip>
    {
        [Pulse(typeof(Damage), On = ClipEvent.Start)]
        public static int HealAtClipStart(in TlFrame<HealClip> frame) => -frame.Direction * frame.Clip.Amount;
    }

    public struct PoisonTrack : ITlTrack<PoisonClip>
    {
        [Feeds(typeof(Damage))]
        public static int PoisonEveryNTicks(in TlFrame<PoisonClip> frame)
            => frame.WithinClip % frame.Clip.Every == 0 ? frame.Direction * frame.Clip.Amount : 0;
    }

    [TlKind(Accepts = KindEntries.Both)]
    public struct Damage
    {
        public static void Combine(ref int total, in int next) => total += next;
        public static void Apply(in int totalDamageThisTick, ref Health health) => health.Value -= totalDamageThisTick;
    }

    [TlState]
    public partial struct Health { public int Value; }
}
```

Run with health 100; Hit 10 at tick 30; Poison 2 every 10 ticks from tick 60 to 120; Heal 15 at tick 100. Measured:

```
step  30  health 90     the hit
step  60  health 88     poison
step  70  health 86
step  80  health 84
step  90  health 82
step 100  health 95     poison (-2) and heal (+15) on the SAME tick: Combine adds them, Apply runs once
step 110  health 93     poison
```

Note the heal is a **negative damage**. One rule for all damage and healing keeps the ordering simple.

---

## 7. Many at once: timelines, tracks, blending, order

So far one timeline played, and each track had one clip at a time. A real object has more going on: a platform rides up while something shakes it. Values from many clips can meet in three ways, and this chapter adds them to one platform one at a time: two **timelines** at once (7.1), two **tracks** at once (7.2), and two **clips** at once on one track (7.3). The finished scene is the **Many At Once** sample: Package Manager > **Tl Unity Playback** > **Samples** > **Many At Once** > **Import**, menu **Tl Many At Once > Build Scene**, press **Play**. The sample ships the end of the chapter; to start where this chapter starts, untick **Play On Awake** on Shake's row (7.1) and use the `Platform.cs` below.

The Tl part is one file, `Platform.cs`. It is chapter 5's wind with a height instead of a push: each clip says how high it lifts the platform this tick, `Combine` adds what arrives on the same tick, and `Apply` writes the total.

```csharp
using Tl;

namespace ManyAtOnce
{
    [TlClip("Many At Once/Height")]
    public struct HeightClip
    {
        public float Height;
    }

    public struct HeightTrack : ITlTrack<HeightClip>
    {
        [Feeds(typeof(HeightKind))]
        public static float HeightClipToHeightThisTick(in TlFrame<HeightClip> frame)
            => frame.Clip.Height;
    }

    [TlKind]
    public struct HeightKind
    {
        public static void Combine(ref float heightFromClipsSoFar, in float heightFromAnotherClip)
            => heightFromClipsSoFar += heightFromAnotherClip;

        public static void Apply(in float totalHeightThisTick, ref Platform platform)
            => platform.Height = totalHeightThisTick;
    }

    [TlState]
    public partial struct Platform
    {
        public float Height;
    }
}
```

Your game code puts the platform at that height, the same as chapter 1's system:

```csharp
using Tl;
using Unity.Burst;
using Unity.Entities;
using Unity.Transforms;

namespace ManyAtOnce
{
    [BurstCompile]
    [UpdateInGroup(typeof(TlSimulationGroup), OrderLast = true)]
    public partial struct PlatformLiftSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (platformTransform, platform) in SystemAPI.Query<RefRW<LocalTransform>, RefRO<Platform>>())
                platformTransform.ValueRW.Position.y = platform.ValueRO.Height;
        }
    }
}
```

The sample also has `PlatformLogSystem.cs`, which prints a line every 15 ticks of Ride: Ride's tick, the height, and how many timelines play. It is a system in `TlSimulationGroup`, not a `MonoBehaviour`: Tl ticks in `FixedStepSimulationSystemGroup`, so one frame can run two ticks or none, and a `MonoBehaviour` would miss some.

The platform's Playable Director plays **Ride** (the builder makes it; a designer edits it in the Timeline window):

| Track | Clip | Ticks | Height |
|---|---|---|---|
| Floor | Ground | 0 to 120 | 1 |
| Floor | Top | 90 to 180 | 5 |
| Wave | Wave | 30 to 60 | 0.5 |

Press **Play**. The Console prints:

```
Many at once: Ride tick 0, platform height 1, playbacks 1
Many at once: Ride tick 15, platform height 1, playbacks 1
Many at once: Ride tick 30, platform height 1.5, playbacks 1
Many at once: Ride tick 45, platform height 1.5, playbacks 1
Many at once: Ride tick 60, platform height 1, playbacks 1
Many at once: Ride tick 75, platform height 1, playbacks 1
Many at once: Ride tick 90, platform height 1, playbacks 1
Many at once: Ride tick 105, platform height 1, playbacks 1
Many at once: Ride tick 120, platform height 5, playbacks 1
Many at once: Ride tick 135, platform height 5, playbacks 1
Many at once: Ride tick 150, platform height 5, playbacks 1
Many at once: Ride tick 165, platform height 5, playbacks 1
```

One thing in it is not what the timeline seems to say: the platform waits at 1 while Top already plays (ticks 90 to 119), then jumps to 5. Section 7.3 turns that into a ride. First, a second timeline.

### 7.1 Two timelines on one platform

**Shake** is a second timeline with one track, **Bump**: one clip, ticks 15 to 30, height 0.25. The platform should play it at the same time as Ride.

**Starting it.** **Play On Awake** on the Playable Director starts only its Playable. In the director's **Tl** foldout, put Shake in **Also Plays** and tick **Play On Awake** on its row. Now the Console prints:

```
Many at once: Ride tick 0, platform height 1, playbacks 2
Many at once: Ride tick 15, platform height 1.25, playbacks 2
Many at once: Ride tick 30, platform height 1.5, playbacks 1
Many at once: Ride tick 45, platform height 1.5, playbacks 1
Many at once: Ride tick 60, platform height 1, playbacks 1
Many at once: Ride tick 75, platform height 1, playbacks 1
Many at once: Ride tick 90, platform height 1, playbacks 1
Many at once: Ride tick 105, platform height 1, playbacks 1
Many at once: Ride tick 120, platform height 5, playbacks 1
Many at once: Ride tick 135, platform height 5, playbacks 1
Many at once: Ride tick 150, platform height 5, playbacks 1
Many at once: Ride tick 165, platform height 5, playbacks 1
```

Each timeline that plays is one **playback** on the entity (`playbacks 2`), and every playback feeds the same kind. On the ticks where both have a clip, `Combine` adds them: tick 15 is Ground (1) plus Bump (0.25) = 1.25. When Shake ends at tick 30 its playback is removed and Ride goes on alone (`playbacks 1`).

The rules of **Play On Awake** in **Also Plays**: the ticked timelines start when the scene loads, right after the Playable, at tick 0, in the order of the **Also Plays** list. They also start when the director's own **Play On Awake** is off. The Playable always starts at the director's **Initial Time**, whether it is started by the director or ticked in **Also Plays**. To start or stop a timeline from your own code instead, use `requests.Start(shake)` and `requests.Stop(shake)` with a `TlTimeline` from your baker (8.4, 9.3).

**Channels.** Ride and Shake are on channel **None**, so they play side by side. Only one timeline plays per channel: give both the same **Channel** (Timeline asset > **Tl** section) and Shake, which starts after Ride, replaces it. The package test `ATimelineTickedPlayOnAwakeOnThePlayablesChannelReplacesIt` checks this: one playback, and only the later timeline's values reach the kind. Put timelines that must not overlap (walk and run) on one channel; leave the rest on None. 8.3 has the rest.

### 7.2 Two clips at once on two tracks

Ride has two tracks. **Wave** carries one clip, ticks 30 to 60, height 0.5. From tick 30 Floor and Wave both have a clip, so `Combine` adds them: 1 + 0.5 = 1.5 (ticks 30 and 45 in both runs above). It is the same rule as two timelines: whatever reaches a kind on one tick is combined, whichever track or timeline it came from. Chapter 5's two winds were this case.

### 7.3 Two clips at once on one track: blend, or the first counts

On the Floor track, Top (ticks 90 to 180, height 5) overlaps Ground (ticks 0 to 120, height 1) for ticks 90 to 119. Clips on **one** track are never combined. A clip type that does not implement `IBlend`, like `HeightClip` so far, keeps the **first** clip (the one that starts earlier) until it ends. That is the wait at 1 and the jump to 5 at tick 120 in both runs above.

A clip that implements `IBlend<HeightClip>` **crossfades** instead. This is the whole change, and it is the sample's `Platform.cs`:

```csharp
using Tl;

namespace ManyAtOnce
{
    [TlClip("Many At Once/Height")]
    public struct HeightClip : IBlend<HeightClip>
    {
        public float Height;

        public void Blend(in HeightClip first, in HeightClip second, float factor, out HeightClip result)
            => result = new HeightClip { Height = first.Height + (second.Height - first.Height) * factor };
    }

    public struct HeightTrack : ITlTrack<HeightClip>
    {
        [Feeds(typeof(HeightKind))]
        public static float HeightClipToHeightThisTick(in TlFrame<HeightClip> frame)
            => frame.Clip.Height;
    }

    [TlKind]
    public struct HeightKind
    {
        public static void Combine(ref float heightFromClipsSoFar, in float heightFromAnotherClip)
            => heightFromClipsSoFar += heightFromAnotherClip;

        public static void Apply(in float totalHeightThisTick, ref Platform platform)
            => platform.Height = totalHeightThisTick;
    }

    [TlState]
    public partial struct Platform
    {
        public float Height;
    }
}
```

Press **Play** again (Shake still ticked):

```
Many at once: Ride tick 0, platform height 1, playbacks 2
Many at once: Ride tick 15, platform height 1.25, playbacks 2
Many at once: Ride tick 30, platform height 1.5, playbacks 1
Many at once: Ride tick 45, platform height 1.5, playbacks 1
Many at once: Ride tick 60, platform height 1, playbacks 1
Many at once: Ride tick 75, platform height 1, playbacks 1
Many at once: Ride tick 90, platform height 1, playbacks 1
Many at once: Ride tick 105, platform height 3.0689654, playbacks 1
Many at once: Ride tick 120, platform height 5, playbacks 1
Many at once: Ride tick 135, platform height 5, playbacks 1
Many at once: Ride tick 150, platform height 5, playbacks 1
Many at once: Ride tick 165, platform height 5, playbacks 1
```

`Blend` runs only on overlap ticks. It gets the first clip, the second clip and a `factor` that is 0 on the first tick of the overlap and 1 on its last, in equal steps (here 1/29 per tick; a one-tick overlap gets 0.5), and it fills `result` with the clip to use this tick. Tick 105 is 1 + 4 x 15/29 = 3.0689654: the platform rides from 1 to 5 between ticks 90 and 119.

What to know about overlaps on one track:

- **Different tracks add, one track blends or keeps the first.** To stack two effects, put them on separate tracks. To hand one over to the next smoothly, overlap them on one track and give the clip `IBlend`. Tell your designers.
- The factor is a straight line across the overlap. The ease-in and ease-out curves the Timeline window draws for overlapping clips are not read.
- **Pulses on one track see the overlap as one long clip:** the first clip's `Start` and the last clip's `End` fire; the second clip's `Start` and the first clip's `End` do not. `IBlend` does not change this (the same pulses fired on .NET before blending existed). Measured in Unity (`TlTimelineTests`) and on .NET (`ClipBlendTests`). If every clip's pulses must fire, put the clips on separate tracks.
- Blending is the clip's job here because a Timeline-window track carries no data of its own (13).

### 7.4 Which kind runs first: `Order`

`[TlKind(Order = n)]`: lower `n` runs first; ties are sorted by name. This matters when one kind **reads what another wrote**. A track can read any column by listing it as a parameter:

```csharp
[Feeds(typeof(HazardKind))]
public static float HazardClipToDamageThisTick(in TlFrame<HazardClip> frame, in PlayerPush playerPush)
    => playerPush.Total * frame.Clip.DamagePerPush;
```

Measured, a wind kind writes `PlayerPush` and a hazard kind reads it (both clips start at tick 10, 10 push per tick, 0.1 damage per push):

| Tick | Wind `Order = 0`, Hazard `Order = 1` | Wind `Order = 1`, Hazard `Order = 0` |
|---|---|---|
| 10 | push 10, health **99** | push 10, health **100** |
| 29 | push 100, health **45** | push 100, health **55** |

With the hazard first it always sees last tick's push. Tracks have no order of their own: they only feed their kind.

### 7.5 Your own systems

Use the usual `[UpdateAfter]`, `[UpdateBefore]`, or `OrderLast` in `TlSimulationGroup` (as in chapter 2).

---

## 8. Designing the timeline side: end, channels, starting, curves

### 8.1 Where the settings live

Select the Timeline **asset**: the Inspector has a **Tl** section with **Channel**, **When it ends** and **Play next**. On the object's **Playable Director** the **Tl** foldout has **Also Plays** (extra timelines this object can start; tick **Play On Awake** on a row to start it with the scene, 7.1) and lists **Plays**.

**Unity's own Wrap Mode on the Playable Director is ignored by Tl.** Only Tl's **When it ends** counts. (Stored in a hidden marker named **Tl Settings** at time 0 on the timeline.)

### 8.2 When it ends

| Value | Meaning |
|---|---|
| Stop | the playback is removed (your columns keep their last value) |
| Loop | starts again at the beginning |
| Hold | stays on the last frame |
| Next | starts the timeline you picked in **Play next** |

"Play next" chains timelines. Push → Return → Push forever is just two timelines pointing at each other. It is not Unity's ping-pong.

### 8.3 Channels

Only **one timeline plays per channel** on an entity: starting another on the same channel replaces it. **None** (channel 0) means no channel: any number can play at once, and starting the same timeline twice gives two playbacks. Name channels in Project Settings > **Tl**; the names are what exported JSON uses.

### 8.4 Starting and controlling from code

Get the object's `DynamicBuffer<TimelineRequest>` and use `TlRequests`:

```csharp
public struct StartIt : IComponentData { public Tl.TlTimeline Timeline; }   // the designer drags a timeline in

void Execute(in StartIt startIt, DynamicBuffer<TimelineRequest> requests) => requests.Start(startIt.Timeline);
```

`timeline` is either a `TlTimeline` (an asset a designer dragged in) or a library id (`ushort`): `library.Find("healing_hand")` on the `TlLibrary` (main thread) gives the id of a timeline by its own name. Every call takes the entity (its buffer), then the timeline or channel, then the rest, the same order as tl's .NET `PlaybackTable`:

| Call | Does | tl .NET (`PlaybackTable`) |
|---|---|---|
| `Start(timeline, atTick)`, `Stop(timeline)`, `Pause(timeline)`, `Resume(timeline)` | the basics | `Start(entity, timeline, at)`, `Stop(entity, timeline)`, ... |
| `Seek(timeline, tick)` | jumps there, fires no pulses on the way | `Seek(entity, timeline, tick)` |
| `Reverse(timeline, on)` | this timeline runs backward | `Reverse(entity, timeline, on)` |
| `SetSpeed(timeline, sixteenths)` | 16 is 1x, 32 is 2x, 8 is 0.5x | `SetSpeed(entity, timeline, sixteenths)` |
| `StopChannel`, `PauseChannel`, `ResumeChannel`, `SeekChannel`, `ReverseChannel`, `SetChannelSpeed`, `StopAll` | the same for a whole channel | the same names, entity first |
| `Reset()` | the row is a new entity: drops its playbacks and queued pulses with no end pulses | `Reset(entity)` |

Requests apply at the start of the next tick, in the order you send them. **Play on Awake** on the Playable Director starts its timeline when the SubScene loads. In a MonoBehaviour baker use `TlBaking.GetTimeline(this, timeline)` to get a `TlTimeline`.

**Another entity.** Any system can start, stop or push a timeline on any entity: write that entity's buffer through a `BufferLookup<TimelineRequest>`. A healer whose cast clip raises an event starts "healing_hand" on its target the same way this tested job (`Tests/Kinds/Harness/KindWorld.cs`, run by `KindHashTests`) starts `stomp` on the row a whistling row aims at:

```csharp
public struct Aim : IComponentData
{
    public Entity Target;
}

[BurstCompile]
public partial struct StompTheAimOnACue : IJobEntity
{
    public BufferLookup<TimelineRequest> Requests;

    void Execute(in DynamicBuffer<TimelineEvent<Cue>> cues, in Aim aim)
    {
        for (var i = 0; i < cues.Length; i++)
            global::Tl.TlRequests.Start(Requests[aim.Target], Timelines.Stomp);
    }
}
```

Run it from a system in `TlSimulationGroup` with `OrderLast = true` (events are refilled every tick, so a reader at frame rate misses ticks) and schedule it with `Schedule`, not `ScheduleParallel` (two casters may aim at one target), or record into an `EntityCommandBuffer` that plays back before the next tick. The request lands on the next tick and the target's kinds apply it there. Take the id once from `library.Find("healing_hand")` or bake it from a `TlTimeline` field. `global::Tl.TlRequests.Start(buffer, id)` is the extension `buffer.Start(id)` written out, for files where `using Tl;` would make `Playback` ambiguous. A start the target cannot hold (its kinds do not cover the timeline) is dropped and counted in `TimelineKindTick.DroppedStarts`; with the scripting define `TL_CHECKED`, `TlCheckedStartsSystem` throws at the end of that step and Unity logs the exception.

### 8.5 Curves, markers, baking

- A `TlCurve` clip field is an `AnimationCurve` in the Inspector, sampled to 16 points. Read it with `curve.Evaluate(frame.Progress)`.
- A `TlText` clip or marker field is a text box in the Inspector. The clip holds an 8-byte key, and the baked library stores the string once. Read it on the main thread with `library.Text(clip.Line)`, or in Burst with `LibraryViewReader.Text(in timelineLibrary.View, clip.Line)`, which gives the UTF-8 bytes. An empty box is `TlText.None`.
- A marker is a point (not a stretch) on the timeline. Write `struct Bell : IMarker<BellMarker>` with a `[Pulse(typeof(MyKind), On = ClipEvent.Marker)]` method taking `MarkerFrame<BellMarker>`. See the Inventory pack's `PickupMarker`.
- Need extra components for a clip? Write `ITlBaker<TClip>` and call `baker.AddComponent<T>()`, `AddBuffer<T>()`, `AddBlob(...)`.
- Ticks per second (default 60) are in Project Settings > **Tl**. Clip times round to whole ticks.

### 8.6 Look inside while you work

- **`GameObject > Tl > Panel`**: Play, Pause, Resume, Stop, Reverse, Restart, seek ±1 s, speed 0.25x to 4x, world rewind. Works in any scene.
- **`Tools > Tl > Kind Report`**: lists kinds and their sizes.
- Change a clip value in **Play mode**: it takes effect live. Drag the Timeline playhead in Edit mode: the object moves without Play.

---

## 9. Baking: how your data gets into the entity (the Unity way)

Everything so far assumed "the entity has a timeline and a column". Baking is how that happens. If you know Unity DOTS you know most of this; the Tl-specific parts are marked.

### 9.1 What baking is

In Unity ECS you author with GameObjects and components, and a **SubScene** turns them into **entities** ahead of time. A **baker** is the code that does the turning: it reads an authoring component and adds ECS components to the entity.

```
GameObject (authoring)  ──Baker──►  Entity (runtime data)  ──loads──►  your systems
 in a SubScene                       stored with the SubScene           (async, when the scene loads)
```

Who bakes what for a Tl object:

| Baker | Runs on | Does |
|---|---|---|
| **Tl's director baker** | every Playable Director that has Tl tracks | packs its timelines (and the ones reached by **Play next**, **Also Plays** and `TlTimeline` fields) into the entity, adds the playback and request buffers, and Play On Awake (the Playable's, then the **Also Plays** rows ticked **Play On Awake**) |
| **Tl's kind baking system** | after all bakers | adds the column of every kind the entity's timelines reach, **but only if the entity does not already have it** |
| **Your bakers** | your authoring components | add your own components, starting values, references |

That last row is why "starting values" work: if **your** baker adds `Health { Value = 100 }`, Tl sees the entity already has a `Health` and leaves it alone. (This comes from the code: the system adds a column only when `HasComponent` is false.)

### 9.2 The rules of Unity baking you need

- **Authoring components are MonoBehaviours in a runtime assembly** (not an Editor one), one class per file, named like the file. A baker can sit next to it.
- A baker class is `Baker<YourAuthoring>` and needs a reference to `Unity.Entities.Hybrid`.
- **Objects must be inside a SubScene** (a GameObject with a `SubScene` component pointing at a scene). No SubScene, no entity.
- To make an entity **move**, ask for it with `GetEntity(TransformUsageFlags.Dynamic)`. (Tl's director baker already does this for the object with the Playable Director, which is why the Hello Tl player can move.)
- At runtime baked entities have **no names**. Find them by their components.
- SubScenes load **asynchronously**. In a test, wait on real time, not frame count.
- A bake can go **stale** after you change a baker or an asset. Re-import the SubScene's scene asset (Project window > right-click > Reimport) and check one baked component exists.

### 9.3 Your own baker: starting values and references

Goal: a player that starts at 100 health, and a reference to another object and to a timeline.

```csharp
using Game;                       // your Health column
using Tl;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Timeline;

public sealed class PlayerAuthoring : MonoBehaviour
{
    public int StartHealth = 100;
    public GameObject SparkEffect;       // another object (prefab)
    public TimelineAsset OnDeath;        // a timeline the designer drags in
}

public struct PlayerDeath : IComponentData
{
    public Entity SparkEffect;
    public TlTimeline OnDeath;           // can be started from code (chapter 8.4)
}

public sealed class PlayerBaker : Baker<PlayerAuthoring>
{
    public override void Bake(PlayerAuthoring authoring)
    {
        var player = GetEntity(TransformUsageFlags.Dynamic);
        AddComponent(player, new Health { Value = authoring.StartHealth });
        AddComponent(player, new PlayerDeath
        {
            SparkEffect = GetEntity(authoring.SparkEffect, TransformUsageFlags.Dynamic),
            OnDeath = TlBaking.GetTimeline(this, authoring.OnDeath),
        });
    }
}
```

Not run as written. It follows the Events demo's `ArenaAttackerBaker`, which is run by that demo's test (it bakes a target, a prefab and a `Hurt` timeline the same way).

What this shows:

- `AddComponent(player, new Health { Value = ... })` sets the **starting value**. Tl keeps it (9.1).
- `GetEntity(otherObject, ...)` turns a scene object into an entity reference. This is also how you give a clip's game code a **target**, since Tl clips cannot hold object references (chapter 13).
- `TlBaking.GetTimeline(this, timeline)` turns a Timeline asset into a `TlTimeline` your code can pass to `requests.Start(...)`. The timeline must be in the loaded library: on some object's Playable Director, its **Also Plays** or a `TlTimeline` field, or in a library you load. Any Tl object can then play it; an object gets the components of a kind the first time it starts a timeline that needs them.

### 9.4 A baker per clip: `ITlBaker<TClip>`

Sometimes a **clip** needs extra data on the entity: a curve, a buffer, an extra component. Write an `ITlBaker<TClip>`. It runs once for each clip of that type on the object. The Force demo's:

```csharp
public struct ForceBaking : ITlBaker<ForceClip>
{
    public void Bake(ref TlBaker baker, in ForceClip clip)
    {
        baker.AddComponent<Force>();
        baker.AddBuffer<ForceLog>();
        baker.AddBlob(new ForceFalloff { Curve = baker.CreateCurve(clip.Falloff) });
    }
}
```

Rules: it must live in the **same assembly as the clip**, and it must be unmanaged. Use it when the data depends on the clip's numbers. Use a normal `Baker<T>` (9.3) when the data depends on the object.

### 9.5 Seconds and ticks

Designers think in seconds, Tl runs in ticks (default 60 per second, Project Settings > **Tl**). In a baker use `TlBaking.Ticks(seconds)` to convert, and store ticks in your component. `TlBaking.TicksPerSecond` gives the rate.

### 9.6 Prefabs and many entities

A prefab referenced from an authoring component is baked as an **entity prefab** (`GetEntity(prefab, TransformUsageFlags.Dynamic)`). A spawn system instantiates it and can start a timeline on every copy. The Crowd demo does exactly this for 10,000 and 100,000 dancers: each gets its own start tick (`requests.Start(dance, offset)`), costs about 32 ns per entity per tick, allocates 0 bytes.

### 9.7 Live baking: the fun feedback loop

Open the SubScene (tick the checkbox next to it in the Hierarchy) and press Play. **Edit a clip's number in the Inspector while the game runs**: Unity re-bakes and patches the running entity within a few milliseconds, and the timeline keeps playing. Drag the Timeline playhead in Edit mode and the object moves without Play. Use this to tune.

### 9.8 "My thing was not baked": checklist

| Symptom | Check |
|---|---|
| No entity at all | is the object inside a SubScene, and is the SubScene loaded? |
| Entity exists but no timeline | does the object have a Playable Director with a Timeline that has at least one Tl clip? |
| Column missing | does a clip on one of its timelines feed a kind that writes it? If only game code feeds the kind, add the column yourself |
| Column is 0, wanted another value | add your own baker (9.3) |
| Old values after an edit | the bake is stale: Reimport the SubScene |
| Can not move the entity | your baker needs `TransformUsageFlags.Dynamic` |
| Warning "plays a timeline that feeds X, but no Unity system runs it" | the kind's code failed to compile: read the Console |
| Authoring component is ignored | the class name differs from its file name, or it is in an Editor assembly |

---

## 10. Time: rewind, reverse, speed

Three controls: **Reverse** one timeline, **World rewind** (everything runs backward one tick per step), **Speed** per timeline or channel.

**The rule: Tl takes back only what you add and subtract. It cannot take back what you overwrite.**

| Your change | Comes back on rewind? |
|---|---|
| Pulse that returns a signed value (`frame.Direction * x`) | yes, exactly |
| Per-tick `[Feeds]` sum signed with `frame.Direction` | yes, **one tick short** |
| Per-tick `[Feeds]` sum **without** `frame.Direction` | **no**, it keeps adding |
| Your own per-frame integration (`position += v * dt`) | close, not exact |
| A clamp / min / max in `Apply` | no (a clamp forgets what it cut) |
| A value you **set** from the clip (the Motion pack) | follows the clip, forgets what was before it |

So: **always multiply by `frame.Direction`** when your kind adds, and write health as an unclamped sum (clamp when you draw it).

Measured: a per-tick sum walks back step by step, but after a full rewind to tick 0 it is left with the contribution of the tick where time turned around (0.5714 left of 7.714). Pulses have no such leftover: Inventory rewinds to an empty bag; Stats goes 300 steps forward and 300 back to Health 100 and Attack 10.

Other facts you need:

- A reversed timeline that reaches tick 0 **parks** and stays parked while it faces backward.
- World rewind can step back through a **Next** handover into the previous timeline.
- World rewind (`TimelineStep.Forward = false`) also moves playbacks of a library without kinds backward.
- Pausing: a paused timeline does not move in either direction until **Resume** or **Seek**.
- **Speed** is in sixteenths of a tick per step. 1.5x crosses 1, 2, 1, 2 ticks. Kinds and pulses are exact at every speed (tests at 0.5x, 1.5x, 2x, 2.5x, 3x, 4x match a 1x run bit for bit). `ITlTrack` event methods (`OnClipStart`, `OnClipEnd`, `ExecuteActive` with `Events<T>`) walk crossings as on .NET: each step raises events for the ticks it just crossed, read from the same per-crossing copies the kinds apply, so every clip boundary and active tick is raised once at any speed, across a speed change too, and in the same step as the kind pulse it feeds.

---

## 11. Outside Unity, and testing

The first four pieces of `Teleport.cs` use only `using Tl;`, so they are plain C#. The same file runs in a console app, a server, a unit test. Only your Unity system (the one that moves a transform) is Unity-only.

**Runnable version in the tl repository:** [`demos/hello`](https://github.com/IAFahim/tl/tree/main/demos/hello) is Hello Tl as a console program (`dotnet run --project demos/hello -c Release`), with the same `Teleport.cs`.

**The recipe** (this is how the Hello Tl example was run outside Unity):

1. **Export from Unity:** menu **Tl > Export** writes one JSON per timeline plus `tl.project.json`. **Tl > Export Library** writes the baked `game.tlib` directly. **Tl > Export Scene JSON** also writes a scene (entities, starting columns, what plays). **Tl > Import** goes the other way: pick a `tl.project.json` and a folder under `Assets`, and it builds one Timeline asset per timeline listed there. A curve comes back as a 16-point curve through the baked samples; what a Unity timeline has no place for, such as track settings or a timeline asset outside the import, is skipped with a warning.
2. **A console project** that references `Tl.Core` and the Tl code generator from https://github.com/IAFahim/tl (MIT), and contains your `Teleport.cs`.
3. **Run it:**

```csharp
using var world   = new TimelineWorld(File.ReadAllBytes("HelloTl.tlib"));
using var players = new PlaybackTable(1);                  // 1 entity
using var kinds   = new KindStore(world, players);
var state = kinds.Column<Teleported>();                    // your column, one slot per entity

players.Start(0, 1);                                       // entity 0 plays timeline id 1
for (var step = 0; step < 150; step++)
{
    world.Tick(players, true);                             // true = forward
    new Tl.Generated.TlAuto_HelloTl().Run(world, players, true);
}
```

Printing the offset when it changes gives exactly the numbers from Unity: `30 → 5`, `90 → 13`, `119 → 5`.

Things to know:

- The system class `Tl.Generated.TlAuto_<AssemblyName>` is **generated at build time**. The name comes from your assembly name with dots turned into underscores. A system left without columns runs on the store's own, one per type, which `kinds.Column<T>()` hands back to read. To run it on arrays of your own instead, set its column fields, named `C0_<Type>`, `C1_<Type>`; use your editor's completion to see them.
- **Timeline ids** follow the timelines' **name order** in a library baked on .NET, and the order timelines stream in on Unity (`TlLibrary.FromBytes` keeps the baked order). Adding a timeline can renumber them. Look them up by name with `world.Find(name)` (.NET) and `library.Find(name)` (Unity) rather than keeping ids.
- You can bake without Unity: `tl bake tl.project.json -o game.tlib --assembly YourGame.dll` (the `tl` command line tool from the tl repository).
- The exported library contains the kinds of the whole Unity project, so its size depends on what else is in that project. The behaviour for your timeline is the same.
- **Prove two hosts agree: the state hash.** `Tl.TlStateHash` hashes every playback and every column after each step. Same scene, same hash, in `tl run`, in Unity (Burst), and in IL2CPP and CoreCLR players (tested with the Inventory, Stats and Crowd scenes). Use it in your own tests: run N steps, assert the hash.
- **Scenes without an engine:** `tl run scene.json --steps N --assembly Game.dll` plays a scene file (entities, starting columns, `play`, `requests`, world `direction`) and prints the hash. It accepts `SetSpeed`.

**What still differs between the hosts, and why** (everything else, the verbs in 8.4, kinds, pulses and event order, is the same; tl's `docs/playback-control.md` has the long form):

| | tl .NET | Unity | Why |
|---|---|---|---|
| Entity | row index of the `PlaybackTable` | `Entity` and its buffers | ECS storage |
| Timeline handle | library id | library id, or a `TlTimeline` asset | designers drag assets |
| Timeline ids | name order of the baked library | the order timelines stream in (`TlLibrary.FromBytes` keeps the library's) | Unity assembles the library as SubScenes load |
| Requests | one queue per table | one buffer per entity | a write to another entity's buffer needs a single-threaded job or an ECB |
| Game pulse sender | `DamageKind.Pulses(kinds)`, taken once | `DamageKind.Pulses(writer)` per job | the .NET sender holds a managed handle |
| Generated sender for a derived kind (`Sum<T>`, `Set<T>`) | none; `kinds.Pulses<T>("Tl.Derived.<Name>")` | `Tl.Derived.<Name>Kind.Pulses(writer)` | tl's source budget |
| One entity's events | `drained.Of(entity)` after `events.Drain()` | `DynamicBuffer<TimelineEvent<T>>` | same run (slot order, then raise order); `Record.Entity` and `Record.Sequence` are host-specific |
| Columns | any unmanaged struct; a kind column that is not a `[TlState]` struct warns `TLGEN117`, and a later version requires it | a partial top-level `[TlState]` struct or an `IComponentData`; a kind that writes anything else fits no system and warns `TLGEN117` | ECS components |
| A declared `[TimelineSystem]` with events and no kinds of its own | its event methods run | not run (the generated job calls an empty `RunStep`); warns `TLUNITY9109`. Consumers outside `[TimelineSystem]` run in the generated system | open in IAFahim/tl#665 |
| `TLGEN107` (context taken by `ref`) | warning; the system runs sequentially | not reported | ECS schedules the job |
| `FoldsPulses` | game pulses fold | accepted, not folded; warns `TLUNITY9108` | open: Unity has no folding sender yet |
| A marker with no data, or a clip whose bake throws | the bake throws | skipped with a warning, so the rest of the timeline still bakes | by design: the Editor keeps baking the scene |
| Kind groups | run | skipped, TLUNITY9104 | |
| A start that needs kinds the object was not baked with | the row has every kind | the object gets those kinds' components on that start, in the same step, and moves to another chunk once | per-object kind archetypes |
| A start of an id past the library | the step throws an `ArgumentException` before any request applies | dropped and counted in `DroppedStarts`, so `TL_CHECKED` throws at the end of the step | a job cannot throw; each buffer is read inside the tick job |
| Checked builds (throw on a start the entity cannot hold) | `Debug` or `-p:TlChecked=true` | scripting define `TL_CHECKED` | |

---

## 12. Sharp edges: what will bite you

| You see | Cause | Do this |
|---|---|---|
| Two overlapping effects do not add | clips overlap on **one track**: they blend if the clip implements `IBlend`, else the first counts | put them on separate tracks to add (7.3) |
| A second timeline does not start on Play | Play On Awake starts only the Playable | tick **Play On Awake** on its **Also Plays** row, or start it from code (7.1) |
| A clip's Start or End pulse is missing | it overlaps another clip on the same track; the overlap fires only the first Start and the last End | put the clips on separate tracks (7.3) |
| Rewind keeps adding / drifts | the track's value is not signed | multiply by `frame.Direction` (chapter 10) |
| Value starts at 0 though you wanted 100 | a `[TlState]` is added with `default` | add your own baker that sets it |
| Unity's Wrap Mode does nothing | Tl ignores it | use **When it ends** on the Timeline asset (8.1) |
| Pressing Play twice gives two copies | no channel set: playbacks stack | set a **Channel**, or Stop first |
| `TLUNITY9107` | `[TlState]` struct not `partial`, nested, or in the global namespace | make it a `partial struct` directly in a namespace |
| Warning "plays a timeline that feeds X, but no Unity system runs it" | the kind's code did not compile | fix the compile errors; check the **Console** |
| Object invisible | project is Built-in render pipeline; SubScene objects need URP/HDRP | create the project from **Universal 3D** |
| Nothing plays | the object with the Playable Director is not inside a **SubScene** | put it in a SubScene |
| Pulse at clip end fires "early" | `ClipEvent.End` fires on the clip's **last tick** (119 for a clip ending at 120) | account for it |
| A kind sees last tick's value from another kind | it ran first | set `Order` (7.4) |
| Codegen errors in your `IJobEntity` | your namespace starts with `Tl` | name your namespace something else |
| `OnClipStart` events missed at 2x | Unity event methods run once per step | use `[Pulse]` |
| .NET: wrong timeline plays | ids follow name order and a timeline was added | keep ids in one place |
| Two playbacks of the same timeline after a world rewind | rewind can step back into the previous timeline in a **Next** chain | Stop all, then replay |
| Authoring component ignored | class name differs from its file name, or it is in an Editor assembly | one class per file, runtime assembly (chapter 9.2) |
| Edits do not show up | the bake is stale | Reimport the SubScene (chapter 9.7) |
| Tests fail only on the first cold run | Burst has not compiled yet | run the tests again and judge the second run |

---

## 13. Known limits and open risks

Honest list. Some of these are design decisions still to be made.

- **No track data.** `ITlTrack` fixes the track settings to "none". Values live on the clip, and so does blending (`IBlend` on the clip, 7.3). (The older general `ITrack<TTrack, TClip>` still exists in `Tl.Core`; its Unity support is not covered here.)
- **Blending is linear.** The factor is a straight line across the overlap (0.5 for a one-tick overlap); Timeline's ease curves are not read. Pulses on one track treat overlapping clips as one long clip (7.3).
- **No Unity track bindings.** A clip cannot hold a reference to a scene object. Put the reference on your own authoring component and bake it (the Events demo does).
- **Names are not fully consistent yet.** Examples: both `TlKindAttribute` and `TimelineKindAttribute` exist; `TlClip`, `TlKind` have the `Tl` prefix but `Pulse`, `Feeds`, `ClipEvent`, `KindMode`, `Events<T>` do not; `Playback` exists in both `Tl` and `Tl.Unity`; scene JSON says `"states"` where the code says `[TlState]` and the hash is `TlStateHash`; Unity's Wrap Mode is ignored while Tl's own setting hides in a marker. A naming review is planned; expect renames before 1.0 with no compatibility aliases.
- **Per-tick sums come back one tick short** on rewind (chapter 10). Pulses are exact.
- **Kind `Order` across different groups or assemblies, and `Order` in Unity,** are not covered by tests here.
- **Version:** preview. Tested on Unity 7000.0.0a16, Entities 7.0.0, URP, Windows. The EditMode and PlayMode suites also run on Linux in CI (the runner is described in `docs/CI-RUNNER.md`: who hosts it and how to restart it). The Linux and macOS players and the macOS editor have not been run. IL2CPP and CoreCLR Windows players were run for the scenes and sample listed in chapter 11.
- **Install** is by git URL (`#main` moves). No tagged release exists yet.
- **Floats:** the Motion pack's float results matched bit for bit between .NET and Burst in tests. Other CPUs and compilers are untested.

---

## 14. Having fun: things to build

You now know all the pieces. These are projects that use them. Each says which pattern it needs, so you can build them yourself. Code here is **not run** unless it says otherwise; it is a sketch built from the parts you have already seen.

### 14.1 A door, an elevator, a platform

**Pattern B** (a value over time) plus **Reverse**. A clip feeds a position offset; pressing a button starts the timeline, pressing it again reverses it:

```csharp
requests.Reverse(doorTimeline, true);     // closes the door by running the same clip backward
```

Why it is fun: the designer shapes the motion with a curve in the Inspector, and you wrote the door once. Remember `frame.Direction` so the close really retraces the open (chapter 10).

### 14.2 Buffs and debuffs that stack

**Pattern C** with separate tracks. Haste, poison, shield: each is a clip, the designer drops them on **different tracks** to stack, and `Combine` adds them. The Stats pack does this, including a buff that gives a bonus at clip start and takes it back at clip end (two pulses, so it also survives a rewind).

### 14.3 A time machine (hold a key to rewind the world)

This is Tl's best trick. The world has one switch for direction: the `TimelineStep` singleton. Flip it and every timeline in the world runs backward.

```csharp
using Tl.Unity;
using Unity.Entities;
using UnityEngine;

public sealed class TimeMachine : MonoBehaviour
{
    EntityQuery _step;

    void Update()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;
        if (_step == default) _step = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<TimelineStep>());
        if (_step.CalculateEntityCount() == 1)
            _step.SetSingleton(new TimelineStep { Forward = !Input.GetKey(KeyCode.R) });   // hold R to rewind
    }
}
```

Not run as written. The Tl Panel's "World: rewinding" button does the same thing with the same singleton, and the Force and Inventory demos rewind with it.

**The design lesson (important):** only what Tl can take back comes back (chapter 10). If you want a Braid-style time machine, build **all gameplay state from pulses and signed sums** (health, inventory, door positions) and the whole game rewinds exactly. Anything you set by hand in your own system stays where you left it.

### 14.4 Bullet time in a zone

Speed can be set **per channel**. Put the enemies on one channel and slow only them:

```csharp
requests.SetChannelSpeed(enemyChannel, 4);     // 4 sixteenths = 0.25x, only the enemies' timelines slow down
requests.SetChannelSpeed(enemyChannel, 16);    // back to normal
```

Kinds and pulses stay exact at every speed (chapter 10), so a slowed enemy still hits for the same total, just later.

### 14.5 A boss fight as a script the designer owns

One timeline per phase, chained with **Play next**. A pulse clip at the start of each phase spawns a wave, a feeds clip drives an attack pattern, a marker is the "now he is vulnerable" beat. The designer re-times the whole fight in the Timeline window and you never open the code. (The Events demo's strikes, sparks and the dummy that flinches are this shape in miniature.)

### 14.6 A rhythm game

Beats are **markers** on a timeline (chapter 8.5). A marker's pulse asks your game "was the player's tap near this beat?". Changing the song speed is `SetSpeed`, so tempo is a slider. Replays are free: record the taps, replay them, and compare the **state hash** to prove it matched.

### 14.7 Kill-cam and lockstep replays

Tl is deterministic: the same requests on the same steps give the same hash, in Unity and in a plain .NET program. So a replay file can be just "the requests and the step they happened at". A server can re-run it and compare hashes to detect cheating, or a kill-cam can replay the last 5 seconds by running the world backward then forward again.

### 14.8 Ship a tiny game

A good final exercise: a one-screen game with **one rule per feeling**.

- A **dash** (pulse teleport, 4 ticks).
- A **poison cloud** that drains health (feeds) and a **potion** that heals (pulse): one `Both` kind.
- A **hold-R time machine**.
- A **slow zone** (`SetChannelSpeed`).

Build each as its own timeline and kind, keep every value in a column, and check that holding R takes you cleanly back. If anything does not come back, it is a state you set by hand: find it and turn it into a pulse.

---

## 15. Cheat sheet and exercises

### Cheat sheet

```
Clip    [TlClip("A/B")]                 the numbers the designer types
Track   ITlTrack<Clip>                  turns one clip into a value
          [Feeds(typeof(Kind))]           every tick the clip is active
          [Pulse(typeof(Kind), On = ClipEvent.Start | End | Marker)]   once at that point
Kind    [TlKind(Accepts = Spans|Pulses|Both, Mode = Combine|Each, Order = n)]
          Combine(ref T total, in T next)       merge (only if values merge)
          Apply(in T total, ref Column col)     write the result
Column  [TlState] partial struct in a namespace, starts at 0, added only where a kind reaches it
Order   per tick: tracks, then kinds (by Order), then your systems (OrderLast)
Rewind  multiply signed values by frame.Direction; pulses come back exactly
Stack   clips on different tracks or timelines: Combine adds them
Blend   clips overlapping on ONE track: MyClip : IBlend<MyClip> crossfades, else the first counts
Start   Play On Awake starts the Playable; Also Plays rows ticked Play On Awake start with it
Control requests.Start/Stop/Pause/Resume/Seek/Reverse/SetSpeed(id or TlTimeline), Reset(); another entity: BufferLookup<TimelineRequest>
Bake    own starting values: Baker<YourAuthoring> + AddComponent(...); Tl keeps what you added
```

### Exercises (in order; each builds on the last)

1. **Predict.** In Hello Tl, set the second clip's `ComeBack` off. What is x after tick 120? (13.)
2. **Add a clip.** Write `FadeClip { float Amount }` that feeds a kind `Brightness` writing a `Light` column, so a light fades in over the clip. (Pattern B.)
3. **Stack it.** Put two Fade clips on two tracks overlapping. What is the brightness during the overlap? (The sum.) Put them on one track. What now? (Only the first, until it ends.) Now give `FadeClip` an `IBlend<FadeClip>` that lerps `Amount`. What now? (A crossfade from the first to the second, 7.3.)
4. **Health.** Build chapter 6. Predict the health at tick 100 before you run it.
5. **Order.** Make a second kind that reads the first kind's column, then swap `Order` and watch it change by one tick (7.4).
6. **Rewind.** Press **World rewind** in the Panel on the Inventory demo, then on your own timeline. Then remove `frame.Direction` from your track and watch what changes.
7. **Take it out of Unity.** Export the library and run the same timeline in a console program with the code from chapter 11. Check the numbers match.
8. **Prove it.** Write a test that runs 600 steps and asserts the state hash. Run it on both hosts.
9. **Bake it.** Write the `PlayerAuthoring` and `PlayerBaker` from chapter 9, give the player 100 health in the Inspector, and check at runtime that `Health.Value` is 100, not 0. Then delete your baker and see it become 0.
10. **Have fun.** Build the time machine and the slow zone from chapter 14, then ship the tiny game in 14.8.

If you can do 1 to 10 without looking back, you can build with Tl.

---

## 16. Install, demos, repository

**Install:** chapter 1. Or let the installer (IAFahim/tl) build a whole project with every demo and run each demo's test:

```powershell
$env:TL_PROJECT='C:\path\MyGame'; irm https://raw.githubusercontent.com/IAFahim/tl/main/install/unity.ps1 | iex
```

```sh
curl -sSL https://raw.githubusercontent.com/IAFahim/tl/main/install/unity.sh | bash -s -- <project-path>
```

(The bash installer has not been run on this version.)

**Demos** (Package Manager > Tl Unity Playback > Samples). Each has its own README with the first ten minutes:

| Demo | Shows |
|---|---|
| **Hello Tl** | this course's first example. Start here |
| Many At Once | chapter 7: two timelines and two tracks on one platform, and a crossfade on one track |
| Force | a push, "there and back", live edit, a follow camera |
| Events | strikes raising events into gameplay, a dummy that is hurt |
| Crowd | 10,000 and 100,000 entities, 0 bytes per frame |
| Motion, Stats, Inventory | the same ideas as packs: animation, health and buffs, a bag that rewinds to empty |
| Demo hub | one scene with a button per demo |

**Guides:** [docs/USING-ECS.md](docs/USING-ECS.md), [docs/UNITY-TIMELINE.md](docs/UNITY-TIMELINE.md), [docs/USING-PLAIN-CSHARP.md](docs/USING-PLAIN-CSHARP.md), [docs/KINDS.md](docs/KINDS.md), [docs/FORCE-DEMO-CHECK.md](docs/FORCE-DEMO-CHECK.md), [docs/COMPARISON.md](docs/COMPARISON.md); words in plain language: [Glossary](com.tl.unity/Documentation~/GLOSSARY.md); what comes back on rewind: [REWIND-AND-SPEED.md](com.tl.unity/Documentation~/REWIND-AND-SPEED.md); plans: [docs/design/](docs/design/).

**Repository**

- `com.tl.unity/`: the package (runtime, Timeline authoring, editor baker and preview, tests, samples).
- `external/tl-kinds`: the pinned IAFahim/tl commit the package's `Core/` and in-process packer are synced from (`eng/sync-tl-core.sh`, `eng/sync-tl-bake.sh`, `eng/build-generators.sh`, each with `--check`).
- `eng/test-com-tl-unity.sh`: runs the package's EditMode and PlayMode tests in a scratch project with the newest Unity 7 editor.
- Agents: read [AGENTS.md](AGENTS.md) first.

**Licence:** MIT. See [LICENSE](LICENSE).
