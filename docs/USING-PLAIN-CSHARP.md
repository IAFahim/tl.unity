# Using Tl from plain C# (no ECS)

Tl's runtime, `Tl.Core`, is plain .NET (netstandard2.1, no Unity dependency). A plain host plays timelines with three objects:
`TimelineWorld` (the library), `PlaybackTable` (who plays what) and `KindStore` (combined kind values). Use it for a server, a tool, a test
or any engine without ECS. Inside Unity, use the ECS path ([USING-ECS.md](USING-ECS.md)); it is the one the designer tools, preview and
live edit are built for.

## What you need

| Piece | Where it comes from |
|---|---|
| `Tl.Core` and the source generator that writes each `[TimelineSystem]`'s `Run` | the `Tl.CSharp` package of IAFahim/tl (or a project reference to its `src/Tl.Core` and `src/Tl.Gen.CSharp`) |
| Clip, track and kind types | your code, the same structs as in Unity (`[TlClip]` needs only the struct) |
| `game.tlib` | Unity: select timelines, `Tl > Export Library`. The selected timelines and every timeline they name are written; the console lists them in id order (ids follow the sorted names, starting at 1). |

## The host

```csharp
using Tl;

public sealed class Host : System.IDisposable
{
    readonly TimelineWorld _world;
    readonly PlaybackTable _players;
    readonly KindStore _kinds;
    readonly Motion[] _motion;

    public Host(byte[] library, int entities)
    {
        _world = new TimelineWorld(library);
        _players = new PlaybackTable(entities);
        _kinds = new KindStore(_world, _players);
        _motion = new Motion[entities];
    }

    public void Start(int entity, ushort timeline, ushort atTick = 0) => _players.Start(entity, timeline, atTick);
    public void Stop(int entity, ushort timeline) => _players.Stop(entity, timeline);
    public void Pause(int entity, ushort timeline) => _players.Pause(entity, timeline);
    public void Resume(int entity, ushort timeline) => _players.Resume(entity, timeline);

    public void Step(bool forward = true)
    {
        _world.Tick(_players, forward);
        new Movement { Motion = _motion }.Run(_world, _players, forward);
    }

    public void Dispose()
    {
        _kinds.Dispose();
        _players.Dispose();
        _world.Dispose();
    }
}
```

- Requests queue until the next `Step`; `Tick` applies them in a stable order and moves every playback one tick, then each of your
  generated systems (`Movement` here, a `[TimelineSystem]` over your `Motion` column) runs its `Run`.
- `Pause` holds a playback's tick (its clips stay active); `Resume` continues. Stepping with `forward: false` rewinds exactly.
- The `[TimelineSystem]` declaration and the generated `Run` are described in the tl README.
- Nothing in the step allocates. Dispose the three objects in reverse order.

## How the two hosts agree

Both hosts run the same `Tl.Core` code on the same library bytes. The package tests keep a 600-step kind script whose state hash is
`13943614770702343869` and an events script with `6607956939547305625`; the ECS host, the plain host (package tests `Tests/Plain`) and the
IL2CPP and CoreCLR players all print those values.

## Limits

- No Timeline window, preview or live edit: those are Unity ECS features.
- A `game.tlib` is a snapshot. Export again after editing timelines.
- `Tl.Core` is unsafe code over native memory; keep a host on one thread.
