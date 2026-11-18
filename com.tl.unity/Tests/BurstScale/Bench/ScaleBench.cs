using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    public unsafe struct TouchRows : IJobChunk
    {
        [ReadOnly] public BufferTypeHandle<Tl.Unity.Playback> Playbacks;
        [ReadOnly] public BufferTypeHandle<EnergyEntry> Entries;
        [ReadOnly] public ComponentTypeHandle<EnergyActive> Active;
        [ReadOnly] public ComponentTypeHandle<EnergyEnabled> Enabled;
        [ReadOnly] public ComponentTypeHandle<Scale_TotalData> Totals;
        [NativeDisableParallelForRestriction] public NativeArray<ulong> Sums;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 mask)
        {
            var a = chunk.GetEnableableBits(ref Active);
            var b = chunk.GetEnableableBits(ref Enabled);
            ulong sum = (a.ULong0 & b.ULong0) ^ (a.ULong1 & b.ULong1);
            var playbacks = chunk.GetBufferAccessor(ref Playbacks);
            var entries = chunk.GetBufferAccessor(ref Entries);
            var totals = (uint*)chunk.GetRequiredComponentDataPtrRO(ref Totals);
            for (var r = 0; r < chunk.Count; r++)
            {
                var slots = playbacks[r];
                var slotPtr = (ulong*)slots.GetUnsafeReadOnlyPtr();
                for (var s = 0; s < slots.Length; s++) sum += slotPtr[s];
                var list = entries[r];
                var entryPtr = (byte*)list.GetUnsafeReadOnlyPtr();
                for (var j = 0; j < list.Length; j++) sum += *(ulong*)(entryPtr + j * 12) ^ *(uint*)(entryPtr + j * 12 + 8);
                sum += totals[r];
            }
            Sums[unfilteredChunkIndex] = sum;
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
            System.Threading.Volatile.Write(ref Fence[3],1);
            while (System.Threading.Volatile.Read(ref Fence[0]) == 0) System.Threading.Thread.SpinWait(32);
            GC.KeepAlive(new byte[4096]);
            Fence[1] = System.Threading.Thread.CurrentThread.ManagedThreadId;
            System.Threading.Volatile.Write(ref Fence[2],1);
        }
    }

    [BurstCompile]
    public static unsafe class ScaleBench
    {
        const int Warmup = 32, Rounds = 6, Steps = 32;
        public delegate void UpdateFn(IntPtr system, IntPtr state);

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

        public static void RunBatch()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var n = int.Parse(Arg("-scaleN", "1000"));
            var active = int.Parse(Arg("-scaleActive", "4"));
            var workers = int.Parse(Arg("-scaleWorkers", "1"));
            var output = Arg("-scaleOut", Path.Combine(Application.dataPath, "../scale.jsonl"));
            var library = File.ReadAllBytes(Arg("-scaleLibrary", "scale.tlib"));
            JobsUtility.JobWorkerCount = workers;
            BurstCompiler.Options.EnableBurstSafetyChecks = Arg("-scaleSafety", "0") == "1";
            JobsUtility.JobDebuggerEnabled = false;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            int proof = 0;
            new BurstProof { Result = &proof }.Schedule().Complete();
            if (!BurstCompiler.IsEnabled || proof != 1) throw new InvalidOperationException("Burst proof failed");
            BurstCompiler.CompileFunctionPointer<UpdateFn>(TickUpdate);
            BurstCompiler.CompileFunctionPointer<UpdateFn>(KindUpdate);
            using (var log = new StreamWriter(output, false))
            {
                Log(log, "env unity=" + Application.unityVersion + " burst=" + BurstCompiler.IsEnabled + " proof=" + proof + " workers=" + JobsUtility.JobWorkerCount + " maxWorkers=" + JobsUtility.JobWorkerMaximumCount
                    + " burstSafety=" + BurstCompiler.Options.EnableBurstSafetyChecks + " jobDebugger=" + JobsUtility.JobDebuggerEnabled + " leakDetection=" + NativeLeakDetection.Mode);
                if (Arg("-scaleMatrix", "0") == "1")
                {
                    var counts = new[] { 1000000, 4000000, 10000 };
                    foreach (var count in counts)
                        foreach (var a in count == 10000 ? new[] { 40, 100, 150 } : new[] { 1, 2, 4 })
                        {
                            JobsUtility.JobWorkerCount = workers;
                            Run(log, library, count, a, workers);
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                }
                else if (Arg("-scaleHeadline", "0") == "1")
                {
                    Run(log, library, 4000000, 4, workers);
                    Run(log, library, 10000, 150, workers);
                }
                else Run(log, library, n, active, workers);
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

        static void Run(StreamWriter log, byte[] bytes, int n, int active, int workers)
        {
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var privateBefore = process.PrivateMemorySize64;
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
                for (var t = 1; t < Warmup; t++) Step(world, tick, scale);
                var samples = new double[Rounds];
                var roundAlloc = new long[Rounds];
                var totalMethod = typeof(GC).GetMethod("GetTotalAllocatedBytes",new[] { typeof(bool) });
                var totalGc = totalMethod == null ? null : (Func<bool,long>)totalMethod.CreateDelegate(typeof(Func<bool,long>));
                var processGcBefore = totalGc == null ? -1 : totalGc(true);
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var r = 0; r < Rounds; r++)
                {
                    var roundBefore = totalGc == null ? -1 : totalGc(true);
                    start = Stopwatch.GetTimestamp();
                    for (var t = 0; t < Steps; t++) Step(world, tick, scale);
                    samples[r] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / Steps;
                    roundAlloc[r] = totalGc == null ? -1 : totalGc(true)-roundBefore;
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                var processGcBytes = totalGc == null ? -1 : totalGc(true)-processGcBefore;
                long retainedAlloc = 0;
                for (var r = 1; r < Rounds; r++) retainedAlloc += roundAlloc[r];
                process.Refresh();
                var privateDelta = process.PrivateMemorySize64 - privateBefore;
                var nativeDelta = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()-nativeBefore;
                var hash = VerifyAndHash(manager, n, active, Warmup + Rounds * Steps);
                Log(log, "host=unity arm=generated shape=" + n + "x" + active + " workers=" + workers + " setupMs=" + setupMs.ToString("F3") + " firstMs=" + firstMs.ToString("F3") + " readySetupMs=" + (setupMs+firstMs).ToString("F3")
                    + " privateDelta=" + privateDelta + " unityNativeDelta=" + nativeDelta
                    + " allocMainThread=" + allocated + " allocProcess=" + processGcBytes + " allocRetained=" + retainedAlloc + " roundAlloc=" + string.Join(",",roundAlloc) + " ticks=" + Rounds * Steps + " hash=" + hash.ToString("X16") + " samplesMs=" + string.Join(",", Array.ConvertAll(samples, x => x.ToString("F6"))));
                Profile(log, world, tick, scale, n, active, workers);
                Touch(log, world, n, active);
                Archetype(log, archetype, n, active);
                Occupancy(log, manager, n, active);
                Log(log, "burstProbe shape=" + n + "x" + active + " kindJob=" + (ScaleGame.ManagedProbe.Hits == 0 ? 1 : 0) + " proofJob=1 tickJob=not-instrumented");
                AllocationAudit(log,world,tick,scale,n,active,workers);
            }
        }

        static ulong VerifyAndHash(EntityManager manager, int n, int active, int ticks)
        {
            var expected = new float[1024];
            for (var p = 0; p < 1024; p++)
                for (var t = 0; t < ticks; t++) expected[p] += 1.25f + ((p + t) & 31) * 0.25f;
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
                            var phase = (int)((e * 37L + s * 11) & 1023);
                            if (p->Timeline != 1 + ((e * 17L + s * 7) % 1000) || p->Tick != ((phase + ticks - 1) & 1023)) throw new InvalidOperationException("playback oracle");
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

        static void Profile(StreamWriter log, World world, SystemHandle tick, SystemHandle scale, int n, int active, int workers)
        {
            var samples = new double[4];
            const int steps = 32;
            var etw = Arg("-scaleEtw", "");
            var tracing = etw.Length != 0 && TraceCommand(log,"-on PROC_THREAD+LOADER+CSWITCH+PROFILE -stackwalk Profile -Pmc TotalCycles,InstructionRetired,BranchMispredictions CSwitch strict -f \"" + etw + ".raw.etl\"") == 0;
            if (etw.Length != 0 && !tracing) tracing = TraceCommand(log,"-on PROC_THREAD+LOADER+PROFILE -stackwalk Profile -f \"" + etw + ".raw.etl\"") == 0;
            try
            {
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
                    samples[0] += a-s; samples[1] += b-a; samples[2] += c-b; samples[3] += d-c;
                }
            }
            finally { if (tracing) TraceCommand(log,"-d \"" + etw + ".etl\""); }
            Log(log, "profile shape=" + n + "x" + active + " workers=" + workers + " phases=tickSchedule,tickWait,kindSchedule,kindWait ms="
                + string.Join(",", Array.ConvertAll(samples, x => (x * 1000.0 / Stopwatch.Frequency / steps).ToString("F6"))) + " barriers=2 steps=" + steps);
        }

        static void Touch(StreamWriter log, World world, int n, int active)
        {
            var manager = world.EntityManager;
            using (var query = manager.CreateEntityQuery(typeof(Tl.Unity.Playback), typeof(EnergyEntry), typeof(Scale_TotalData)))
            using (var sums = new NativeArray<ulong>(query.CalculateChunkCountWithoutFiltering(), Allocator.Persistent))
            {
                var job = new TouchRows
                {
                    Playbacks = manager.GetBufferTypeHandle<Tl.Unity.Playback>(true),
                    Entries = manager.GetBufferTypeHandle<EnergyEntry>(true),
                    Active = manager.GetComponentTypeHandle<EnergyActive>(true),
                    Enabled = manager.GetComponentTypeHandle<EnergyEnabled>(true),
                    Totals = manager.GetComponentTypeHandle<Scale_TotalData>(true),
                    Sums = sums
                };
                job.ScheduleParallel(query, default(JobHandle)).Complete();
                const int steps = 32;
                var start = Stopwatch.GetTimestamp();
                for (var t = 0; t < steps; t++) job.ScheduleParallel(query, default(JobHandle)).Complete();
                var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / steps;
                ulong total = 0;
                for (var i = 0; i < sums.Length; i++) total += sums[i];
                Log(log, "touch shape=" + n + "x" + active + " ms=" + ms.ToString("F6") + " chunks=" + sums.Length + " checksum=" + total.ToString("X16"));
            }
        }

        static void Archetype(StreamWriter log, EntityArchetype archetype, int n, int active)
        {
            var types = archetype.GetComponentTypes(Allocator.Temp);
            var parts = new System.Text.StringBuilder();
            var bytes = 0;
            for (var i = 0; i < types.Length; i++)
            {
                var info = TypeManager.GetTypeInfo(types[i].TypeIndex);
                bytes += info.SizeInChunk;
                parts.Append(i == 0 ? "" : ",").Append(TypeManager.GetType(types[i].TypeIndex).Name).Append(':').Append(info.SizeInChunk);
            }
            types.Dispose();
            Log(log, "archetype shape=" + n + "x" + active + " chunkCapacity=" + archetype.ChunkCapacity + " bytesPerEntity=" + bytes + " types=" + parts);
        }

        static void Occupancy(StreamWriter log, EntityManager manager, int n, int active)
        {
            var histogram = new long[257];
            long fold = 0, visits = 0, sampled = 0;
            Tl.Unity.KindIndex index;
            using (var libraryQuery = manager.CreateEntityQuery(typeof(TimelineLibrary)))
                index = libraryQuery.GetSingleton<TimelineLibrary>().Kinds;
            using (var query = manager.CreateEntityQuery(typeof(EnergyEntry), typeof(Tl.Unity.Playback)))
            using (var chunks = query.ToArchetypeChunkArray(Allocator.TempJob))
            {
                var entryType = manager.GetBufferTypeHandle<EnergyEntry>(true);
                var playbackType = manager.GetBufferTypeHandle<Tl.Unity.Playback>(true);
                foreach (var chunk in chunks)
                {
                    var entries = chunk.GetBufferAccessor(ref entryType);
                    var slots = chunk.GetBufferAccessor(ref playbackType);
                    for (var r = 0; r < chunk.Count; r++)
                    {
                        var list = entries[r];
                        histogram[Math.Min(256, list.Length)]++;
                        var pbase = (global::Tl.Playback*)slots[r].GetUnsafeReadOnlyPtr();
                        var data = (byte*)list.GetUnsafeReadOnlyPtr();
                        for (var j = 0; j < list.Length; j++)
                        {
                            var en = (global::Tl.KindEntry*)(data + j * 12);
                            if ((en->Flags & global::Tl.KindEntryFlags.Sample) != 0) sampled++;
                            if (en->Timeline == 0 || en->Owner != 0) continue;
                            visits++;
                            var def = index.StepOf(en->Timeline, en->Step, pbase[en->Slot].Tick);
                            if (def != null && def->FoldLane != null) fold++;
                        }
                    }
                }
            }
            long seen = 0, p50 = -1, p99 = -1, max = 0;
            for (var i = 0; i <= 256; i++)
            {
                if (histogram[i] == 0) continue;
                seen += histogram[i];
                max = i;
                if (p50 < 0 && seen * 2 >= n) p50 = i;
                if (p99 < 0 && seen * 100 >= n * 99L) p99 = i;
            }
            Log(log, "occupancy shape=" + n + "x" + active + " p50=" + p50 + " p99=" + p99 + " max=" + max + " visits=" + visits + " foldLaneShare=" + (visits == 0 ? 0 : (double)fold / visits).ToString("F4") + " sampledEntries=" + sampled);
        }

        static int TraceCommand(StreamWriter log, string args)
        {
            var path = "C:/Program Files (x86)/Windows Kits/10/Windows Performance Toolkit/xperf.exe";
            if (!File.Exists(path)) return -1;
            using (var process = Process.Start(new ProcessStartInfo(path,args) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true }))
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Log(log,"etw exit=" + process.ExitCode + " output=" + output.Replace('\n',' ') + error.Replace('\n',' '));
                return process.ExitCode;
            }
        }

        static void AllocationAudit(StreamWriter log, World world, SystemHandle tick, SystemHandle scale, int n, int active, int workers)
        {
            var method = typeof(GC).GetMethod("GetTotalAllocatedBytes",new[] { typeof(bool) });
            if (method == null) throw new NotSupportedException("Precise all-thread allocation counter is unavailable");
            var total = (Func<bool,long>)method.CreateDelegate(typeof(Func<bool,long>));
            var fence = stackalloc int[4];
            for (var i = 0; i < 4; i++) fence[i] = 0;
            var control = new AllocationControl { Fence = fence }.Schedule();
            JobHandle.ScheduleBatchedJobs();
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
            while (System.Threading.Volatile.Read(ref fence[3]) == 0 && Stopwatch.GetTimestamp() < deadline) System.Threading.Thread.Yield();
            var before = total(true);
            GC.KeepAlive(new byte[4096]);
            System.Threading.Volatile.Write(ref fence[0],1);
            while (System.Threading.Volatile.Read(ref fence[2]) == 0 && Stopwatch.GetTimestamp() < deadline) System.Threading.Thread.Yield();
            control.Complete();
            var calibration = total(true)-before;
            var workerThread = fence[1] != System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (!workerThread || calibration < 8192) throw new InvalidOperationException("All-thread counter calibration failed");
            before = total(true);
            for (var i = 0; i < 32; i++) Step(world,tick,scale);
            var bytes = total(true)-before;
            Log(log,"allocationAudit shape=" + n + "x" + active + " workers=" + workers + " calibrationBytes=" + calibration + " workerThread=" + workerThread + " allThreadGcBytes=" + bytes + " ticks=32 nativeCalls=not-measured");
        }
    }
}
