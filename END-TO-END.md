# tl end-to-end: from JSON baking to Unity ECS typed queries

Everything in Part A of this walkthrough is verified green on this machine today
(newest Unity 7 editor, Entities 6.7.0, tl.unity @ `6904a53`: `DirectQueryTests`,
23/23 EditMode). Part B is the standard-Entities integration shape and is
clearly marked where it is not yet receipted on this editor alpha.

You will: author a timeline as JSON, bake it to `.tlb` bytes with `tlbake`,
load it in Unity, let the coordinator advance entities, and consume frames from
your own system with the typed query:

```cs
foreach (var frame in Timeline.Query<DamageTrack, DamageClip>(in core))
    ApplyDamage.Execute(in frame, in resistance, ref health);
```

## 0. Prerequisites

- The newest Unity 7 editor (install it by name: `unity install 7`) with the Entities 6.7.0 package.
- .NET 10 SDK (for baking).
- Two local checkouts side by side:

```
~/GitHub/tl        the core repository (runtime sources + tlbake tool)
~/GitHub/tl.unity  the Unity package repository (this file's home)
```

- In your Unity project: install the package from the local path
  `tl.unity/com.iafahim.tl` (Package Manager → add from disk →
  `com.iafahim.tl/package.json`).

## 1. Shared domain code (one file, compiled by both hosts)

Create `Assets/Scripts/Timelines/CombatTimeline.cs` in the Unity project.
The namespace must be a single bare segment (schema v1 law: dotted namespaces
are diagnostics):

```cs
using Tl;

namespace Combat
{
    public readonly partial struct Resistance
    {
        public readonly float Scale;
        public Resistance(float scale) => Scale = scale;
    }

    public partial struct Health
    {
        public float Value;
    }

    public readonly struct DamageClip
    {
        public readonly float Amount;
        public DamageClip(float amount) => Amount = amount;
    }

    public readonly struct DamageTrack : IBlend<DamageClip>
    {
        public readonly float Multiplier;
        public DamageTrack(float multiplier) => Multiplier = multiplier;

        public void Blend(in DamageClip first, in DamageClip second, float factor, out DamageClip result)
            => result = new DamageClip(first.Amount + (second.Amount - first.Amount) * factor);
    }

    public readonly struct ApplyDamage : ITimelineJob<DamageTrack, DamageClip>
    {
        public static void Execute(
            in Frame<DamageTrack, DamageClip> frame,
            in Resistance resistance,
            ref Health health)
        {
            var amount = frame.Clip.Amount * frame.Track.Multiplier * resistance.Scale;
            health.Value += frame.IsBackward ? amount : -amount;
        }
    }
}
```

Unity-only file `Assets/Scripts/Timelines/CombatTimeline.Unity.cs` (the
`partial` hooks; skip for components that already implement `IComponentData`):

```cs
using Combat;
using Unity.Entities;

namespace Combat
{
    public readonly partial struct Resistance : IComponentData { }
    public partial struct Health : IComponentData { }
}
```

## 2. The bake shim (why: pair keys hash assembly-qualified type names)

Baked pair keys are hash-derived from
`"Combat.DamageTrack, <AssemblyName>, Version=..."`. At bake time the type
lives in your .NET shim DLL; at runtime it lives in a Unity assembly. The two
assembly names must match exactly. If the domain file compiles into
`Assembly-CSharp` (the default), the shim must be
`AssemblyName=Assembly-CSharp`, `AssemblyVersion=0.0.0.0`. If you put the
domain under a custom asmdef, use that asmdef's name instead.

`tools/TlBakeShim/TlBakeShim.csproj` in this repository is the working
reference. A user-facing copy (e.g. `~/GitHub/tl.unity/tools/MyBakeShim/`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Assembly-CSharp</AssemblyName>
    <AssemblyVersion>0.0.0.0</AssemblyVersion>
    <RootNamespace>Combat</RootNamespace>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../external/tl/Tl.Core/Tl.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="<path-to-unity-project>/Assets/Scripts/Timelines/CombatTimeline.cs" Link="CombatTimeline.cs" />
  </ItemGroup>
</Project>
```

`external/tl` in this repository is a snapshot of the `tl` sources; point the
`ProjectReference` at `~/GitHub/tl/src/Tl.Core/Tl.Core.csproj` if you prefer
the live tree.

Build the shim and the baker:

```sh
dotnet build -c Release ~/GitHub/tl.unity/tools/MyBakeShim
dotnet build -c Release ~/GitHub/tl/tools/Tl.Bake
```

## 3. Author and bake the timeline

`boss.json` — flat schema v1. Track/clip `namespace`+`type` name the C# types
above; `data` field names map onto struct fields; windows are half-open
`[start, end)`; execution order is authored clip order:

```json
{
  "name": "boss_phase_one",
  "duration": 8,
  "loop": true,
  "tracks": [
    {
      "name": "main_damage",
      "namespace": "Combat",
      "type": "DamageTrack",
      "data": { "Multiplier": 2.0 },
      "clips": [
        { "namespace": "Combat", "type": "DamageClip", "start": 0, "end": 3, "data": { "Amount": 5 } },
        { "namespace": "Combat", "type": "DamageClip", "start": 4, "end": 8, "data": { "Amount": 9 } }
      ]
    }
  ]
}
```

Bake (deterministic; cache hits preserve timestamps; `--report` prints sizes):

```sh
dotnet run --project ~/GitHub/tl/tools/Tl.Bake -c Release -- \
  boss.json \
  ~/MyUnityProject/Assets/Resources/TlTimelines/boss.bytes \
  --assembly ~/GitHub/tl.unity/tools/MyBakeShim/bin/Release/net10.0/Assembly-CSharp.dll \
  --cache ~/.tlbcache
```

Unity imports `.bytes` files as `TextAsset`, so
`Resources.Load<TextAsset>("TlTimelines/boss")` gives you the baked bytes.

## 4. Unity runtime — coordinator, clock, rows

The runtime package gives you (all receipted):

- `Tl.Unity.TimelineComponent : IComponentData` — `{ TimelineRef Reference; uint Position; long Cycle; }`,
  layout-identical to the core row.
- `TimelineClock : IComponentData` — `{ uint GameTick; int Delta; }` singleton.
- `TimelineEcs.CreateGroup(world)` — managed coordinator.
- `TimelineBurst.CreateGroup(world)` — Bursted coordinator (same semantics).
- `TimelineAsset.Load(bytes)` — cold validated import; dispose after all rows
  and readers are done.

This is the exact verified harness shape (from `DirectQueryTests`), runnable as
an EditMode test today:

```cs
using Combat;
using Tl.Unity;
using Unity.Entities;

// IMPORTANT (verified the hard way): systems that use SystemAPI/generated
// code must NOT live inside a Tl.* namespace — the enclosing namespace Tl
// shadows Unity.* in generated code. Use a top-level, block-scoped namespace.
namespace GameTimelines
{
    // In a normal project, drop [DisableAutoCreation]: Unity auto-creates the
    // system into the default world and updates it in the player loop. It is
    // only needed when you create and update the system manually yourself.
    public partial struct DamageQuerySystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (timeline, resistance, health) in
                     SystemAPI.Query<RefRO<TimelineComponent>, RefRO<Resistance>, RefRW<Health>>())
            {
                foreach (var frame in TimelineEcs.Query<DamageTrack, DamageClip>(in timeline.ValueRO))
                {
                    var r = resistance.ValueRO;
                    var h = health.ValueRW;
                    ApplyDamage.Execute(in frame, in r, ref h);
                    health.ValueRW = h;
                }
            }
        }
    }
}
```

`TimelineEcs.Query<TTrack, TClip>(in row)` is the Unity-facing entry: it
reinterprets the ECS component to the layout-identical core row inside the
package, so user code never touches `Unsafe.As` or the two-type split.

Bootstrap and drive (EditMode / test harness form):

```cs
var world = new World("Game");
var group = TimelineEcs.CreateGroup(world);          // or TimelineBurst.CreateGroup
var probe = world.CreateSystem<GameTimelines.DamageQuerySystem>();
var clockEntity = world.EntityManager.CreateEntity(typeof(TimelineClock));

var bytes = Resources.Load<TextAsset>("TlTimelines/boss").bytes;
var asset = TimelineAsset.Load(bytes);                 // keep alive; dispose last

var entity = world.EntityManager.CreateEntity<UnityRow, Resistance, Health>();
world.EntityManager.SetComponentData(entity, new UnityRow { Reference = asset.Reference });
world.EntityManager.SetComponentData(entity, new Resistance(1f));
world.EntityManager.SetComponentData(entity, new Health { Value = 100f });

// per game step:
world.EntityManager.SetComponentData(clockEntity,
    new TimelineClock { GameTick = gameTick, Delta = 1 });   // +1 forward, -1 backward
group.Update();
probe.Update(world.Unmanaged);   // consume the freshly committed stage
```

## 5. What the query gives you (receipted semantics)

For each row, `Timeline.Query<TTrack,TClip>` enumerates the `<TTrack,TClip>`
occurrences of the row's currently selected stage:

- It is a read-only stage view: it never advances `Position`/`Cycle`; repeated
  queries return identical frames (verified twice-in-a-row with the row
  untouched).
- A gap or clamped-completed position yields no frames (verified: fixture
  stages `[0,1) 11`, `[1,2)` gap, `[2,3) 22`).
- Reverse movement re-observes the same stages in reverse (verified).
- Multiple tracks of the same pair in one step all appear, authored order.
- In this view `Track`, `Clip`, `TimelineTick`, `Cycle`, `TrackIndex` are
  populated; `GameTick` and `Flags` are not (current behavior — direction
  flags belong to the execution path).
- Movement math: `Tick(G, +N)` emits game ticks `G..G+N-1`; `Tick(G, -N)`
  emits `G-1..G-N`; new instances start at local 0; non-looping assets clamp
  at duration; loops carry independent per-instance cycles.

One eligibility rule to know: if the project ALSO registers function-pointer
consumers with mandatory columns (the in-tick effect lane, below), a row whose
archetype lacks a mandatory component is excluded as a total no-op for that
tick — no effects, no movement. With no registered consumers (the pure
direct-query project above), every `TimelineComponent` row ticks.

## 6. Alternative: effects inside the tick (function-pointer consumers)

If you want effects applied by the coordinator itself (inside the tick, with
Burst), register consumers and declare their columns instead of — never in
addition to — a direct-query system for the same pair:

```cs
public static unsafe class DamageConsumers
{
    static DamageConsumers() =>
        PairRuntime<DamageTrack, DamageClip>.Consume(&Execute, &Bind);

    static void Bind(ulong* keys, int keyCount, byte* table)
    {
        var health = TimelineEcs.ComponentKey<Health>();
        for (var i = 0; i < keyCount; i++)
            if (keys[i] == health) { table[0] = (byte)(i + 1); break; }
    }

    static void Execute(byte* slot, uint gameTick, uint tick, long cycle,
                        FrameFlags flags, void** columns, int row)
    {
        DamageClip scratch = default;
        var frame = TickFrame.ToFrame<DamageTrack, DamageClip>(
            slot, gameTick, tick, cycle, flags, ref scratch);
        var health = (Health*)columns[0];
        health[row].Value -= frame.Clip.Amount * frame.Track.Multiplier;
    }
}

// once, before the first coordinator update, alongside:
TimelineEcs.Column<Resistance>();
TimelineEcs.EnableableColumn<Health>();
```

Touch the consumer class once at startup so its static constructor installs
the chain (installs are once per process). This lane is fully receipted
(oracle, eligibility, IJobEntity parity, delayed jobs; see the repository
README). The `.NET` host uses the same shared operation via generated
bindings — no manual registration there.

## 7. The .NET equivalent (same bytes, no Unity)

```cs
using var asset = TimelineAsset.Load(File.ReadAllBytes("boss.tlb"));
var rows    = new[] { new TimelineComponent(asset.Reference) };
var resist  = new[] { new Resistance(1f) };
var health  = new[] { new Health { Value = 100f } };
var query   = Timeline.Rows(rows).Read(resist).Write(health);
query.Tick(gameTick: 200_000u, delta: 1);   // consumers dispatch, movement commits
```

## 8. Honest boundaries (today)

- The Unity package installs from a local path only — publication and license
  are open owner decisions (issue #64).
- Part B production integration (default world, `[UpdateInGroup(typeof(SimulationSystemGroup))]`
  + `[UpdateAfter(typeof(TimelineSystemGroup))]` consumer systems driven by
  the player loop) is standard Entities usage but not yet receipted on this
  editor alpha; the receipted form is the explicit `group.Update()` /
  `probe.Update(world.Unmanaged)` sequence shown above. On this alpha, keep
  `SystemBase` classes free of SystemAPI-rewritten constructs (editor
  binary/source drift, CS0115); `ISystem` systems like the one above are fine.
- PlayMode, standalone players, and IL2CPP qualification are pending.
- There is no designer-facing authoring GameObject/Baker yet — assets are JSON
  + bytes today; `tlbake --report` audits sizes, `--strip` trims metadata for
  distribution.
- `Timeline.Query` frames do not carry `GameTick`/`Flags` in the current view.
