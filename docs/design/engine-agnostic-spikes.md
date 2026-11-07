# Engine-agnostic phase 0: spikes S-A to S-D

Results of the four spikes in `docs/design/engine-agnostic.md` section 6. Measured 2026-11-08 on Windows 11, Unity 7000.0.0a16 (Burst on,
Entities 7.0.0 built in), .NET SDK 10.0.401, against tl `a588e48a` and tl.unity `916a874`. The spike sources are kept in
`docs/design/spikes/` (outside the package, never compiled by Unity) so each result can be run again; the paths in them point at this
machine's scratch project.

| Spike | Result | Taken |
|---|---|---|
| S-A generator-made `IComponentData` on a `partial` pack struct | PASS, with one limit | main path; the limit shapes phase 8 |
| S-B one folder with `.csproj` and `package.json` | PASS | main path, three rules |
| S-C `Tl.Mathematics.float3` against Unity's `float3` in Burst | PASS | decision 1 main path; the conditional-alias fallback is not taken |
| S-D Unity generator reads kinds from a referenced assembly | FAIL today | fallback until phase 2: a kind and every track that feeds it share one assembly |

## S-A: generator-made component, registered by the ILPP, used by Burst jobs

Setup (`spikes/statgen`, `spikes/probe/SpikeA*`): a 60-line Roslyn generator, labelled `RoslynAnalyzer` in the folder of asmdef `Spike.A`,
turns `[SpikeState] public partial struct SpikeHealth { public float Value; }` into `partial struct SpikeHealth : IComponentData { }` and
writes the `TlUnityGenerated.TlUnityTypeRegistration_<asm>.Register()` that tl.unity's existing ILPostProcessor
(`Unity.Tl.CodeGen`, `TypeRegistrationPostProcessor`) calls from the module initializer. `Spike.A` references `Tl.Unity`, as a pack's
asmdef will. Three systems touch the component: an `IJobChunk` in `Spike.A` itself, and an `IJobEntity` plus a `SystemAPI.Query` in
`Spike.A.Game`, which references `Spike.A`. All jobs `[BurstCompile(CompileSynchronously = true)]` with a `[BurstDiscard]` counter that
only managed code increments.

Measured: `TypeManager.GetTypeIndex<SpikeHealth>()` resolves, `typeof(IComponentData).IsAssignableFrom(typeof(SpikeHealth))` is true,
1,000 entities go 1 -> 2 (IJobChunk) -> 4 (IJobEntity) -> 104 (SystemAPI.Query), and the managed counter stays 0. 0 `error CS`, 0
`warning CS` in the spike assemblies.

The limit (`spikes/probe/SpikeA2`): inside the **same** assembly as the generated partial, Unity's own source generators do not see the
`IComponentData` our generator adds (generators never see each other's output):
`error SGJE0010: IJobEntity.Execute() parameter 'mana' of type global::SpikeA2.SpikeMana is not supported`. A `SystemAPI.Query<RefRW<T>>`
in that same assembly did compile and run, and an `IJobChunk` with `ComponentTypeHandle<T>` works (the C# compiler checks its constraint on
the final compilation).

What it means for packs: a pack's own code never writes an `IJobEntity` over its `[TlState]` types (the kind jobs Tl.Gen.Unity emits are
`IJobChunk`s it writes itself, and the field notes' generic-system trick keeps Unity's generator out of them). Game code that uses the
pack's state with `IJobEntity` lives in another assembly, which is what S-A proves works. Two findings for the real implementation:

- the ILPP only calls the **first** `TlUnityTypeRegistration_*` it finds in an assembly, so `[TlState]` registration must go into
  Tl.Gen.Unity's one registration list, not into a second generator;
- the fallback in the plan (`TlState<Force> : IComponentData` with `RegisterGenericComponentType`) is not needed.

## S-B: one folder with a `.csproj` and a `package.json`

Setup (`spikes/pack`): `tl/packs/Spike/` holds `Tl.Spike.csproj`, `package.json`, `Tl.Spike.asmdef` (references `Tl.Core`, `Tl.Unity`),
`csc.rsp` and two engine-free `.cs` files (a `[TlClip]` clip with a `Tl.Mathematics.float3` and a `TlCurve`, and the math prototype).
`tl/packs/Directory.Build.props` sets `BaseIntermediateOutputPath` to `obj~\` and `BaseOutputPath` to `bin~\` and imports tl's root
`Directory.Build.props`. The scratch project took it as `"com.tl.pack.spike": "file:<tl>/packs/Spike"`.

Measured: `dotnet build packs/Spike/Tl.Spike.csproj -c Release` builds `bin~/Release/net10.0/Tl.Spike.dll`, 0 warnings with
`TreatWarningsAsErrors`. Unity compiled the same folder as assembly `Tl.Spike` and never looked into `bin~/` or `obj~/`; Tl.Gen.Unity ran on
it (the asmdef references `Tl.Unity`) and emitted the Timeline mirror `Tl.Generated.SpikeForceClip_TlData` with menu name `Spike Force`.
0 `error CS`.

Rules the real packs must follow:

1. `obj~` and `bin~` through `packs/Directory.Build.props` (Unity skips folders ending in `~`), and add `packs/**/bin~/`, `packs/**/obj~/`
   to tl's `.gitignore` (today it only ignores `bin/` and `obj/`).
2. A `csc.rsp` with `-nullable:enable` next to the asmdef. Without it Unity warns `CS8632` on every `?` annotation, because Unity does not
   read the `.csproj`.
3. Commit the `.meta` files Unity writes for every file in the folder (it also imports `.csproj`, `csc.rsp` and `package.json` as assets).
   A git-URL install is immutable, and Unity ignores files without a `.meta` there. Generate them by opening the pack once from a `file:`
   path, never by hand.

No sync script per pack is needed.

## S-C: `Tl.Mathematics.float3` against Unity's `float3` in Burst

Setup (`spikes/probe/SpikeC`, `spikes/probe/SpikeTests`): the prototype `Tl.Mathematics.float3` (three public `float` fields `x, y, z`,
operators `+ - *` written out per component), and two pairs of Burst `IJobFor` kernels that differ only in the math type:

- evaluate: `Force[i] += clip.Direction * clip.Strength * clip.Falloff.Evaluate(0.37f)` (the Force track's body, with a `TlCurve`);
- apply: `total += t; total += t * 0.5f; Force[i] += total` (the kind's `Combine` and `Apply`).

100,000 entities, 10 warm-up runs, then 200 timed runs per row, 7 rounds with the U/T order swapped every round. Same random inputs for
both types.

Bit equality: after the same runs, all 100,000 results are bit-identical between the two types (0 mismatches, compared as
`SingleToInt32Bits`). The managed counter stayed 0, so every kernel ran in Burst.

Speed, ns/entity, median of 7 rounds (last clean run; the run before it gave the same picture):

| Kernel | Unity `float3` | `Tl.Mathematics.float3` |
|---|---|---|
| evaluate, `Run` (one thread) | 3.83 | 3.92 |
| apply, `Run` (one thread) | 3.61 | 2.84 |
| evaluate, `ScheduleParallel` | 0.85 | 0.88 |

Differences are inside the plus or minus 0.5 ns noise band, except the apply row where Tl's type is faster in most rounds. One earlier run
(right after a recompile) showed Unity's evaluate kernel at 17 ns in every round while Tl's stayed at 4; it did not repeat in the next run and
is treated as a disturbed run.

Decision: the main path of decision 1. Packs use `Tl.Mathematics` types; the conditional alias is not taken. The five Force contract files
stay unchanged because they are game code that keeps using `Unity.Mathematics` (nothing in Tl requires them to change), and the two pinned
hashes come from those files and from the KindGame fixture, which this choice does not touch. Work left for phase 8: the rest of the
types (`float2`, `float4`, `quaternion`), the `math` functions packs use, and `ToUnity()`/`ToTl()` at the edge (same layout, so a
reinterpret).

## S-D: Unity generator reads kinds from a referenced assembly

Setup (`spikes/spike-d`): a .NET harness runs the real `TlGenUnityGenerator` (built from `com.tl.unity/Tools~/Tl.Gen.Unity`) on three
compilations: `Spike.D.Kinds` (a `[TlKind] SpikeDHealKind` whose `Apply` writes an `IComponentData`), `Spike.D.Game` (a
`[TlClip] SpikeDHealClip` and `SpikeDHealTrack : ITlTrack<SpikeDHealClip>` with `[Feeds(typeof(SpikeD.SpikeDHealKind))]`, referencing
`Spike.D.Kinds` as metadata), and the two sources in one assembly as a control.

Measured: the control and the kinds assembly generate cleanly (kind types, the auto `IJobChunk`, the registration). The game assembly fails:

```
error TLGEN90: Consumer 'global::SpikeDGame.SpikeDHealTrack' needs Events 'global::Tl.Frame<...SpikeDHealClip>' but no system provides it.
```

Why: both readers (`UnityNewStyle.Prepare` and the linked `Tl.Gen.CSharp` `SystemReader`) find kinds by walking `compilation.SyntaxTrees`,
so a kind compiled into another assembly is invisible. Reading `[TlKind]` from metadata is easy (`GetTypeByMetadataName` and attribute data
work on referenced types), but it is not enough on its own: the kind's assembly already generates its kind types (public) and an auto
`IJobChunk` that applies it, and today the feeders are folded inside that same job. A feeding assembly would have to reuse the referenced
kind types and either own the apply or hand its entries to the owner's job, without registering the kind twice.

Fallback until then: a kind and every track that feeds it live in one assembly. That fits the pack layout (a pack ships its kinds with its
tracks, like `ForceKind` with `ForceTrack`), and game code keeps its own kinds in its own assembly, as the Force contract does. Proposed for
phase 2 (one front end): read kinds from referenced assemblies, generate feeders only in the feeding assembly, and keep kind types and the
apply in the declaring assembly.

## How to run them again

1. Copy `spikes/pack/*` except `Directory.Build.props` to `<tl>/packs/Spike/` and `Directory.Build.props` to `<tl>/packs/`; `dotnet build
   packs/Spike/Tl.Spike.csproj -c Release`.
2. Build `spikes/statgen` (`dotnet build -c Release`), copy `SpikeStateGen.dll` to the scratch project's `Assets/SpikeA/`, run
   `EaSpikeSetup.Run` (`spikes/probe/Editor`) with `-executeMethod` to label it `RoslynAnalyzer`, then add the other `spikes/probe` folders
   and `"com.tl.pack.spike": "file:<tl>/packs/Spike"` to the manifest. Leave out `SpikeA2` unless you want to see the SGJE0010 error.
3. `-runTests -testPlatform EditMode -assemblyNames Spike.Tests`; the tests append their lines to the path in `SpikeTests.cs`.
4. S-D: `dotnet run --project spikes/spike-d -c Release -- <tl>/src/Tl.Core/bin/Release/netstandard2.1/Tl.Core.dll`.
