using System;
using System.Runtime.InteropServices;
using Stopwatch = System.Diagnostics.Stopwatch;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;

namespace Tl.Unity.Tests
{
    // MEASURE-ONLY A/B: tl.unity 4.0 generated Burst jobs (TimelineJobs
    // TimelineAdvanceJob + KindTickJob) vs TlBurstWorld (BurstWorld) on the
    // PhaseClock LargeBench hostile shape (tl benchmarks/PhaseClock/LargeBench.cs):
    // duration 1024, phase (e*37+11)%1024, row (e*97)%N, VaryingLane, full step
    // window [0,1024), loop, ticks 97. Shapes: 1M rows x1 playback (1M visits/tick)
    // and 4M rows x4 playbacks (16M visits/tick, one playback per timeline 1..4).
    // Every arm is asserted bit-identical against its scalar .NET oracle; the test
    // fails on any hash divergence. Timings are logged as TL-AB lines with min-of-
    // rounds ms/tick; no timing thresholds (cross-machine flaky).
    //
    // 4.0 arms: entities carry one Playback buffer with `perRow` slots. Slot
    // Timeline/Tick are set directly (production TimelinePlayer.Start clamping +
    // StartPending is bypassed; initial Tick == phase, Flags None, Channel 0).
    //   Advance arm: TimelineAdvanceJob (TimelineJobs.ScheduleAdvance body,
    //     .Schedule vs .ScheduleParallel).
    //   KindTick arm: KindTickJob (TimelineKindTickSystem body, kindless
    //     KindIndex KindCount 0 so TickEntity's fast path steps Tick with no
    //     events; .Schedule vs .ScheduleParallel).
    // KindTick is the entrypoint that steps Tick per row (the per-row/per-kind
    // fold). TimelineKernel.AdvancePlayback has no normal-step Tick++
    // (tl Core TimelineWorld.Advance has `else current->Tick++`), so the
    // Advance arm only wraps Tick==1023 to 0; its oracle models that
    // divergence explicitly and its ratio is reported, not hidden.
    // Workers: 1 = IJobChunk.Schedule (single sequential job) / BurstWorld
    // Tick(workers 1); N = ScheduleParallel / Tick(workers 0 = ProcessorCount).
    [TestFixture]
    public unsafe class BurstWorldVsTimelineJobsTests
    {
        const int Duration = 1024;
        const int Ticks = 97;

        static float[] VaryingLane(int duration)
        {
            var lane = new float[duration];
            for (var i = 0; i < duration; i++) lane[i] = 1f + (i * 7 % 13) * 0.5f + (i % 5) * 0.25f;
            return lane;
        }

        static global::Tl.Library MakeLibrary(int timelines, float[] lane)
        {
            var specs = new global::Tl.TimelineSpec[timelines];
            for (var t = 0; t < timelines; t++)
                specs[t] = new global::Tl.TimelineSpec((uint)Duration, true, new[]
                {
                    new global::Tl.StepSpec(0u, (uint)Duration, 0u, 1ul, lane, lane),
                });
            var blob = global::Tl.LibraryWriter.Write(specs);
            var result = global::Tl.Library.Verify(blob);
            Assert.IsTrue(result.Ok);
            Assert.IsNotNull(result.Library);
            return result.Library!;
        }

        static ulong HashFloats(float[] acc)
        {
            var h = 14695981039346656037ul;
            for (var i = 0; i < acc.Length; i++)
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(acc[i])) * 1099511628211ul;
            return h;
        }

        // LargeBench hostile generation, grouped by row (entity):
        // for e in [0,total): row=(e*97)%rows, phase=(e*37+11)%1024,
        // timeline=(e/rows)+1 (per-row distinct 1..perRow), rows x perRow.
        static void GenerateGrouped(int rows, int perRow, out ushort[] groupedPhase, out ushort[] groupedTimeline)
        {
            var total = rows * perRow;
            groupedPhase = new ushort[total];
            groupedTimeline = new ushort[total];
            var cursor = new int[rows];
            for (var e = 0; e < total; e++)
            {
                var r = (int)((e * 97L) % rows);
                var slot = cursor[r]++;
                var idx = r * perRow + slot;
                groupedPhase[idx] = (ushort)((e * 37 + 11) % Duration);
                groupedTimeline[idx] = (ushort)(e / rows + 1);
            }
            for (var r = 0; r < rows; r++)
                Assert.AreEqual(perRow, cursor[r], "row " + r + " must be hit exactly perRow times");
        }

        // Scalar .NET oracle for the Burst accumulator: per-row sum in
        // (frame f=1..97, plan) order, plan = timeline order (grouped order).
        static ulong BurstOracleHash(ushort[] groupedPhase, int rows, int perRow, float[] lane)
        {
            var acc = new float[rows];
            for (var r = 0; r < rows; r++)
            {
                var sum = 0f;
                var baseIdx = r * perRow;
                for (var f = 1; f <= Ticks; f++)
                    for (var k = 0; k < perRow; k++)
                    {
                        var t = groupedPhase[baseIdx + k] + f;
                        if (t >= Duration) t -= Duration;
                        sum += lane[t];
                    }
                acc[r] = sum;
            }
            return HashFloats(acc);
        }

        static ulong TickHashAccumulate(ulong h, int tick)
        {
            return (h ^ (uint)tick) * 1099511628211ul;
        }

        // Scalar .NET oracle for the kind-tick fold: final Tick == (phase+97)%1024.
        static ulong KindTickOracleHash(ushort[] groupedPhase, int rows, int perRow)
        {
            var h = 14695981039346656037ul;
            for (var r = 0; r < rows; r++)
                for (var k = 0; k < perRow; k++)
                    h = TickHashAccumulate(h, (groupedPhase[r * perRow + k] + Ticks) % Duration);
            return h;
        }

        // Scalar .NET oracle for the advance job: only Tick==1023 wraps to 0.
        static ulong AdvanceOracleHash(ushort[] groupedPhase, int rows, int perRow)
        {
            var h = 14695981039346656037ul;
            for (var r = 0; r < rows; r++)
                for (var k = 0; k < perRow; k++)
                    h = TickHashAccumulate(h, groupedPhase[r * perRow + k] == Duration - 1 ? 0 : groupedPhase[r * perRow + k]);
            return h;
        }

        static void LogTime(string name, double nsPerVisit, double msPerTick, int visits, int warmupTicks, int rounds, int ticksPerRound)
        {
            Debug.Log("TL-AB TIME " + name + " nsPerVisit=" + nsPerVisit.ToString("F3")
                + " msPerTick=" + msPerTick.ToString("F4")
                + " visitsPerTick=" + visits
                + " warmupTicks=" + warmupTicks + " rounds=" + rounds + " ticksPerRound=" + ticksPerRound + " min");
        }

        static double MeasureTicks(int warmupTicks, int rounds, int ticksPerRound, int visitsPerTick, Action tick, out double msPerTick)
        {
            for (var i = 0; i < warmupTicks; i++) tick();
            var samples = new double[rounds];
            for (var r = 0; r < rounds; r++)
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < ticksPerRound; i++) tick();
                sw.Stop();
                samples[r] = sw.Elapsed.TotalSeconds * 1e9 / ticksPerRound / visitsPerTick;
            }
            Array.Sort(samples);
            msPerTick = samples[0] * visitsPerTick / 1e6;
            return samples[0];
        }

        [Test]
        public void Large1MBy1_BurstVsTimelineJobs()
        {
            RunShape("1M-1", 1_000_000, 1, 30, 5, 60);
        }

        [Test]
        public void Large4MBy4_BurstVsTimelineJobs()
        {
            RunShape("4M-4", 4_000_000, 4, 5, 3, 10);
        }

        void RunShape(string shape, int rows, int perRow, int warmupTicks, int rounds, int ticksPerRound)
        {
            var lane = VaryingLane(Duration);
            var timelines = perRow;
            var visits = rows * perRow;
            GenerateGrouped(rows, perRow, out var groupedPhase, out var groupedTimeline);
            var processorCount = Environment.ProcessorCount;
            Debug.Log("TL-AB shape=" + shape + " rows=" + rows + " perRow=" + perRow
                + " timelines=" + timelines + " visitsPerTick=" + visits
                + " duration=1024 phase=(e*37+11)%1024 row=(e*97)%N lane=VaryingLane window=[0,1024) ticks=97"
                + " processorCount=" + processorCount
                + " jobWorkers=" + global::Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount
                + " burstEnabled=" + global::Unity.Burst.BurstCompiler.IsEnabled
                + " unity=" + Application.unityVersion);

            var burstOracle = BurstOracleHash(groupedPhase, rows, perRow, lane);
            var kindTickOracle = KindTickOracleHash(groupedPhase, rows, perRow);
            var advanceOracle = AdvanceOracleHash(groupedPhase, rows, perRow);

            // --- Arm 1: TlBurstWorld (BurstWorld) ---
            long burstCollectMs;
            long burstFirstRunMs;
            double burstW1Ns, burstWNs, burstW1Ms, burstWNMs;
            ulong burstHash;
            {
                var swBake = Stopwatch.StartNew();
                using (var lib = MakeLibrary(timelines, lane))
                {
                    swBake.Stop();
                    Debug.Log("TL-AB setup burst bakeMs=" + swBake.ElapsedMilliseconds + " timelines=" + timelines);
                    using (var burst = new global::Tl.Unity.BurstWorld(lib, rows))
                    {
                        var sw = Stopwatch.StartNew();
                        for (var idx = 0; idx < visits; idx++)
                        {
                            var r = idx / perRow;
                            var k = idx % perRow;
                            // Reconstruct e order: grouped arrays are row-major;
                            // BurstWorld sorts by (timeline,phase) so Start order
                            // does not affect the per-row sum order.
                            burst.Start(new global::Tl.Row(r), new global::Tl.TimelineId(groupedTimeline[idx]), groupedPhase[idx]);
                        }
                        sw.Stop();
                        burstCollectMs = sw.ElapsedMilliseconds;
                        Assert.AreEqual(visits, burst.ActiveCount);
                        sw.Restart();
                        burst.Run(global::Tl.Direction.Forward, Ticks, 0);
                        sw.Stop();
                        burstFirstRunMs = sw.ElapsedMilliseconds;
                        burstHash = HashFloats(burst.Accumulator.ToArray());
                        burstW1Ns = MeasureTicks(warmupTicks, rounds, ticksPerRound, visits,
                            () => burst.Tick(global::Tl.Direction.Forward, 1), out burstW1Ms);
                        LogTime("burst tick workers=1 shape=" + shape, burstW1Ns, burstW1Ms, visits, warmupTicks, rounds, ticksPerRound);
                        burstWNs = MeasureTicks(warmupTicks, rounds, ticksPerRound, visits,
                            () => burst.Tick(global::Tl.Direction.Forward, 0), out burstWNMs);
                        LogTime("burst tick workers=0(N=" + processorCount + ") shape=" + shape, burstWNs, burstWNMs, visits, warmupTicks, rounds, ticksPerRound);
                        Debug.Log("TL-AB setup burst collectMs=" + burstCollectMs + " firstRun97Ms=" + burstFirstRunMs);
                    }
                    Debug.Log("TL-AB hash burst oracle=" + burstOracle.ToString("X16") + " actual=" + burstHash.ToString("X16") + " shape=" + shape);
                    Assert.AreEqual(burstOracle, burstHash, "BurstWorld hash must equal scalar oracle");
                }
            }

            // --- Arm 2: 4.0 TimelineAdvanceJob ---
            double advW1Ns, advWNs, advW1Ms, advWNMs;
            ulong advHash;
            long advSetupMs;
            {
                var timelines4 = MakeViewTimelines(timelines, out var viewMemory, out var view);
                Debug.Log("TL-AB setup advance viewTimelines=" + timelines4 + " memory=" + viewMemory);
                try
                {
                    var setup = CreateAdvanceWorld(view, rows, perRow, groupedPhase, groupedTimeline, out var world, out var query, out var handle);
                    advSetupMs = setup;
                    for (var f = 0; f < Ticks; f++) AdvanceTick(world.Entities, query, view, handle, false);
                    advHash = HashEcsTicks(world.Entities, world.EntitiesList, perRow);
                    advW1Ns = MeasureTicks(warmupTicks, rounds, ticksPerRound, visits,
                        () => AdvanceTick(world.Entities, query, view, handle, false), out advW1Ms);
                    LogTime("4.0-advance Schedule workers=1 shape=" + shape, advW1Ns, advW1Ms, visits, warmupTicks, rounds, ticksPerRound);
                    advWNs = MeasureTicks(warmupTicks, rounds, ticksPerRound, visits,
                        () => AdvanceTick(world.Entities, query, view, handle, true), out advWNMs);
                    LogTime("4.0-advance ScheduleParallel workers=N shape=" + shape, advWNs, advWNMs, visits, warmupTicks, rounds, ticksPerRound);
                    Debug.Log("TL-AB setup advance ecsSetupMs=" + advSetupMs);
                    Debug.Log("TL-AB hash 4.0-advance oracle=" + advanceOracle.ToString("X16") + " actual=" + advHash.ToString("X16") + " shape=" + shape);
                    Assert.AreEqual(advanceOracle, advHash, "4.0 Advance hash must equal scalar oracle");
                    DestroyWorld(world, query);
                }
                finally
                {
                    Marshal.FreeHGlobal(viewMemory);
                }
            }

            // --- Arm 3: 4.0 KindTickJob (kindless) ---
            double ktW1Ns, ktWNs, ktW1Ms, ktWNMs;
            ulong ktHash;
            long ktSetupMs;
            {
                var timelines4 = MakeViewTimelines(timelines, out var viewMemory, out var view);
                Debug.Log("TL-AB setup kindtick viewTimelines=" + timelines4 + " memory=" + viewMemory);
                try
                {
                    var setup = CreateKindTickWorld(view, rows, perRow, groupedPhase, groupedTimeline, out var world, out var ktQuery, out var index, out var pbHandle, out var reqHandle, out var playingHandle, out var counters);
                    ktSetupMs = setup;
                    for (var f = 0; f < Ticks; f++) KindTickStep(ktQuery, index, pbHandle, reqHandle, playingHandle, counters, false);
                    ktHash = HashEcsTicks(world.Entities, world.EntitiesList, perRow);
                    ktW1Ns = MeasureTicks(warmupTicks, rounds, ticksPerRound, visits,
                        () => KindTickStep(ktQuery, index, pbHandle, reqHandle, playingHandle, counters, false), out ktW1Ms);
                    LogTime("4.0-kindtick Schedule workers=1 shape=" + shape, ktW1Ns, ktW1Ms, visits, warmupTicks, rounds, ticksPerRound);
                    ktWNs = MeasureTicks(warmupTicks, rounds, ticksPerRound, visits,
                        () => KindTickStep(ktQuery, index, pbHandle, reqHandle, playingHandle, counters, true), out ktWNMs);
                    LogTime("4.0-kindtick ScheduleParallel workers=N shape=" + shape, ktWNs, ktWNMs, visits, warmupTicks, rounds, ticksPerRound);
                    Debug.Log("TL-AB setup kindtick ecsSetupMs=" + ktSetupMs);
                    Debug.Log("TL-AB hash 4.0-kindtick oracle=" + kindTickOracle.ToString("X16") + " actual=" + ktHash.ToString("X16") + " shape=" + shape);
                    Assert.AreEqual(kindTickOracle, ktHash, "4.0 KindTick hash must equal scalar oracle");
                    DestroyKindTickWorld(world, ktQuery, counters);
                }
                finally
                {
                    Marshal.FreeHGlobal(viewMemory);
                }
            }

            var ratioBurst1OverKT1 = burstW1Ns / ktW1Ns;
            var ratioBurstNOverKTN = burstWNs / ktWNs;
            var ratioBurst1OverAdv1 = burstW1Ns / advW1Ns;
            var ratioBurstNOverAdvN = burstWNs / advWNs;
            Debug.Log("TL-AB RATIO shape=" + shape
                + " burst/kindtick@1=" + ratioBurst1OverKT1.ToString("F2") + "x"
                + " burst/kindtick@N=" + ratioBurstNOverKTN.ToString("F2") + "x"
                + " burst/advance@1=" + ratioBurst1OverAdv1.ToString("F2") + "x"
                + " burst/advance@N=" + ratioBurstNOverAdvN.ToString("F2") + "x");
        }

        // --- LibraryView (4.0) helpers (PlaybackRulesTests pattern) ---
        int MakeViewTimelines(int timelines, out IntPtr memory, out global::Tl.LibraryView view)
        {
            memory = Marshal.AllocHGlobal(timelines * sizeof(global::Tl.LibraryTimeline));
            var ptr = (global::Tl.LibraryTimeline*)memory;
            for (var i = 0; i < timelines; i++)
                ptr[i] = new global::Tl.LibraryTimeline
                {
                    Id = (ushort)(i + 1),
                    Channel = 0,
                    Next = 0,
                    Loop = 1,
                    Hold = 0,
                    Duration = Duration,
                    StageCount = 0,
                    Stages = null,
                    EventCount = 0,
                    Events = null,
                    EventTickBits = null,
                };
            view = new global::Tl.LibraryView
            {
                Version = global::Tl.LibraryView.AbiVersion,
                TimelineCount = timelines,
                Timelines = ptr,
                ByteCount = 0,
                Kinds = default,
            };
            return timelines;
        }

        sealed class EcsArm
        {
            public global::Unity.Entities.World World;
            public EntityManager Entities;
            public NativeArray<Entity> EntitiesList;
            public EntityQuery Query;
        }

        long CreateAdvanceWorld(global::Tl.LibraryView view, int rows, int perRow, ushort[] groupedPhase, ushort[] groupedTimeline,
            out EcsArm world, out EntityQuery query, out BufferTypeHandle<Playback> handle)
        {
            var sw = Stopwatch.StartNew();
            world = new EcsArm();
            world.World = new global::Unity.Entities.World("TlAdvAB");
            world.Entities = world.World.EntityManager;
            var archetype = world.Entities.CreateArchetype(typeof(Playback));
            world.EntitiesList = world.Entities.CreateEntity(archetype, rows, Allocator.Persistent);
            for (var r = 0; r < rows; r++)
            {
                var buffer = world.Entities.GetBuffer<Playback>(world.EntitiesList[r]);
                Assert.AreEqual(0, buffer.Length);
                for (var k = 0; k < perRow; k++)
                {
                    var idx = r * perRow + k;
                    buffer.Add(new Playback
                    {
                        Timeline = groupedTimeline[idx],
                        Tick = groupedPhase[idx],
                        Channel = 0,
                        Flags = global::Tl.PlaybackFlags.None,
                        Reserved = 0,
                    });
                }
                Assert.AreEqual(perRow, buffer.Length);
            }
            query = world.Entities.CreateEntityQuery(ComponentType.ReadWrite<Playback>());
            handle = world.Entities.GetBufferTypeHandle<Playback>(false);
            Assert.AreEqual(rows, query.CalculateEntityCount());
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        long CreateKindTickWorld(global::Tl.LibraryView view, int rows, int perRow, ushort[] groupedPhase, ushort[] groupedTimeline,
            out EcsArm world, out EntityQuery query, out KindIndex index, out BufferTypeHandle<Playback> pbHandle, out BufferTypeHandle<TimelineRequest> reqHandle, out ComponentTypeHandle<TimelinePlaying> playingHandle, out NativeArray<int> counters)
        {
            var sw = Stopwatch.StartNew();
            world = new EcsArm();
            world.World = new global::Unity.Entities.World("TlKtAB");
            world.Entities = world.World.EntityManager;
            var archetype = world.Entities.CreateArchetype(typeof(Playback), typeof(TimelineRequest), typeof(TimelinePlaying));
            world.EntitiesList = world.Entities.CreateEntity(archetype, rows, Allocator.Persistent);
            for (var r = 0; r < rows; r++)
            {
                var entity = world.EntitiesList[r];
                var buffer = world.Entities.GetBuffer<Playback>(entity);
                Assert.AreEqual(0, buffer.Length);
                for (var k = 0; k < perRow; k++)
                {
                    var idx = r * perRow + k;
                    buffer.Add(new Playback
                    {
                        Timeline = groupedTimeline[idx],
                        Tick = groupedPhase[idx],
                        Channel = 0,
                        Flags = global::Tl.PlaybackFlags.None,
                        Reserved = 0,
                    });
                }
                Assert.AreEqual(perRow, buffer.Length);
                world.Entities.SetComponentEnabled<TimelinePlaying>(entity, true);
            }
            query = world.Entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadWrite<Playback>(),
                    ComponentType.ReadWrite<TimelineRequest>(),
                    ComponentType.ReadOnly<TimelinePlaying>(),
                },
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });
            Assert.AreEqual(rows, query.CalculateEntityCount());

            index = default;
            index.TimelineCount = view.TimelineCount;
            index.KindCount = 0;
            index.Timelines = view.Timelines;
            index.Sec = default;
            counters = new NativeArray<int>(KindCounter.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            pbHandle = world.Entities.GetBufferTypeHandle<Playback>(false);
            reqHandle = world.Entities.GetBufferTypeHandle<TimelineRequest>(false);
            playingHandle = world.Entities.GetComponentTypeHandle<TimelinePlaying>(false);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        static void AdvanceTick(EntityManager entities, EntityQuery query, global::Tl.LibraryView view, BufferTypeHandle<Playback> handle, bool parallel)
        {
            var job = new TimelineAdvanceJob { View = view, PlaybackHandle = handle };
            JobHandle h = parallel ? job.ScheduleParallel(query, default(JobHandle)) : job.Schedule(query, default(JobHandle));
            h.Complete();
        }

        static void KindTickStep(EntityQuery query, KindIndex index, BufferTypeHandle<Playback> pbHandle, BufferTypeHandle<TimelineRequest> reqHandle, ComponentTypeHandle<TimelinePlaying> playingHandle, NativeArray<int> countersRaw, bool parallel)
        {
            var job = new KindTickJob
            {
                Index = index,
                PlaybackHandle = pbHandle,
                RequestHandle = reqHandle,
                PlayingHandle = playingHandle,
                Handles = default,
                Counters = (int*)countersRaw.GetUnsafePtr(),
                StepDir = 1,
                LastSystemVersion = 0,
            };
            JobHandle h = parallel ? job.ScheduleParallel(query, default(JobHandle)) : job.Schedule(query, default(JobHandle));
            h.Complete();
        }

        static ulong HashEcsTicks(EntityManager entities, NativeArray<Entity> list, int perRow)
        {
            var h = 14695981039346656037ul;
            for (var r = 0; r < list.Length; r++)
            {
                var buffer = entities.GetBuffer<Playback>(list[r]);
                Assert.AreEqual(perRow, buffer.Length, "entity " + r + " buffer length");
                for (var k = 0; k < perRow; k++)
                    h = TickHashAccumulate(h, buffer[k].Tick);
            }
            return h;
        }

        static void DestroyWorld(EcsArm arm, EntityQuery query)
        {
            arm.Entities.CompleteAllTrackedJobs();
            query.Dispose();
            arm.EntitiesList.Dispose();
            if (arm.World != null && arm.World.IsCreated)
                arm.World.Dispose();
        }

        static void DestroyKindTickWorld(EcsArm arm, EntityQuery query, NativeArray<int> counters)
        {
            arm.Entities.CompleteAllTrackedJobs();
            query.Dispose();
            if (counters.IsCreated) counters.Dispose();
            arm.EntitiesList.Dispose();
            if (arm.World != null && arm.World.IsCreated)
                arm.World.Dispose();
        }
    }
}
