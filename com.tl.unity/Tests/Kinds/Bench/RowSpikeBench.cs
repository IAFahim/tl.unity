using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace KindGame
{
    public static unsafe class RowSpikeBench
    {
        const int TimelineCount = 500;
        const int ShortestDuration = 30;
        const int DurationSpread = 31;
        const int AddressableRows = 65536;
        const int KindsPerRow = 4;
        const double ClipsPerVisitAtLiveEight = 29034043.0 / 5000000.0;

        public static void RunBatch()
        {
            var args = Environment.GetCommandLineArgs();
            var log = Path.Combine(UnityEngine.Application.dataPath, "..", "RowSpikeBench.log");
            var shapes = "10000x40,10000x100,50000x100,1000000x100";
            var warmup = 16;
            var measure = 32;
            var verifyTicks = 0;
            var verifyCells = "100";
            var modes = "unfused,fused";
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-shapeLog") log = args[i + 1];
                if (args[i] == "-rowShapes") shapes = args[i + 1];
                if (args[i] == "-rowWarmup") warmup = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-rowMeasure") measure = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-rowModes") modes = args[i + 1];
                if (args[i] == "-rowVerifyCells") verifyCells = args[i + 1];
                if (args[i] == "-rowVerify") verifyTicks = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            }
            Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobDebuggerEnabled = false;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
            using var writer = new StreamWriter(log, append: true);
            var library = BuildRows(out var rowCount);
            try
            {
                Row(writer, "env unity=" + UnityEngine.Application.unityVersion + " burst=" + Unity.Burst.BurstCompiler.IsEnabled + " safety=" + Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks + " workers=" + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + " timelines=" + TimelineCount + " addressableRows=" + rowCount + " touchedRows=" + TouchedRows() + " rowBytes=" + sizeof(ScoreRow) + " warmup=" + warmup + " measure=" + measure);
                foreach (var cellsText in verifyCells.Split(','))
                foreach (var mode in modes.Split(','))
                    if (verifyTicks > 0)
                        Row(writer, "verify mode=" + mode + " entities=2000 cells=" + cellsText + " ticks=" + verifyTicks + ": " + Verify(library, rowCount, 2000, int.Parse(cellsText, CultureInfo.InvariantCulture), verifyTicks, mode == "fused"));
                foreach (var shape in shapes.Split(','))
                foreach (var mode in modes.Split(','))
                {
                    var parts = shape.Split('x');
                    Cell(writer, library, rowCount, int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), warmup, measure, mode == "fused");
                }
            }
            finally
            {
                UnsafeUtility.Free(library, Allocator.Persistent);
            }
        }

        static void Row(StreamWriter writer, string text)
        {
            var line = "TlRowSpike: " + text;
            writer.WriteLine(line);
            writer.Flush();
            UnityEngine.Debug.Log(line);
        }

        static int DurationOf(int timelineIndex) => ShortestDuration + timelineIndex * 7 % DurationSpread;

        static int FirstRowOf(int timelineIndex) => RowMath.BlockRows * (timelineIndex + 1) - DurationOf(timelineIndex);

        static ScoreRow* BuildRows(out int rowCount)
        {
            var bytes = (long)AddressableRows * sizeof(ScoreRow);
            var rows = (ScoreRow*)UnsafeUtility.Malloc(bytes, 64, Allocator.Persistent);
            UnsafeUtility.MemClear(rows, bytes);
            var random = 0x9E3779B9u;
            rowCount = RowMath.BlockRows * TimelineCount;
            for (var t = 0; t < TimelineCount; t++)
            {
                var duration = DurationOf(t);
                var first = FirstRowOf(t);
                var successor = t % 5 == 0 ? FirstRowOf((t + 1) % TimelineCount) : first;
                for (var tick = 0; tick < duration; tick++)
                {
                    var row = rows + first + tick;
                    row->LiftFixed = ((int)(Next(ref random) % 4097) - 2048) * 64;
                    var priority = (int)(Next(ref random) % 4) - 1;
                    row->WalkKey = priority < 0 ? RowMath.NoWalk : priority << 16 | (first + tick);
                    row->Walk = priority < 0 ? 0f : Next(ref random) % 2001 / 100f;
                    row->Damage = Next(ref random) % 20 == 0 ? 1 + (int)(Next(ref random) % 9) : 0;
                    row->Stat = Next(ref random) % 15 == 0 ? (int)(Next(ref random) % 7) - 3 : 0;
                    row->End = tick == duration - 1 ? RowMath.EndBit : 0;
                    row->Successor = tick == duration - 1 ? successor : 0;
                }
            }
            return rows;
        }

        static int TouchedRows()
        {
            var touched = 0;
            for (var t = 0; t < TimelineCount; t++) touched += DurationOf(t);
            return touched;
        }

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        static ushort OriginOf(int entity, int cell)
        {
            var timeline = (entity * 7 + cell * 13) % TimelineCount;
            var tick = (entity * 31 + cell * 17) % DurationOf(timeline);
            return (ushort)(FirstRowOf(timeline) + tick);
        }

        sealed class Game : IDisposable
        {
            public World World;
            public EntityManager Entities;
            public NativeArray<Entity> Rows;
            public SystemHandle Accumulate;
            public SystemHandle HandOver;
            public SystemHandle Apply;
            public SystemHandle Fused;
            public bool IsFused;
            public EntityQuery Score;

            public Game(ScoreRow* library, int rowCount, int count, int cells, bool fused)
            {
                if (cells > RowMath.MaxActive) throw new ArgumentOutOfRangeException(nameof(cells), cells, "at most " + RowMath.MaxActive + " active timelines per character");
                IsFused = fused;
                World = new World("RowSpike");
                Entities = World.EntityManager;
                var score = Entities.CreateEntity(typeof(RowScore));
                Entities.SetComponentData(score, new RowScore { Rows = library, RowCount = rowCount, Frame = 0 });
                var archetype = fused
                    ? Entities.CreateArchetype(typeof(RowCell), typeof(RowMotion), typeof(RowVitals), typeof(RowStats), typeof(RowAnchor))
                    : Entities.CreateArchetype(typeof(RowCell), typeof(RowTotals), typeof(RowMotion), typeof(RowVitals), typeof(RowStats), typeof(RowAnchor));
                Rows = Entities.CreateEntity(archetype, count, Allocator.Persistent);
                var firstRow = new int[TimelineCount];
                for (var t = 0; t < TimelineCount; t++) firstRow[t] = FirstRowOf(t);
                for (var e = 0; e < count; e++)
                {
                    var buffer = Entities.GetBuffer<RowCell>(Rows[e]);
                    buffer.ResizeUninitialized(cells);
                    var origins = (ushort*)buffer.GetUnsafePtr();
                    for (var c = 0; c < cells; c++)
                    {
                        var timeline = (e * 7 + c * 13) % TimelineCount;
                        origins[c] = (ushort)(firstRow[timeline] + (e * 31 + c * 17) % DurationOf(timeline));
                    }
                    Entities.SetComponentData(Rows[e], new RowStats { Walk = 1f + e % 3 * 0.5f, JumpPower = 0.5f + e % 4 * 0.25f, Target = Rows[(e * 17 + 3) % count] });
                    Entities.SetComponentData(Rows[e], new RowAnchor { Boost = 1f + e % 5 * 0.25f });
                    Entities.SetComponentData(Rows[e], new RowVitals { Health = 1000, Stamina = 100 });
                }
                Accumulate = World.CreateSystem<RowAccumulateSystem>();
                HandOver = World.CreateSystem<RowHandOverSystem>();
                Apply = World.CreateSystem<RowApplySystem>();
                Fused = World.CreateSystem<RowFusedSystem>();
                Score = Entities.CreateEntityQuery(typeof(RowScore));
            }

            public void Step(double* phaseMs)
            {
                var toMs = 1000.0 / Stopwatch.Frequency;
                var start = Stopwatch.GetTimestamp();
                if (IsFused)
                {
                    Fused.Update(World.Unmanaged);
                    Entities.CompleteAllTrackedJobs();
                    var fusedEnd = Stopwatch.GetTimestamp();
                    Advance();
                    if (phaseMs == null) return;
                    phaseMs[0] = (fusedEnd - start) * toMs;
                    phaseMs[1] = 0;
                    phaseMs[2] = 0;
                    return;
                }
                Accumulate.Update(World.Unmanaged);
                Entities.CompleteAllTrackedJobs();
                var accumulated = Stopwatch.GetTimestamp();
                HandOver.Update(World.Unmanaged);
                Entities.CompleteAllTrackedJobs();
                var handed = Stopwatch.GetTimestamp();
                Apply.Update(World.Unmanaged);
                Entities.CompleteAllTrackedJobs();
                var applied = Stopwatch.GetTimestamp();
                Advance();
                if (phaseMs == null) return;
                phaseMs[0] = (accumulated - start) * toMs;
                phaseMs[1] = (handed - accumulated) * toMs;
                phaseMs[2] = (applied - handed) * toMs;
            }

            void Advance()
            {
                var score = Score.GetSingleton<RowScore>();
                score.Frame++;
                Score.SetSingleton(score);
            }

            public ulong Hash()
            {
                var hash = 14695981039346656037ul;
                for (var e = 0; e < Rows.Length; e++)
                {
                    var motion = Entities.GetComponentData<RowMotion>(Rows[e]);
                    var vitals = Entities.GetComponentData<RowVitals>(Rows[e]);
                    hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(motion.X)) * 1099511628211ul;
                    hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(motion.Y)) * 1099511628211ul;
                    hash = (hash ^ (uint)vitals.Health) * 1099511628211ul;
                    hash = (hash ^ (uint)vitals.Stamina) * 1099511628211ul;
                }
                return hash;
            }

            public void Dispose()
            {
                Rows.Dispose();
                World.Dispose();
            }
        }

        static void Cell(StreamWriter writer, ScoreRow* library, int rowCount, int count, int cells, int warmup, int measure, bool fused)
        {
            var setup = Stopwatch.StartNew();
            using var game = new Game(library, rowCount, count, cells, fused);
            game.Step(null);
            setup.Stop();
            for (var i = 0; i < warmup; i++) game.Step(null);
            var phases = new double[measure * 3];
            var before = GC.GetAllocatedBytesForCurrentThread();
            fixed (double* phase = phases)
                for (var i = 0; i < measure; i++)
                    game.Step(phase + 3 * i);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var accumulate = Median(phases, 0, measure);
            var handOver = Median(phases, 1, measure);
            var apply = Median(phases, 2, measure);
            var totals = new double[measure];
            for (var i = 0; i < measure; i++) totals[i] = phases[3 * i] + phases[3 * i + 1] + phases[3 * i + 2];
            var total = Median(totals, 0, measure, 1);
            var visits = (double)count * cells;
            var chunkCapacity = game.Entities.GetChunk(game.Rows[0]).Capacity;
            Row(writer, string.Format(CultureInfo.InvariantCulture,
                "cell mode={16} entities={0} cells={1} accumulateMs={2:F3} handOverMs={3:F3} applyMs={4:F3} totalMs={5:F3} nsPerVisit={6:F4} nsPerEntity={7:F2} nsPerEntityKind={8:F3} nsPerClipAtLive8={9:F4} chunkCapacity={10} chunkBytesPerEntity={11} heapBytesPerEntity={17} scoreBytes={12} setupMs={13:F0} alloc={14} hash={15:X16}",
                count, cells, accumulate, handOver, apply, total, total * 1e6 / visits, total * 1e6 / count, total * 1e6 / (count * KindsPerRow), total * 1e6 / (visits * ClipsPerVisitAtLiveEight),
                chunkCapacity, 16384 / chunkCapacity, (long)TouchedRows() * sizeof(ScoreRow), setup.Elapsed.TotalMilliseconds, allocated, game.Hash(), fused ? "fused" : "unfused", cells > 100 ? cells * 2 : 0));
        }

        static double Median(double[] values, int offset, int count, int stride = 3)
        {
            var picked = new double[count];
            for (var i = 0; i < count; i++) picked[i] = values[offset + i * stride];
            Array.Sort(picked);
            return picked[count / 2];
        }

        static string Verify(ScoreRow* library, int rowCount, int count, int cells, int ticks, bool fused)
        {
            using var game = new Game(library, rowCount, count, cells, fused);
            var origins = new ushort[count, cells];
            var motionX = new float[count];
            var motionY = new float[count];
            var health = new int[count];
            var stamina = new int[count];
            var walk = new float[count];
            var power = new float[count];
            var boost = new float[count];
            var target = new int[count];
            for (var e = 0; e < count; e++)
            {
                for (var c = 0; c < cells; c++) origins[e, c] = OriginOf(e, c);
                walk[e] = 1f + e % 3 * 0.5f;
                power[e] = 0.5f + e % 4 * 0.25f;
                boost[e] = 1f + e % 5 * 0.25f;
                target[e] = (e * 17 + 3) % count;
                health[e] = 1000;
                stamina[e] = 100;
            }
            var outOfRange = 0;
            var handOvers = 0;
            var walked = 0;
            for (uint frame = 0; frame < ticks; frame++)
            {
                game.Step(null);
                for (var e = 0; e < count; e++)
                {
                    var liftFixed = 0;
                    var damage = 0;
                    var stat = 0;
                    var walkKey = RowMath.NoWalk;
                    var ended = new bool[cells];
                    for (var c = cells - 1; c >= 0; c--)
                    {
                        var index = (ushort)(origins[e, c] + frame);
                        if (index >= rowCount) outOfRange++;
                        var row = library[index];
                        liftFixed += row.LiftFixed;
                        damage += row.Damage;
                        stat += row.Stat;
                        if (row.WalkKey > walkKey) walkKey = row.WalkKey;
                        ended[c] = row.End != 0;
                    }
                    for (var c = 0; c < cells; c++)
                    {
                        if (!ended[c]) continue;
                        handOvers++;
                        origins[e, c] = (ushort)(library[(ushort)(origins[e, c] + frame)].Successor - (int)(frame + 1));
                    }
                    motionY[e] += liftFixed * RowMath.FixedToFloat * power[e];
                    if (walkKey >= 0)
                    {
                        motionX[e] += library[walkKey & 0xFFFF].Walk * walk[e] * boost[target[e]];
                        walked++;
                    }
                    health[e] -= damage;
                    stamina[e] += stat;
                }
                for (var e = 0; e < count; e++)
                {
                    var motion = game.Entities.GetComponentData<RowMotion>(game.Rows[e]);
                    var vitals = game.Entities.GetComponentData<RowVitals>(game.Rows[e]);
                    var buffer = game.Entities.GetBuffer<RowCell>(game.Rows[e]);
                    var same = BitConverter.SingleToInt32Bits(motion.X) == BitConverter.SingleToInt32Bits(motionX[e]) && BitConverter.SingleToInt32Bits(motion.Y) == BitConverter.SingleToInt32Bits(motionY[e]) && vitals.Health == health[e] && vitals.Stamina == stamina[e] && buffer.Length == cells;
                    for (var c = 0; same && c < cells; c++) same = buffer[c].Origin == origins[e, c];
                    if (!same)
                        return string.Format(CultureInfo.InvariantCulture, "DIFFER at frame {0} entity {1}: world X={2} Y={3} H={4} S={5}, reference X={6} Y={7} H={8} S={9}", frame, e, motion.X, motion.Y, vitals.Health, vitals.Stamina, motionX[e], motionY[e], health[e], stamina[e]);
                }
            }
            return string.Format(CultureInfo.InvariantCulture, "equal (motion, vitals and every cell origin bit for bit each tick, reference walks cells in reverse; handOvers={0} walkingEntityTicks={1} outOfRangeReads={2})", handOvers, walked, outOfRange);
        }
    }
}
