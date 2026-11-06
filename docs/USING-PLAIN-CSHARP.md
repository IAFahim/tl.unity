# Using Tl from plain C# (MonoBehaviours, no ECS)

Play Tl timelines from a `MonoBehaviour`. No entities, no SubScene, no Burst.
The .NET host of Tl runs inside Unity as it is.

## Verdict

It works. Tested on the newest Unity 7 editor (tested on 7000.0.0a16):

- From an empty project: package from the git URL, import the sample, 0
  `error CS`, `tlb library` gives `game.bytes` byte for byte equal to the shipped
  one, a scene with `PlainRunner` moves a Transform in Play mode, and the
  600-step script hashes to the pinned value (about 10 minutes).

- Editor PlayMode: the 600-step kind script hashes to `13943614770702343869`,
  the same hash as the ECS host and the .NET `KindUnityHashTests`.
- A Windows CoreCLR player and a Windows IL2CPP player (scratch builds, not
  committed) print the same hash and step a Transform from `FixedUpdate`.
- Not verified: other platforms, and Burst (the plain host does not use it).

The fastest start is the sample (step 1). It is the complete working code. The
code blocks in steps 2 to 6 are excerpts of its files, not a full program: for
your own game, copy the sample files and change them.

What ships in `com.tl.unity` is enough for the runtime: the `Tl.Core`
assembly (`Core/`, netstandard2.1, no Unity dependency). It is not enough
alone. Your assembly also needs generated game code (below).

## What you need

| Piece | Where it comes from |
|---|---|
| `Tl.Core` (`TimelineWorld`, `PlaybackTable`, `KindStore`, `Events<T>`) | the `com.tl.unity` package |
| Game types: tracks, clips, kinds, consumers, systems | your code |
| `Run` for each `[TimelineSystem]` | the package's plain source generator, at compile time |
| `Timelines.g.cs` (ids) and `game.tlib` (the baked library) | `tlb library` |
| A driver `MonoBehaviour` | your code (the sample has one) |

The package still lists `com.unity.entities` as a dependency. The plain host
does not use it.

The `Run` code comes from `Tl.Gen.Plain`, a Roslyn source generator that
ships prebuilt in the package (`Generators/Plain/Tl.Gen.Plain.dll`). It wraps
the system emitter of `Tl.Gen.CSharp`. Unity runs it on every compile of an
assembly whose asmdef references `Tl.Unity.Generate.Plain` (Step 4). Nothing
is checked in and no tool is run by hand.

## Step 1: install and import the sample

`Packages/manifest.json`:

```json
"com.tl.unity": "https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main"
```

Package Manager, `Tl Unity Playback`, Samples, import `Plain C# host`. It
lands in `Assets/Samples/Tl Unity Playback/<version>/Plain C# host`. The sample
assembly is `Tl.Unity.PlainCSharp` and it references `Tl.Core` and
`Tl.Unity.Generate.Plain`. Unity compiles it at once (0 errors). The sample
already contains a baked `game.bytes`, so you can go to step 6 and press Play
before you bake anything.

Files in the sample:

| File | Role |
|---|---|
| `PlainGame.cs` | track, clip and kind types, consumers, two systems |
| `PlainHost.cs` | owns all native memory; `Step`, `Start`, `Stop`, events |
| `PlainRunner.cs` | the `MonoBehaviour` |
| `PlainScript.cs` | the 600-step script and its hash |
| `Generated/` | timeline ids (`Timelines.g.cs`) |
| `game.bytes` | the baked library (`game.tlib` renamed) |
| `Timelines~/` | timeline JSON sources (hidden from Unity) |

Unity imports a `.bytes` file as a `TextAsset`. It does not import `.tlib`.
Rename `game.tlib` to `game.bytes`, or read the file yourself from
`StreamingAssets`.

## Step 2: define the game

A clip is a data struct. A track holds settings and a blend. A consumer reads
a frame and feeds a kind. A kind combines the values of all active entries and
applies the result to your data. All types are unmanaged.

```csharp
public struct Motion { public float X, Y; }
public struct Health { public int Value; }
public struct Speed { public float Value; public byte Priority; }
public struct Cue { public int Voice; }

public struct WalkClip { public float Speed; public byte Priority; }
public struct WalkTrack : IBlend<WalkClip>
{
    public float Scale;
    public void Blend(in WalkClip first, in WalkClip second, float factor, out WalkClip result) => result = first;
}
```

```csharp
[TimelineKind]
public struct Locomotion
{
    public static void Combine(ref Speed total, in Speed next) { if (next.Priority >= total.Priority) total = next; }
    public static void Apply(in Speed total, ref Motion motion) => motion.X += total.Value;
}

[TimelineKind(Accepts = KindEntries.Pulses)]
public struct Damage
{
    public static void Combine(ref int total, in int next) => total += next;
    public static void Apply(in int total, ref Health health) => health.Value -= total;
}
```

```csharp
public struct Walk : ITrack<WalkTrack, WalkClip>
{
    [Feeds(typeof(Locomotion))]
    public static Speed Evaluate(in Frame<WalkTrack, WalkClip> f, in WalkStat stat)
        => new Speed { Value = f.Direction * f.Clip.Speed * f.Track.Scale * stat.Value, Priority = f.Clip.Priority };
}
```

A consumer that raises an event takes an `Events<T>`:

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

A system lists the columns and context the consumers may touch. The host
passes real arrays for them:

```csharp
[TimelineSystem]
public ref partial struct Movement
{
    public Span<Motion> Motion;
    public ReadOnlySpan<WalkStat> WalkStat;
    public ReadOnlySpan<JumpPower> JumpPower;
    public Span<Stamina> Stamina;
    public Events<Cue> Cues;
}

[TimelineSystem]
public ref partial struct Vitals
{
    public Span<Health> Health;
}
```

The full model of kinds (combine rules, pulses, step order) is in
[KINDS.md](KINDS.md). The plain host follows the same rules.

## Step 3: author and bake the library

One JSON file per timeline, one `library.json`. `Timelines~/run.json` in the
sample is a looping walk. Bake against the compiled assembly. The assembly must
already compile with the reference from step 4.

Unity compiles your game assembly to `Library/ScriptAssemblies/`. Copy
`Tl.Unity.PlainCSharp.dll` and `Tl.Core.dll` into one scratch folder outside
`Assets` and bake from the project folder. `<sample>` is the folder from step 1:

```sh
dotnet <tl>/tools/Tl.Bake/bin/Release/net10.0/Tl.Bake.dll library "<sample>/Timelines~" --assembly <scratch>/Tl.Unity.PlainCSharp.dll --out <scratch>/game.tlib --ids <scratch>/Timelines.g.cs --namespace PlainGame
```

Build `tlb` first (see the last paragraph of this step). `tlb` also writes
`Kinds.g.cs` next to `--out`. That file is for the ECS host. Delete it, or keep
`--out` outside `Assets` as above.

Copy `game.tlib` over `<sample>/game.bytes`, and `Timelines.g.cs` over
`<sample>/Generated/Timelines.g.cs` if you added or renamed timelines.

Baking is deterministic for one assembly name. The library stores the assembly name, so the bytes change if you rename
the asmdef.

Bake against the compiled game assembly, not against the generated code. A
brand new game has a chicken-and-egg step: compile the types first (leave
`PlainHost` out), bake, then add the rest.

`tlb` is the `Tl.Bake` tool from IAFahim/tl. You need the .NET 10 SDK:
`git clone --depth 1 https://github.com/IAFahim/tl.git`, then `dotnet build
tools/Tl.Bake -c Release` in the clone. Use a build from `main` (the
`tl.bake` 2.0.1 package on NuGet has no `kinds` command). It is
`tools/Tl.Bake/bin/Release/net10.0/Tl.Bake.dll`.

## Step 4: generate the systems

Add `Tl.Unity.Generate.Plain` to the references of your game asmdef:

```json
"references": [
  "Tl.Core",
  "Tl.Unity.Generate.Plain"
]
```

That reference opts the assembly in. The generator in the same package
folder as `Tl.Unity.Generate.Plain` runs only for assemblies that reference
it, so other assemblies (the package's own, your other game assemblies) are
not touched. On each compile it writes two sources into the assembly:

- `TlSystems<Assembly>.g.cs`: `Run` for each `[TimelineSystem]` and the module
  class `TlSystems.TlModule_<Assembly>` (`<Assembly>` is the assembly name with
  every character that is not a letter or digit replaced by `_`).
- `TlSystems.Module.g.cs`: `TlSystems.TlModule.Install()`, which installs that
  module.

Errors in the declarations (`TLGEN...`) appear in the Console like any
compile error. Rebake the library whenever a track, clip, kind, consumer or
system changes: the pair keys hash the type names and the assembly name, and
a library baked against other types fails on load with a rebake hint.

Do not also reference `Tl.Unity.Generate` (the ECS generator) from the same
assembly: the plain host and the ECS host generate different code for the
same `[TimelineSystem]`.

Unity does not define `NET` for netstandard assemblies, so the generated
`ModuleInitializer` is off. The host installs the module itself (next step).

## Step 5: the host

`PlainHost` owns everything native: the `TimelineWorld`, the `PlaybackTable`,
the `KindStore` and the event buffer. Your column arrays are plain managed
arrays.

```csharp
public PlainHost(byte[] library, int rows, float walkStat = 1f, float jumpPower = 2f, int health = 1000)
{
    if (library == null || library.Length == 0)
        throw new ArgumentException("The library bytes are empty.", nameof(library));
    Rows = rows;
    Motion = new Motion[rows];
    WalkStat = new WalkStat[rows];
    JumpPower = new JumpPower[rows];
    Stamina = new Stamina[rows];
    Health = new Health[rows];
    for (var row = 0; row < rows; row++)
    {
        WalkStat[row] = new WalkStat { Value = walkStat };
        JumpPower[row] = new JumpPower { Value = jumpPower };
        Health[row] = new Health { Value = health };
    }
    InstallGeneratedCode();
    _world = new TimelineWorld(library);
    _players = new PlaybackTable(rows);
    _kinds = new KindStore(_world, _players);
    _damage = _kinds.Pulses<int>("Damage");
    _stat = _kinds.Pulses<int>("StatChange");
    _cues = new Events<Cue>(16);
}
```

A row is an entity index. `PlaybackTable(rows)` gives each row four playback
slots and grows them when needed.

The module is installed once per assembly. The check reads the module list
instead of a static of ours, so it is safe with domain reload off:

```csharp
static void InstallGeneratedCode()
{
    var mine = typeof(PlainHost).Assembly;
    foreach (var module in TlGenerated.Modules)
        if (module.GetType().Assembly == mine)
            return;
    TlSystems.TlModule.Install();
}
```

One step is one tick. `Tick` applies requests, advances the playbacks and
merges pulses. Then each system runs. A `false` argument runs it backward:

```csharp
public void Step(bool forward = true)
{
    _world.Tick(_players, forward);
    new Movement { Motion = Motion, WalkStat = WalkStat, JumpPower = JumpPower, Stamina = Stamina, Cues = _cues.Writer }.Run(_world, _players, forward);
    new Vitals { Health = Health }.Run(_world, _players, forward);
}
```

## Step 6: the MonoBehaviour driver

The library and the targets are set in the Inspector. The host steps in
`FixedUpdate` and writes the result to the Transforms:

```csharp
public sealed class PlainRunner : MonoBehaviour
{
    public TextAsset Library;
    public Transform[] Targets;
    public float UnitsPerMeter = 100f;
    public bool Keyboard = true;

    PlainHost _host;
    Vector3[] _origin;
    bool _rewind;

    public event Action<int, int> CueRaised;

    public PlainHost Host => _host;
    public int Steps { get; private set; }

    void Awake()
    {
        var rows = Targets == null ? 0 : Targets.Length;
        if (Library == null || rows == 0)
            throw new InvalidOperationException("PlainRunner needs a Library asset and at least one target.");
        _host = new PlainHost(Library.bytes, rows);
        _origin = new Vector3[rows];
        for (var row = 0; row < rows; row++)
            _origin[row] = Targets[row].position;
        _host.Start(0, Timelines.Run);
    }
```

```csharp
    void FixedUpdate()
    {
        if (_host == null)
            return;
        _host.Step(!_rewind);
        Steps++;
        Apply();
        ReadCues();
    }

    void Apply()
    {
        for (var row = 0; row < Targets.Length; row++)
        {
            var motion = _host.Motion[row];
            Targets[row].position = _origin[row] + new Vector3(motion.X, motion.Y, 0f) / UnitsPerMeter;
        }
    }
```

To use it: make a GameObject with `PlainRunner`, drag `game.bytes` into
`Library`, add one Transform to `Targets` (for example a cube), press Play. The
Transform moves along X (3 units after 150 steps at the default 100 units per
meter). Keys (legacy input
manager): R run, J jump, B speed boost, P potion hop, S stomp, W whistle,
X stop all, Space pause or resume run, Backspace (held) rewind.

The library says `tickRate` 60 but Unity steps at 50 Hz by default. The
runtime counts ticks only. Set `Time.fixedDeltaTime` to `1f / 60f` if
timeline time should equal seconds.

## Start, stop, pause, rewind

All of these are requests or flags. They take effect in the next `Step`.

```csharp
public void Start(int row, ushort timeline, ushort at = 0) => _players.Start(row, timeline, at);

public void Stop(int row, ushort timeline) => _players.Stop(row, timeline);

public void StopChannel(int row, byte channel) => _players.StopChannel(row, channel);

public void StopAll(int row) => _players.StopAll(row);

public bool IsPlaying(int row, ushort timeline) => _players.IsPlaying(row, timeline);
```

Ids come from the generated `Timelines` class (`Timelines.Run`,
`Timelines.Jump`). A timeline on a channel replaces the one on that channel.
A timeline without a channel stacks.

Pause is a flag on the playback slot. Paused slots do not advance:

```csharp
public void SetPaused(int row, ushort timeline, bool paused)
{
    var slots = _players.View.SlotOf(row, 0);
    for (var slot = 0; slot < _players.CountOf(row); slot++)
        if (slots[slot].Timeline == timeline)
            slots[slot].Flags = paused ? slots[slot].Flags | PlaybackFlags.Paused : slots[slot].Flags & ~PlaybackFlags.Paused;
}
```

Rewind is `Step(false)`. The test run: start `run`, step 3 times (X is 6),
pause and step 3 times (X stays 6), resume and step once (X is 8), then step
backward 4 times (X is 0).

A stop is not instant. The slot stays in a stopping state for one step.
`IsPlaying` already ignores it. After one more step the row is empty.

Game code can also push one-step values into a kind:

```csharp
public void Damage(int row, int amount) => _damage.To(row, amount);

public void ChangeStamina(int row, int delta) => _stat.To(row, delta);
```

Pulses sent before a `Step` merge during that step with the pulses that clips
send. A pulse that cannot be delivered is counted in `DroppedPulses`.

## Read events

Consumers raise events into the `Events<T>` the system holds. Read them after
the step. `Drain` sorts by entity, slot and raise order, and clears the
buffer:

```csharp
public Span<EventRecord<Cue>> DrainCues() => _cues.Drain();
```

```csharp
void ReadCues()
{
    foreach (var cue in _host.DrainCues())
        CueRaised?.Invoke(cue.Entity.Index, cue.Value.Voice);
}
```

`Drain` returns a `Span`. Use it in the same method. Do not store it.

## Dispose

Native memory is not garbage collected. Dispose the host in `OnDestroy`:

```csharp
public void Dispose()
{
    if (_disposed)
        return;
    _disposed = true;
    _cues.Dispose();
    _kinds.Dispose();
    _players.Dispose();
    _world.Dispose();
}
```

```csharp
void OnDestroy()
{
    if (_host == null)
        return;
    _host.Dispose();
    _host = null;
}
```

Dispose the `KindStore` before the `PlaybackTable`, and the table before the
world. `Dispose` twice is safe. Do not use the host after `Dispose`.

## Domain reload off

With Enter Play Mode options and domain reload off, statics survive between
Play sessions. The sample has no statics of its own. The one shared piece of
state is the generated module list inside `Tl.Core`. That list survives too,
and `InstallGeneratedCode` finds the module and does not add it again. The
test `TwoSessionsInOneDomainGiveTheSameHashAndInstallTheModuleOnce` runs two
hosts in one domain and checks both the hash and that one module is installed.

If you add statics of your own, reset or dispose them in a
`[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`
method.

## Threading and allocation

- Use the host from the main thread only. `TimelineWorld`, `PlaybackTable`
  and `KindStore` have no locks. A `Step` is not thread-safe, and it does not
  use the job system.
- A step does not allocate managed memory after warm-up. The test
  `AStepAfterWarmUpAllocatesNothing` warms up 200 steps, then reads
  `GC.GetAllocatedBytesForCurrentThread` over 1000 steps and gets 0 bytes
  (Unity editor, CoreCLR). Keep your own code out of the hot path too: do not
  allocate in `ReadCues` handlers.
- The first steps allocate a little (88 B over the first 100 steps in a .NET console run). Run a warm-up step
  during loading, not in the first gameplay frame.
- `Events<T>` grows its native buffer when it is full. Pick a capacity that
  fits one step.
- Creating a host loads and validates the whole library. Do it once, not per
  spawn.

## Plain C# host or ECS host

| | Plain C# host | ECS host |
|---|---|---|
| Entity | a row index into your arrays | an `Entity` with buffers |
| Playbacks | `PlaybackTable`, native, owned by `PlainHost` | `Playback` buffer on each entity |
| Your data | managed arrays or `Span` columns | components |
| Kinds | `KindStore` tables, one row per entity index | per-entity entry buffers, `Active` and `Enabled` bits, archetype per kind set |
| Run code | `Run` from `Tl.Gen.Plain` (opt in: `Tl.Unity.Generate.Plain`) | Burst jobs from `Tl.Gen.Unity` (opt in: `Tl.Unity.Generate`) |
| Step | you call `Tick` and each `Run` | `TimelineSimulationGroup` systems |
| Authoring | by hand in code and JSON | Timeline Player in a SubScene, bakers |
| Burst, jobs | none, main thread | yes |
| Library | `TextAsset` bytes (or any `byte[]`) | `TimelineLibraryBootstrap.Use`, then a `TimelineLibrary` singleton per world |
| Hash of the 600-step script | `13943614770702343869` | `13943614770702343869` |
| Allocation per warm step | 0 B (test above) | not measured in this repo for the ECS host |

Numbers in this repository:

- .NET, generated path, 100,000 entities, 32 steps per op, i9-14900K, .NET 10:
  `Tick` plus `Run` for one walk entry per entity takes 31.09 ms (K-A);
  `Tick` alone takes 2.35 ns per playback (K-D); warm steps allocate 0 B.
  Source: `external/tl-kinds/benchmarks/KindSystems/README.md`.
- The ECS host has a benchmark (`Tests/Kinds/Bench/KindBench.cs`) but no
  committed result. The plain host in Unity has no benchmark. This guide makes
  no speed claim for either.

## When to choose ECS

Choose the plain host when:

- you have a modest number of animated things;
- your game is MonoBehaviours and GameObjects and you do not want a world, a
  SubScene or bakers;
- you want to test timelines in a plain .NET project with the same code.

Choose the ECS host when:

- you already use Entities and your data lives in components;
- you need Burst and jobs, or very many entities;
- designers should assign timelines to GameObjects in a SubScene with the
  Timeline Player component.

The timeline JSON and the track, clip and kind model are the same for both.
The generated code, the host and the data columns differ.

## Tests

`Tests/Plain/` holds a copy of the sample that the tests compile (Unity does
not compile `Samples~`). `PlainSampleSyncTests` fails if the two copies'
sources differ. The copy compiles as `Tl.Unity.Plain`, so the generator keys
its pairs with that assembly name, and its `game.bytes` is baked from the same
`Timelines~` against `Tl.Unity.Plain.dll`.

PlayMode tests:

| Test | Checks |
|---|---|
| `SixHundredStepScriptHashesLikeDotNet` | hash equals `13943614770702343869`, no dropped pulses |
| `TwoSessionsInOneDomainGiveTheSameHashAndInstallTheModuleOnce` | two hosts in one domain |
| `StartStopPauseAndRewindMoveOneRow` | start, pause, resume, rewind, stop |
| `ATimelineCueIsReadOnceAfterTheStepThatRaisedIt` | events |
| `EventScriptRaisesTheSameCuesAsTheEcsHost` | 600-step whistle script, cue hash `6607956939547305625`, equal to the ECS host |
| `GamePulsesAndClipsWriteTheRowColumns` | clips and game pulses |
| `AStepAfterWarmUpAllocatesNothing` | 0 B over 1000 steps |
| `TheRunnerStepsInFixedUpdateMovesATransformAndDisposesOnDestroy` | the MonoBehaviour in real `FixedUpdate` |

Run them with `eng/test-com-tl-unity.sh`.

## Limits

- The library is baked by hand with `tlb`. No Unity importer runs it.
- The sample is main-thread, single-world.
- Player builds were checked on Windows (CoreCLR and IL2CPP) with a scratch
  scene only.
