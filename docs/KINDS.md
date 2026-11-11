# Tl kinds in Unity (`com.tl.unity`)

How game-defined per-step work runs on the Unity host: the kind model, the step order, sizing, the Kind Report and presentation events.
Authoring (Timeline window, director, clips) is in [USING-ECS.md](USING-ECS.md) and [UNITY-TIMELINE.md](UNITY-TIMELINE.md).

## Kind model

A kind is a value type plus a `Combine` rule (or `Each`) plus an `Apply`
method. A clip feeds a kind through a `[Feeds]` method evaluated every step
while the clip is active; a `[Pulse]` method fires a one-step value at clip
start, clip end, or a marker. Game code sends one-step values through
`KindPulses<K>.To(entity, value)`. The test game (`Tests/Kinds/Game`) defines:

| Kind | Value | Combine | Apply target | Fed / pulsed by |
|---|---|---|---|---|
| Locomotion | `Speed` (`Value`, `Priority`) | highest priority wins | `Motion.X` | `Walk.Evaluate` (reads `WalkStat`, `Target`, `AnchorLookup`) |
| Lift | `float` | sum | `Motion.Y` | `Jump` fold + `Evaluate` (reads `JumpPower`) |
| Damage | `int` | sum | `Health` | `Hit` start pulse; game code via `KindPulses<int>.To` |
| StatChange | `int` | each applied once | `Stamina` | `ChangeStat` start pulse, `Steps` marker pulse |

`Locomotion` shows overriding by priority (a faster high-priority entry
shadows the walk until its clip ends). `Lift` shows stacking (jump plus two
potion hops sum into one value per step). `Damage` shows game-code pulses
summed with timeline hit pulses into one `Health` write per step. `StatChange`
shows `Each` mode: every pulse applies exactly once, in a fixed merge order.
`Whistle` raises a `Cue` presentation event (see Presentation events).

A kind is named by its type name (`Damage`). New code writes `[TlKind]`; the test game keeps the older `[TimelineKind]` and
`[TimelineSystem]` spelling, which still works.

## Step order

Each step runs `TimelineKindTickSystem` first, then the kind loops inside each
system (`Movement` runs Locomotion, Lift, StatChange; `Vitals` runs Damage):

1. `Tick`: apply start/stop requests, advance playback ticks, maintain span
   entries (span start/end from boundaries, marker pulses), merge queued game
   pulses in a deterministic order, handle end/next/hold/finish, maintain the
   `Active`/`Enabled` row state.
2. Kind loops: one loop per kind per chunk over that kind's entry buffer.
   Rows whose kind row is disabled (`Enabled` off, or `Active` off) contribute
   nothing but keep their entries.
3. `Apply`: each kind writes its owner's data (`Motion`, `Health`, `Stamina`).

Every Tl system on the step path (`TimelineRequestSystem`,
`TimelineAdvanceSystem`, `TimelineKindTickSystem`, `TimelineEventSystem`) is a
Burst `ISystem`. Everything a step needs is in unmanaged singletons:
`TimelineLibrary` (`View`, the `KindIndex` in `Kinds`, and `Generation`, which
changes on a hot reload) and `TimelineStep` (`Forward`, the step direction;
set it with `SystemAPI.SetSingleton`). Your kind systems run after the Tick,
also as Burst `ISystem`s:

```csharp
[BurstCompile]
[UpdateInGroup(typeof(TimelineSimulationGroup))]
[UpdateAfter(typeof(TimelineKindTickSystem))]
public partial struct VitalsSystem : ISystem
{
    TimelineJobs.Vitals _jobs;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<TimelineLibrary>();
        state.RequireForUpdate<TimelineStep>();
        _jobs = TimelineJobs.Vitals.Create(ref state);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var library = SystemAPI.GetSingleton<TimelineLibrary>();
        var step = SystemAPI.GetSingleton<TimelineStep>();
        state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
    }
}
```

`Tests/Kinds/Harness/KindWorld.cs` has the full `Movement` and `Vitals` pair.

An entity that carries `TimelinePlaying` belongs to the kind Tick.
`TimelineRequestSystem` and `TimelineAdvanceSystem` skip it. The director
baker adds `TimelinePlaying` (disabled); the Tick enables it when it applies a
request. With `[TlKind]` you do not write these systems: the generator writes
one Burst `TlKindSystem<...>` per assembly for every kind whose `Apply` writes
your own `IComponentData`.

Send game pulses before the step; they merge during that step's Tick. Take
the `TimelineKindTick` singleton (`SystemAPI.GetSingleton<TimelineKindTick>()`,
it lives on the Tick system's entity), call `BeginPulses(n)`, write with
`DamageKind.Pulses(sender.Writer).To(target, value)` (from Burst code or a
job; pass the job's handle to `AddPulseWriter`). Pulses to an entity without
the kind are counted in `DroppedPulses` and dropped, as are starts for
unreachable timelines (`DroppedStarts`), both on `TimelineKindTick`; reading
either completes the Tick's last jobs first, so read them on the main thread. A count
above zero usually means the object never plays a timeline that feeds the kind:
declare `[TlKind(GamePulses = n)]` and let one of its timelines reach it, or add
the kind's entry buffer in an `ITlBaker`.

## Sizing

- Kind sets: the director baker adds a kind's entry buffer (and the components its `Apply` writes) only when a timeline of the object
  reaches it, so objects that play different timelines land in different archetypes with nothing unused.
- Inline capacity: each kind's entry buffer keeps `128 / entry bytes` entries inside the chunk; `[TlKind(Inline = n)]` sets it. More
  entries than that move to the heap: still correct, only slower. The Kind Report shows where that happens. A smaller inline capacity
  that still holds the usual entry count makes rows smaller and chunks fuller: 4 instead of 10 for a kind with at most 4 entries cut a
  4M-row tick by 14 % and 370 MB.
- Game pulses: `[TlKind(GamePulses = n)]` reserves room for n pulses from game code per step.
- None of this is generated into a file and editing a timeline never recompiles.

## Horizon glide

The kind tick skips per-row rule work between kind edges. Each row keeps one byte, `TimelineHorizon.Steps`: how many coming ticks
are proven ordinary (the tick moves by one, no kind event bit fires, no request is pending). While it is above zero the tick job only
moves the tick; at zero it runs the full rules and computes the next horizon from the library's kind calm tables (`KindIndex.Calm`,
built once per library bind). Results are byte-identical to the full rules on every tick.

- On by default: baked directors (`TlDirectorBaker`) and rows spawned by `TlSceneRun` get `TimelineHorizon` in the same archetype.
- Entities you create in code glide only when `TimelineHorizon` is in the same type set as `Playback`, `TimelineRequest` and
  `TimelinePlaying`; without it the row takes the full rules every tick (still correct).
- Limit: `TimelineGlide { Limit }` sits on the `TimelineKindTickSystem` system entity, default 255. `Limit = 0` turns glide off for the
  world: `EntityManager.SetComponentData(world.GetExistingSystem<TimelineKindTickSystem>(), new TimelineGlide { Limit = 0 })`.
- Invalidation: a chunk's horizons are cleared when its `Playback` buffer changed since the system's last update
  (`ArchetypeChunk.DidChange`), and for the whole world on a library rebind, a step-direction change or a limit change. Requests
  through `TimelineRequest`/`TlRequests` and any RW access to `Playback` (`GetBuffer<Playback>`, an RW `BufferTypeHandle`, `RefRW`)
  bump the change version, so they are safe.
- Caveat: writing `Playback` bytes through a raw pointer kept from an earlier frame does not bump the change version; such a writer
  must take RW access in the frame it writes, or set the row's `TimelineHorizon.Steps` to 0.
- Memory: +1 B per row in the chunk, plus the calm tables: one forward and one backward byte per library timeline tick (6 MB for
  1000 timelines x 3000 ticks) and one start index per timeline, built at bind and freed with the library.
- `TimelineKindTick.ManagedRuns` and `KindCounter.ManagedRuns` count managed (non-Burst) executions of the tick job; 0 in a warm
  Burst world.
- Measured (Unity 7000.0.0a16, Burst, 11 workers, safety checks off, median of 5 processes, 1000 timelines with one kind each):
  4M rows x 4 timelines 38.2 -> 31.4 ms per tick (tick job 14.0 -> 9.9 ms, kind jobs 24.9 -> 22.0 ms with `StepInWindow`), 10k rows x
  150 timelines 2.87 -> 1.94 ms. The tick job stays near 10 ms at 4M because it still reads and writes every row's `Playback`.

## Kind Report

`Tools > Tl > Kind Report` logs what the kinds cost in the entities that
exist. To use it:

1. Open a scene whose SubScene has a Playable Director with Tl tracks, or enter Play mode.
2. Run the menu item.
3. Read the Console. There is one block per world that has entities with
   kinds. A block that has warnings is logged as a warning.

Each row is one archetype that carries at least one kind:

```
<kinds>: <entities> entities in <chunks> chunks of <chunk capacity>, <n> kinds, <bytes> B kind storage per entity
```

`<kinds>` are the kind names joined with `+`. Kind storage is the size the
kind entry buffers take in the chunk, per entity. Compare `entities` with
`chunks` times `chunk capacity`: many mostly empty chunks mean too many
archetypes.

Three warnings:

- More than 32 kinds on one archetype. Give the entity fewer kinds, or split
  the entity.
- More than 1024 B of kind storage per entity. Lower `[TlKind(Inline = n)]`,
  or give rare kinds `Inline = 0`.
- Fewer than 16 entities per chunk. Per-entity storage is large; fix it as
  for the 1024 B warning.

With no entities that carry kinds, the menu logs: no world has entities with
kinds.

The report logic is in the runtime assembly, so game code can call it:
`KindReport.Capture(entityManager, KindRegistry.Discover())` returns the
rows, `KindReport.Warnings(rows)` the warning strings, and
`KindReport.Text(worldName, rows)` the text above. The limits are
`KindReport.MaxKinds`, `MaxKindBytes` and `MinChunkCapacity`.

## Presentation events (`Events<T>`)

A consumer that raises presentation events declares `OnClipStart`,
`OnClipEnd` or `ExecuteActive` with a Frame and `ref Events<T>` parameters
only, and its system declares an `Events<T>` field (the test game's
`Whistle` and `Movement.Cues`):

```csharp
public struct Whistle : ITrack<CueTrack, CueClip>
{
    public static void OnClipStart(in Frame<CueTrack, CueClip> f, ref Events<Cue> cues)
    {
        if (f.Direction > 0)
            cues.Raise(new Cue { Voice = f.Clip.Voice });
    }
}
```

On Unity the generated kind job of that system dispatches these methods in
Burst, with the same firing rules as the .NET host (`OnClipStart` on the
clip's first tick, `OnClipEnd` on its last tick and when the timeline is
stopped inside the clip, `ExecuteActive` on every tick inside it). Each
owner entity gets a `DynamicBuffer<TimelineEvent<T>>` (`Tl.Unity`); the
director baker adds one for every event type the game declares. Every
step clears the buffer and refills it with the events that entity raised in
that step, ordered by playback slot, then by raise order (the order of the
.NET host's `Events<T>.Drain`). `TimelineEvent<T>.Record` holds `Slot`,
`Sequence` and `Value`; `Record.Entity` is not used on Unity. Read the buffer
in a system that runs after `TimelineSimulationGroup`.

The job writes the events straight into the owner's buffer: `Events<T>` is
built over the buffer's memory (`Events<T>(header, items, capacity)`), and a
step that raises more events than the buffer holds grows the buffer and runs
that entity's dispatch again, so no event is lost and the result is the same.
The system's dispatch only runs for chunks that carry the buffer.

The test `EventScriptRaisesTheSameCuesAsThePlainHost` plays a 600-step
script that starts and stops `whistle` among the other timelines and hashes
every entity's cues per step: `6607956939547305625`, equal to the plain C#
host (the .NET `Tl.Core` host) running the same script on the plain sample.
That script exercises `OnClipStart`; the `OnClipEnd` and `ExecuteActive`
paths follow the same rules but have no Unity test yet.
