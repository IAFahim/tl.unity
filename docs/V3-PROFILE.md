# tl v3 Unity profile

`Tl.Runtime.Unity` 3.0.1-preview.1 is a local NuGet package containing `Tl.Core.dll` for .NET Standard 2.1, source-pinned to tl commit `a464bfe95e158fb79e97f7b0848bd8e8d9c79b7b`. It consumes the same TLB4 containers and ABI 3 folded banks. It is separate from the older UPM runtime and from the published .NET 10-only `Tl.Runtime` 3.0.0. Install one runtime profile in a Unity project.

## Install and extend

```sh
git clone --recurse-submodules https://github.com/IAFahim/tl.unity.git
cd tl.unity
git checkout feat/1-v3-unity-profile
bash eng/gate-v3.sh unity
```

The gate builds the pinned generator and baker, checks every fixture word against the real .NET v3 decoder, runs cold and prepared Apply in separate Mono processes, packs the runtime and sample domain into `artifacts/packages`, and restores the clean test project through NuGetForUnity. Set `TL_MONO` to the installed Unity Mono executable if it is not found automatically. Add that local feed to your project's NuGet configuration and install `Tl.Runtime.Unity`. No runtime source or DLL is copied into Assets by the gate.

Use `samples/Game.Domain/Game.Domain.csproj` as the portable domain template. Keep its .NET Standard 2.1 target, runtime reference, pinned `TlGenAssembly` and `CompileGeneratedBindings` target. Add ordinary unmanaged structs and `ITrack` consumers. The CLI's `unity-entities` backend emits bindings automatically. Package the compiled domain with a new version and install it through NuGetForUnity. Bake against the corresponding .NET game domain assembly, or the portable assembly with the pinned baker; field layouts are checked at preparation. The Studio worker runs game code out of the editor service process.

```csharp
using var asset = TimelineAsset.LoadOwned(tlbBytes);
var view = Timeline<RecoilTrack, RecoilClip>.Prepare(asset);
var columns = new ColumnSet();
columns.Add<RecoilFx>(effects);
columns.Add<RecoilStrength>(liveInputs);
Timeline<RecoilTrack, RecoilClip>.Apply<ushort, ushort>(indices, clocks, true, in columns);
Timeline.Advance(indices, clocks, true);
```

`Fold` depends on authored content. `ExecuteActive` sees current inputs. `Apply` initializes generated consumers on first use and leaves clocks unchanged; a prior `Prepare` call is optional for managed dispatch. The game owns every column, job dependency, world and update boundary. Asset content is interned; different entities can share its index. There are no per-track or per-clip entities.

## Burst and lifetime

Prepare on the managed cold path. Pass `PreparedTimeline` by value to a job, locate typed lane indexes before scheduling, read with `TryRead<T>` and call the game's shared consumer directly. The receipt's BurstDiscard marker proves the tested job used compiled Burst execution. Managed generic registration and `ColumnSet` dispatch are not Burst entry points.

Complete jobs before releasing their columns, clearing a pair's prepared banks, unloading a domain or shutting down the game. `TimelineAsset.LoadOwned` owns one acquisition; disposing one acquisition does not invalidate another load of the same content. Prepared banks own their copied folded values and remain valid until `ClearPrepared` or domain unload. `ClearPrepared` invalidates every prepared view for that pair. Native cold allocations are released on domain unload/process exit; this cleanup does not complete game jobs. Type registrations and content identities have process/domain lifetime as in the pinned runtime.

## Qualified boundary

Validated on Unity 6000.7.0a6 Linux: actual NuGetForUnity restore, Mono, EditMode and PlayMode, real Burst jobs, exact forward/backward raw words, finite/loop clocks, 1/2/4/8-byte results, signed zero, NaN/infinity, live input changes, shared content ownership, failed loads and 0 B managed allocation for warm Apply. Run the gate for the current receipt.

The compatibility projection keeps the authoritative loader, intern table, measurement, segmented encoding, bank layout and live dispatch. `eng/project-v3-profile.py` records source hashes and rejects a changed source pin. It replaces APIs absent from .NET Standard and ref fields with equivalent unmanaged pointers. The .NET SIMD routes and ref-struct BakeContext surfaces are excluded. IL2CPP, other platforms and generating domain bindings directly inside Unity's script compiler are not qualified by this receipt; portable domain builds are the supported extension route. These local packages are not a public release and choose no license.

CI runs `bash eng/gate-v3.sh dotnet` on the hosted .NET runner for source parity and packaging. The required native job runs `bash eng/gate-v3.sh unity` on a licensed runner labeled `self-hosted, unity`, including the fresh-process Mono regression and both Unity suites. The gate's default `mono` stage uses the installed Unity Mono; Ubuntu's older distro Mono is not the qualified runtime.
