using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Tl.Unity;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace ScaleGame.Benchmark
{
    public struct ScaleRow : IComponentData
    {
        public int Id;
    }

    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe struct SetupRows : IJobChunk
    {
        public BufferTypeHandle<Tl.Unity.TimelineRequest> Requests;
        public ComponentTypeHandle<ScaleRow> Rows;
        [ReadOnly] public NativeArray<int> Bases;
        public int Active;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 mask)
        {
            var requests = chunk.GetBufferAccessor(ref Requests);
            var rows = chunk.GetNativeArray(ref Rows);
            for (var r = 0; r < chunk.Count; r++)
            {
                var e = Bases[unfilteredChunkIndex] + r;
                rows[r] = new ScaleRow { Id = e };
                var queue = requests[r];
                queue.ResizeUninitialized(Active);
                for (var s = 0; s < Active; s++)
                    queue[s] = TimelineRequest.Start(0, (ushort)(1 + ((e * 17L + s * 7) % 1000)), (ushort)((e * 37L + s * 11) & 1023));
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct BurstProof : IJob
    {
        [NativeDisableUnsafePtrRestriction] public int* Result;
        [BurstDiscard] static void Managed(ref int value) { value = 0; }
        public void Execute()
        {
            var value = 1;
            Managed(ref value);
            *Result = value;
        }
    }

    public unsafe struct AllocationControl : IJob
    {
        [NativeDisableUnsafePtrRestriction] public int* Fence;
        public void Execute()
        {
            System.Threading.Volatile.Write(ref Fence[3], 1);
            while (System.Threading.Volatile.Read(ref Fence[0]) == 0) System.Threading.Thread.SpinWait(32);
            GC.KeepAlive(new byte[4096]);
            Fence[1] = System.Threading.Thread.CurrentThread.ManagedThreadId;
            System.Threading.Volatile.Write(ref Fence[2], 1);
        }
    }

    [BurstCompile]
    public static unsafe class ScaleBench
    {
        const int Warmup = 32, Rounds = 6, Steps = 32, ProbeWindow = 8, MainThreadAuditTicks = 1000;
        public delegate void UpdateFn(IntPtr system, IntPtr state);

        static readonly Type HorizonType = typeof(TimelineKindTick).Assembly.GetType("Tl.Unity.TimelineHorizon");
        static readonly Type GlideType = typeof(TimelineKindTick).Assembly.GetType("Tl.Unity.TimelineGlide");
        static readonly System.Reflection.PropertyInfo ManagedRunsProperty = typeof(TimelineKindTick).GetProperty("ManagedRuns");

        [BurstCompile(CompileSynchronously = true)]
        public static void TickUpdate(IntPtr system, IntPtr state) => ((TimelineKindTickSystem*)system)->OnUpdate(ref *(SystemState*)state);

        [BurstCompile(CompileSynchronously = true)]
        public static void KindUpdate(IntPtr system, IntPtr state) => ((ScaleSystem*)system)->OnUpdate(ref *(SystemState*)state);

        static string Arg(string key, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return fallback;
        }

        static int Prepare(int workers)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            JobsUtility.JobWorkerCount = workers;
            if (Arg("-scaleEditorDefaults", "0") != "1")
            {
                JobsUtility.JobDebuggerEnabled = false;
                NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
                BurstCompiler.Options.EnableBurstSafetyChecks = false;
                BurstCompiler.Options.ForceEnableBurstSafetyChecks = false;
            }
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            int proof = 0;
            new BurstProof { Result = &proof }.Schedule().Complete();
            if (!BurstCompiler.IsEnabled || proof != 1) throw new InvalidOperationException("Burst proof failed");
            BurstCompiler.CompileFunctionPointer<UpdateFn>(TickUpdate);
            BurstCompiler.CompileFunctionPointer<UpdateFn>(KindUpdate);
            return proof;
        }

        static string Settings(int proof)
        {
            return "unity=" + Application.unityVersion + " burst=" + BurstCompiler.IsEnabled + " proof=" + proof + " workers=" + JobsUtility.JobWorkerCount + " maxWorkers=" + JobsUtility.JobWorkerMaximumCount
                + " jobDebugger=" + JobsUtility.JobDebuggerEnabled + " leakDetection=" + NativeLeakDetection.Mode + " burstSafetyChecks=" + BurstCompiler.Options.EnableBurstSafetyChecks
                + " forceBurstSafetyChecks=" + BurstCompiler.Options.ForceEnableBurstSafetyChecks + " horizonType=" + (HorizonType != null) + " managedRunsProbe=" + (ManagedRunsProperty != null);
        }

        static (int N, int Active, int Duration)[] Shapes(string spec)
            => spec.Split(';').Select(x => x.Split(':')).Select(x => (int.Parse(x[0]), int.Parse(x[1]), int.Parse(x[2]))).ToArray();

        static byte[] Library(int duration) => File.ReadAllBytes(Path.Combine(Arg("-scaleLibraries", "."), "scale-" + duration + ".tlib"));

        public static void Compile()
        {
            var workers = int.Parse(Arg("-scaleWorkers", "11"));
            var proof = Prepare(workers);
            var horizon = int.Parse(Arg("-scaleHorizon", "-1"));
            using (var log = new StreamWriter(Arg("-scaleOut", "compile.txt"), false))
            {
                Log(log, "compile " + Settings(proof));
                foreach (var shape in Shapes(Arg("-scaleRuns", "2000:4:3000;2000:4:300;200:150:3000")))
                    Run(log, Library(shape.Duration), shape.N, shape.Active, shape.Duration, workers, horizon, false);
            }
        }

        public static void RunBatch()
        {
            var workers = int.Parse(Arg("-scaleWorkers", "11"));
            var proof = Prepare(workers);
            var horizon = int.Parse(Arg("-scaleHorizon", "-1"));
            using (var log = new StreamWriter(Arg("-scaleOut", Path.Combine(Application.dataPath, "../scale.jsonl")), false))
            {
                Log(log, "env " + Settings(proof) + " arm=" + Arg("-scaleArm", "?") + " horizon=" + horizon);
                foreach (var shape in Shapes(Arg("-scaleRuns", "4000000:4:3000;4000000:4:300;10000:150:3000")))
                {
                    JobsUtility.JobWorkerCount = workers;
                    Run(log, Library(shape.Duration), shape.N, shape.Active, shape.Duration, workers, horizon, true);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }

        static void Log(StreamWriter log, string text)
        {
            log.WriteLine(text);
            log.Flush();
            UnityEngine.Debug.Log("TL-SCALE " + text);
        }

        static void Step(World world, SystemHandle tick, SystemHandle scale)
        {
            tick.Update(world.Unmanaged);
            scale.Update(world.Unmanaged);
            world.EntityManager.CompleteAllTrackedJobs();
        }

        static long ManagedTickRuns(EntityManager manager, SystemHandle tick)
        {
            if (ManagedRunsProperty == null) return -1;
            return (int)ManagedRunsProperty.GetValue(manager.GetComponentData<TimelineKindTick>(tick));
        }

        static void SetGlideLimit(EntityManager manager, SystemHandle tick, int limit)
        {
            var glide = Activator.CreateInstance(GlideType);
            GlideType.GetField("Limit").SetValue(glide, (byte)limit);
            var set = typeof(EntityManager).GetMethods().First(m => m.Name == "SetComponentData" && m.IsGenericMethodDefinition && m.GetParameters()[0].ParameterType == typeof(SystemHandle));
            set.MakeGenericMethod(GlideType).Invoke(manager, new object[] { tick, glide });
        }

        static long PositiveHorizons(EntityManager manager)
        {
            if (HorizonType == null) return -1;
            var positive = 0L;
            using (var query = manager.CreateEntityQuery(ComponentType.ReadOnly(HorizonType)))
            using (var chunks = query.ToArchetypeChunkArray(Allocator.TempJob))
            {
                var handle = manager.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(HorizonType));
                foreach (var chunk in chunks)
                {
                    var bytes = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref handle, 1);
                    for (var i = 0; i < bytes.Length; i++) if (bytes[i] != 0) positive++;
                }
            }
            return positive;
        }

        static void Run(StreamWriter log, byte[] bytes, int n, int active, int duration, int workers, int horizon, bool measure)
        {
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var privateBefore = process.PrivateMemorySize64;
            var workingBefore = process.WorkingSet64;
            var nativeBefore = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            var start = Stopwatch.GetTimestamp();
            using (var library = TlLibrary.FromBytes(bytes))
            using (var world = new World("TlBurstScale"))
            {
                var manager = world.EntityManager;
                library.Publish(manager);
                var tick = world.CreateSystem<TimelineKindTickSystem>();
                var scale = world.CreateSystem<ScaleSystem>();
                var types = new System.Collections.Generic.List<ComponentType>
                {
                    typeof(Tl.Unity.Playback), typeof(TimelineRequest), typeof(TimelinePlaying), typeof(Scale_TotalData), typeof(ScaleRow)
                };
                types.AddRange(EnergyKind.Types);
                if (horizon >= 0)
                {
                    if (HorizonType == null) throw new InvalidOperationException("this package has no TimelineHorizon");
                    types.Add(ComponentType.ReadWrite(HorizonType));
                    SetGlideLimit(manager, tick, horizon);
                }
                var archetype = manager.CreateArchetype(types.ToArray());
                using (var entities = new NativeArray<Entity>(n, Allocator.Persistent)) manager.CreateEntity(archetype, entities);
                using (var query = manager.CreateEntityQuery(typeof(ScaleRow), typeof(TimelineRequest)))
                using (var bases = query.CalculateBaseEntityIndexArray(Allocator.TempJob))
                {
                    new SetupRows
                    {
                        Requests = manager.GetBufferTypeHandle<TimelineRequest>(false),
                        Rows = manager.GetComponentTypeHandle<ScaleRow>(false),
                        Bases = bases,
                        Active = active
                    }.ScheduleParallel(query, default(JobHandle)).Complete();
                }
                var setupMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                start = Stopwatch.GetTimestamp();
                Step(world, tick, scale);
                var firstMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                var ticks = 1;
                var warmStart = Stopwatch.GetTimestamp();
                var windowTick = ManagedTickRuns(manager, tick);
                var windowKind = ScaleProbe.ManagedEvaluations;
                var clean = 0;
                while (ticks < Warmup || clean < ProbeWindow)
                {
                    Step(world, tick, scale);
                    ticks++;
                    var managedTick = ManagedTickRuns(manager, tick);
                    var managedKind = ScaleProbe.ManagedEvaluations;
                    clean = managedTick == windowTick && managedKind == windowKind ? clean + 1 : 0;
                    windowTick = managedTick;
                    windowKind = managedKind;
                    if ((Stopwatch.GetTimestamp() - warmStart) > Stopwatch.Frequency * 300) throw new InvalidOperationException("jobs still run managed after 300 s of warm-up");
                }
                var warmupTicks = ticks;
                if (!measure)
                {
                    Log(log, "compiled shape=" + n + "x" + active + "@" + duration + " warmupTicks=" + warmupTicks + " managedTickRuns=" + windowTick + " managedEvaluations=" + windowKind);
                    return;
                }
                var samples = new double[Rounds];
                var roundAlloc = new long[Rounds];
                var totalMethod = typeof(GC).GetMethod("GetTotalAllocatedBytes", new[] { typeof(bool) });
                var totalGc = totalMethod == null ? null : (Func<bool, long>)totalMethod.CreateDelegate(typeof(Func<bool, long>));
                var processGcBefore = totalGc == null ? -1 : totalGc(true);
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var r = 0; r < Rounds; r++)
                {
                    var roundBefore = totalGc == null ? -1 : totalGc(true);
                    start = Stopwatch.GetTimestamp();
                    for (var t = 0; t < Steps; t++) Step(world, tick, scale);
                    samples[r] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / Steps;
                    roundAlloc[r] = totalGc == null ? -1 : totalGc(true) - roundBefore;
                }
                ticks += Rounds * Steps;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                var processGcBytes = totalGc == null ? -1 : totalGc(true) - processGcBefore;
                var burstTick = ManagedTickRuns(manager, tick) == windowTick ? 1 : 0;
                var burstKind = ScaleProbe.ManagedEvaluations == windowKind ? 1 : 0;
                long retainedAlloc = 0;
                for (var r = 1; r < Rounds; r++) retainedAlloc += roundAlloc[r];
                process.Refresh();
                var privateDelta = process.PrivateMemorySize64 - privateBefore;
                var workingDelta = process.WorkingSet64 - workingBefore;
                var nativeDelta = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() - nativeBefore;
                var positive = PositiveHorizons(manager);
                var hash = VerifyAndHash(manager, n, active, duration, ticks);
                Log(log, "host=unity arm=generated shape=" + n + "x" + active + " duration=" + duration + " horizon=" + horizon + " workers=" + workers + " setupMs=" + setupMs.ToString("F3") + " firstMs=" + firstMs.ToString("F3") + " readySetupMs=" + (setupMs + firstMs).ToString("F3")
                    + " privateDelta=" + privateDelta + " workingSetDelta=" + workingDelta + " privateBytes=" + process.PrivateMemorySize64 + " unityNativeDelta=" + nativeDelta
                    + " allocMainThread=" + allocated + " allocProcess=" + processGcBytes + " allocRetained=" + retainedAlloc + " roundAlloc=" + string.Join(",", roundAlloc)
                    + " warmupTicks=" + warmupTicks + " burstTick=" + burstTick + " burstKind=" + burstKind + " managedTickRuns=" + windowTick + " positiveHorizons=" + positive
                    + " ticks=" + Rounds * Steps + " hash=" + hash.ToString("X16") + " samplesMs=" + string.Join(",", Array.ConvertAll(samples, x => x.ToString("F6"))));
                if (burstTick == 0 || burstKind == 0) throw new InvalidOperationException("a timed job ran managed");
                Profile(log, world, tick, scale, n, active, duration, workers);
                MainThreadAudit(log, world, tick, scale, n, active, duration);
                AllocationAudit(log, world, tick, scale, n, active, duration, workers);
            }
        }

        static ulong VerifyAndHash(EntityManager manager, int n, int active, int duration, int ticks)
        {
            var expected = new float[duration];
            for (var p = 0; p < duration; p++)
                for (var t = 0; t < ticks; t++) expected[p] += 1.25f + (((p + t) % duration) & 31) * 0.25f;
            var hash = 14695981039346656037ul;
            var count = 0;
            using (var query = manager.CreateEntityQuery(typeof(ScaleRow)))
            using (var chunks = query.ToArchetypeChunkArray(Allocator.TempJob))
            {
                var rowType = manager.GetComponentTypeHandle<ScaleRow>(true);
                var totalType = manager.GetComponentTypeHandle<Scale_TotalData>(true);
                var playbackType = manager.GetBufferTypeHandle<Tl.Unity.Playback>(true);
                foreach (var chunk in chunks)
                {
                    var rows = chunk.GetNativeArray(ref rowType);
                    var totals = chunk.GetNativeArray(ref totalType);
                    var slots = chunk.GetBufferAccessor(ref playbackType);
                    for (var r = 0; r < chunk.Count; r++)
                    {
                        var e = rows[r].Id;
                        if (e != count++ || slots[r].Length != active) throw new InvalidOperationException("row/count oracle");
                        var buffer = slots[r];
                        float sum = 0;
                        hash = (hash ^ (uint)active) * 1099511628211ul;
                        for (var s = 0; s < active; s++)
                        {
                            var p = (global::Tl.Playback*)buffer.GetUnsafeReadOnlyPtr() + s;
                            var phase = (int)(((e * 37L + s * 11) & 1023) % duration);
                            if (p->Timeline != 1 + ((e * 17L + s * 7) % 1000) || p->Tick != (phase + ticks - 1) % duration) throw new InvalidOperationException("playback oracle");
                            sum += expected[phase];
                            hash = (hash ^ *(ulong*)p) * 1099511628211ul;
                        }
                        if (sum != totals[r].Value) throw new InvalidOperationException("output oracle row=" + e + " expected=" + sum + " actual=" + totals[r].Value);
                        hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(totals[r].Value)) * 1099511628211ul;
                    }
                }
            }
            if (count != n) throw new InvalidOperationException("entity count");
            return hash;
        }

        static void Profile(StreamWriter log, World world, SystemHandle tick, SystemHandle scale, int n, int active, int duration, int workers)
        {
            var samples = new double[4];
            const int steps = 32;
            for (var t = 0; t < steps; t++)
            {
                var s = Stopwatch.GetTimestamp();
                tick.Update(world.Unmanaged);
                var a = Stopwatch.GetTimestamp();
                world.EntityManager.CompleteAllTrackedJobs();
                var b = Stopwatch.GetTimestamp();
                scale.Update(world.Unmanaged);
                var c = Stopwatch.GetTimestamp();
                world.EntityManager.CompleteAllTrackedJobs();
                var d = Stopwatch.GetTimestamp();
                samples[0] += a - s; samples[1] += b - a; samples[2] += c - b; samples[3] += d - c;
            }
            Log(log, "profile shape=" + n + "x" + active + " duration=" + duration + " workers=" + workers + " phases=tickSchedule,tickWait,kindSchedule,kindWait ms="
                + string.Join(",", Array.ConvertAll(samples, x => (x * 1000.0 / Stopwatch.Frequency / steps).ToString("F6"))) + " barriers=2 steps=" + steps);
        }

        static void MainThreadAudit(StreamWriter log, World world, SystemHandle tick, SystemHandle scale, int n, int active, int duration)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var t = 0; t < MainThreadAuditTicks; t++) Step(world, tick, scale);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Log(log, "mainThreadAudit shape=" + n + "x" + active + " duration=" + duration + " ticks=" + MainThreadAuditTicks + " mainThreadGcBytes=" + bytes);
        }

        static void AllocationAudit(StreamWriter log, World world, SystemHandle tick, SystemHandle scale, int n, int active, int duration, int workers)
        {
            var method = typeof(GC).GetMethod("GetTotalAllocatedBytes", new[] { typeof(bool) });
            if (method == null) throw new NotSupportedException("Precise all-thread allocation counter is unavailable");
            var total = (Func<bool, long>)method.CreateDelegate(typeof(Func<bool, long>));
            var fence = stackalloc int[4];
            for (var i = 0; i < 4; i++) fence[i] = 0;
            var control = new AllocationControl { Fence = fence }.Schedule();
            JobHandle.ScheduleBatchedJobs();
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
            while (System.Threading.Volatile.Read(ref fence[3]) == 0 && Stopwatch.GetTimestamp() < deadline) System.Threading.Thread.Yield();
            var before = total(true);
            GC.KeepAlive(new byte[4096]);
            System.Threading.Volatile.Write(ref fence[0], 1);
            while (System.Threading.Volatile.Read(ref fence[2]) == 0 && Stopwatch.GetTimestamp() < deadline) System.Threading.Thread.Yield();
            control.Complete();
            var calibration = total(true) - before;
            var workerThread = fence[1] != System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (!workerThread || calibration < 8192) throw new InvalidOperationException("All-thread counter calibration failed");
            before = total(true);
            for (var i = 0; i < 32; i++) Step(world, tick, scale);
            var bytes = total(true) - before;
            Log(log, "allocationAudit shape=" + n + "x" + active + " duration=" + duration + " workers=" + workers + " calibrationBytes=" + calibration + " workerThread=" + workerThread + " allThreadGcBytes=" + bytes + " ticks=32 nativeCalls=not-measured");
        }
    }
}
