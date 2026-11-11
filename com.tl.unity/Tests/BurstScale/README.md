# Unity scale fixture

`ScaleGame.Benchmark.ScaleBench.RunBatch` runs Core's existing `KindRules` through the production `TimelineKindTickSystem` and the generated kind jobs. It does not copy the engine into the benchmark.

Build `scale.tlib` using the companion `tl/benchmarks/BurstScale` project. The `.NET` and Unity `ScaleDomain.cs` files must be byte-identical. Use a scratch Unity project whose manifest references this checkout's `com.tl.unity` and lists it under `testables`.

```powershell
& $unity -batchmode -nographics -quit -projectPath $probe -job-worker-count 11 -executeMethod ScaleGame.Benchmark.ScaleBench.RunBatch -scaleN 4000000 -scaleActive 4 -scaleWorkers 11 -scaleLibrary $fixture -scaleOut $result -logFile $log
```

`-scaleMatrix 1` runs the nine requested shapes using the requested worker count. Repeat in five fresh editor processes for headline results. Unity's worker count is the number of background job workers; the main thread can also participate during completion. This differs from `TlWorkers.WorkerCount`, which includes the .NET caller.

The fixture uses 1,000 available definitions, one sum kind, staggered phases and fixed membership. It does not model 1,000 retained playback slots or arbitrary gameplay. Warmup, rounds and the independent state/output oracle match the .NET harness. Discard the first of the six raw rounds when aggregating.

`readySetupMs` includes the first tick and buffer growth. Memory is captured before verification. Live Unity memory and process private-memory deltas are separate measurements, neither an allocation-traffic counter.

The benchmark fails when Burst is disabled. It runs a Burst-discard sentinel and compiles the actual system update entry points under Burst. Read the complete log as well as the result file; C# warnings/errors, Burst compilation errors and missing records invalidate a run.

On the Unity 7 CoreCLR editor, the runtime GC exposes `GetTotalAllocatedBytes(bool)` even though Unity's compile reference omits it. The harness binds it cold through reflection, measures precise process-wide managed allocation, then calibrates it with allocations on both the caller and a managed worker job. Native allocation-call traffic is not measured by that counter.

The separate profile pass adds a completion barrier between tick and kind work. Its schedule/wait intervals are diagnostic wall times, not pure CPU time. Optional `-scaleEtw <absolute-prefix>` records Windows CPU samples and PMU counters during that pass when xperf is available and permitted.

Use the shared machine locks. Never overlap Unity editors, time while another benchmark holds `bench.lock`, or delete another contributor's live lock.
