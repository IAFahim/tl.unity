using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace KindPure.Origins
{
    public sealed unsafe class OriginWorld : IDisposable
    {
        const ulong OriginSeed = 0x1234567ul;
        const uint SeedFrame = 1000;
        readonly World _world;
        readonly EntityManager _entities;
        readonly ComponentType _originType;
        readonly EntityQuery _characters;
        readonly OriginJobShape _shape;
        readonly SystemHandle _system;
        uint _frame;
        public readonly int ChunkCapacity;
        public readonly int BytesInChunk;
        public readonly string TypeSizes;

        public OriginWorld(OriginTable table, int characters, int active, OriginLayout layout, OriginJobShape shape)
        {
            _world = new World("Origins");
            _entities = _world.EntityManager;
            _originType = OriginTypeOf(layout);
            _shape = shape;
            var archetype = _entities.CreateArchetype(ComponentType.ReadWrite<Motion>(), ComponentType.ReadWrite<Vitals>(), ComponentType.ReadWrite<Stats>(), _originType);
            ChunkCapacity = archetype.ChunkCapacity;
            BytesInChunk = SizeInChunk(archetype, out TypeSizes);
            _characters = _entities.CreateEntityQuery(ComponentType.ReadWrite<Motion>(), ComponentType.ReadWrite<Vitals>(), ComponentType.ReadWrite<Stats>(), _originType);
            using (var created = _entities.CreateEntity(archetype, characters, Allocator.Persistent))
                Seed(table, created, active, FixedActiveOf(layout));
            if (shape == OriginJobShape.Entity)
            {
                _system = _world.CreateSystem<OriginEntitySystem>();
                ref var entitySystem = ref _world.Unmanaged.GetUnsafeSystemRef<OriginEntitySystem>(_system);
                entitySystem.Table = table;
                entitySystem.Layout = layout;
            }
            else
            {
                _system = _world.CreateSystem<OriginChunkSystem>();
                ref var chunkSystem = ref _world.Unmanaged.GetUnsafeSystemRef<OriginChunkSystem>(_system);
                chunkSystem.Bind(ref _world.Unmanaged.ResolveSystemStateRef(_system), table, _originType, FixedActiveOf(layout), shape == OriginJobShape.Floor);
            }
        }

        public static ComponentType OriginTypeOf(OriginLayout layout)
        {
            switch (layout)
            {
                case OriginLayout.Heap: return ComponentType.ReadWrite<HeapOrigin>();
                case OriginLayout.Inline2: return ComponentType.ReadWrite<Inline2Origin>();
                case OriginLayout.Inline4: return ComponentType.ReadWrite<Inline4Origin>();
                case OriginLayout.Inline8: return ComponentType.ReadWrite<Inline8Origin>();
                case OriginLayout.Inline100: return ComponentType.ReadWrite<Inline100Origin>();
                case OriginLayout.Fixed2: return ComponentType.ReadWrite<Fixed2Origins>();
                default: return ComponentType.ReadWrite<Fixed4Origins>();
            }
        }

        public static int FixedActiveOf(OriginLayout layout) => layout == OriginLayout.Fixed2 ? 2 : layout == OriginLayout.Fixed4 ? 4 : 0;

        static int SizeInChunk(EntityArchetype archetype, out string typeSizes)
        {
            using var types = archetype.GetComponentTypes(Allocator.Temp);
            var text = new StringBuilder();
            var bytes = 0;
            for (var i = 0; i < types.Length; i++)
            {
                var size = TypeManager.GetTypeInfo(types[i].TypeIndex).SizeInChunk;
                bytes += size;
                text.Append(i == 0 ? "" : "+").Append(TypeManager.GetType(types[i].TypeIndex).Name).Append(':').Append(size);
            }
            typeSizes = text.ToString();
            return bytes;
        }

        void Seed(OriginTable table, NativeArray<Entity> created, int active, int fixedActive)
        {
            _frame = SeedFrame;
            var random = new XorShift64(OriginSeed);
            var entityHandle = _entities.GetEntityTypeHandle();
            var originHandle = _entities.GetDynamicComponentTypeHandle(_originType);
            var motionHandle = _entities.GetComponentTypeHandle<Motion>(false);
            var vitalsHandle = _entities.GetComponentTypeHandle<Vitals>(false);
            var statsHandle = _entities.GetComponentTypeHandle<Stats>(false);
            using var chunks = _characters.ToArchetypeChunkArray(Allocator.Persistent);
            var character = 0;
            for (var c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                var ids = chunk.GetEntityDataPtrRO(entityHandle);
                var motion = (Motion*)chunk.GetRequiredComponentDataPtrRW(ref motionHandle);
                var vitals = (Vitals*)chunk.GetRequiredComponentDataPtrRW(ref vitalsHandle);
                var stats = (Stats*)chunk.GetRequiredComponentDataPtrRW(ref statsHandle);
                var fixedOrigins = fixedActive > 0 ? (ushort*)chunk.GetDynamicComponentDataArrayReinterpret<ushort>(ref originHandle, fixedActive * sizeof(ushort)).GetUnsafePtr() : null;
                var buffers = fixedActive > 0 ? default : chunk.GetUntypedBufferAccessor(ref originHandle);
                for (var row = 0; row < chunk.Count; row++, character++)
                {
                    if (ids[row] != created[character]) throw new InvalidOperationException("chunk order differs from creation order at character " + character);
                    motion[row] = default;
                    vitals[row] = new Vitals { Health = 1000000, Stamina = 0 };
                    stats[row] = new Stats { Walk = 1f + character % 7 * 0.5f, Jump = 2f + character % 3 };
                    ushort* origins;
                    if (fixedActive > 0) origins = fixedOrigins + row * fixedActive;
                    else
                    {
                        buffers.ResizeUninitialized(row, active);
                        origins = (ushort*)buffers.GetUnsafePtr(row);
                    }
                    for (var slot = 0; slot < active; slot++)
                    {
                        var timeline = (int)(random.Next() % OriginTable.Timelines);
                        var tick = random.Next() % (uint)(table.DurationOfTimeline[timeline] - 1);
                        origins[slot] = (ushort)(table.FirstRowOfTimeline[timeline] + tick - _frame);
                    }
                }
            }
        }

        public void Step()
        {
            _frame++;
            if (_shape == OriginJobShape.Entity) _world.Unmanaged.GetUnsafeSystemRef<OriginEntitySystem>(_system).Frame = _frame;
            else _world.Unmanaged.GetUnsafeSystemRef<OriginChunkSystem>(_system).Frame = _frame;
            _system.Update(_world.Unmanaged);
            _entities.CompleteAllTrackedJobs();
        }

        public ulong Hash()
        {
            _entities.CompleteAllTrackedJobs();
            var motionHandle = _entities.GetComponentTypeHandle<Motion>(true);
            var vitalsHandle = _entities.GetComponentTypeHandle<Vitals>(true);
            using var chunks = _characters.ToArchetypeChunkArray(Allocator.Persistent);
            var hash = 1469598103934665603ul;
            for (var c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                var motion = (Motion*)chunk.GetRequiredComponentDataPtrRO(ref motionHandle);
                var vitals = (Vitals*)chunk.GetRequiredComponentDataPtrRO(ref vitalsHandle);
                for (var row = 0; row < chunk.Count; row++)
                {
                    hash = (hash ^ *(uint*)&motion[row].X) * 1099511628211ul;
                    hash = (hash ^ *(uint*)&motion[row].Y) * 1099511628211ul;
                    hash = (hash ^ (uint)vitals[row].Health) * 1099511628211ul;
                    hash = (hash ^ (uint)vitals[row].Stamina) * 1099511628211ul;
                }
            }
            return hash;
        }

        public void Dispose()
        {
            _world.Dispose();
        }
    }

    public sealed class PerfControl : IDisposable
    {
        readonly StreamWriter _control;
        readonly StreamReader _ack;

        public PerfControl(string controlFifo, string ackFifo)
        {
            if (controlFifo.Length == 0) return;
            _control = new StreamWriter(new FileStream(controlFifo, FileMode.Open, FileAccess.Write));
            _ack = new StreamReader(new FileStream(ackFifo, FileMode.Open, FileAccess.Read));
        }

        public void Enable() => Send("enable");

        public void Disable() => Send("disable");

        void Send(string command)
        {
            if (_control == null) return;
            _control.Write(command + "\n");
            _control.Flush();
            _ack.ReadLine();
        }

        public void Dispose()
        {
            _control?.Dispose();
            _ack?.Dispose();
        }
    }

    public static unsafe class OriginBench
    {
        const ulong RowSeed = 0x9E3779B97F4A7C15ul;

        public static void RunBatch()
        {
            var args = Environment.GetCommandLineArgs();
            var log = Path.Combine(UnityEngine.Application.dataPath, "..", "OriginBench.log");
            var cells = "10000x40,10000x100,50000x40,50000x100,1000000x2,1000000x4";
            var layouts = "Heap,Inline2,Inline4,Inline8,Inline100,Fixed2,Fixed4";
            var shapes = "Chunk,Entity";
            var warmup = 40;
            var measure = 16;
            var runs = 5;
            var perfControl = "";
            var perfAck = "";
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-originLog") log = args[i + 1];
                if (args[i] == "-originCells") cells = args[i + 1];
                if (args[i] == "-originLayouts") layouts = args[i + 1];
                if (args[i] == "-originShapes") shapes = args[i + 1];
                if (args[i] == "-originWarmup") warmup = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-originMeasure") measure = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-originRuns") runs = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-originPerfControl") perfControl = args[i + 1];
                if (args[i] == "-originPerfAck") perfAck = args[i + 1];
            }
            Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobDebuggerEnabled = false;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
            using var writer = new StreamWriter(log, append: true);
            using var perf = new PerfControl(perfControl, perfAck);
            var table = new OriginTable(RowSeed);
            try
            {
                Row(writer, "env unity=" + UnityEngine.Application.unityVersion + " burst=" + Unity.Burst.BurstCompiler.IsEnabled + " safety=" + Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks + " workers=" + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + " warmup=" + warmup + " measure=" + measure + " runs=" + runs);
                foreach (var cell in cells.Split(','))
                {
                    var size = cell.Split('x');
                    var characters = int.Parse(size[0], CultureInfo.InvariantCulture);
                    var active = int.Parse(size[1], CultureInfo.InvariantCulture);
                    if (active > OriginTable.MaxActive) throw new ArgumentOutOfRangeException(nameof(cells), active, "at most " + OriginTable.MaxActive + " active timelines per character");
                    foreach (var layoutName in layouts.Split(','))
                    foreach (var shapeName in shapes.Split(','))
                    {
                        var layout = (OriginLayout)Enum.Parse(typeof(OriginLayout), layoutName);
                        var shape = (OriginJobShape)Enum.Parse(typeof(OriginJobShape), shapeName);
                        var fixedActive = OriginWorld.FixedActiveOf(layout);
                        if (fixedActive > 0 && fixedActive != active) continue;
                        if (shape == OriginJobShape.Floor && layout != OriginLayout.Fixed2 && layout != OriginLayout.Fixed4 && layout != OriginLayout.Heap && layout != OriginLayout.Inline100) continue;
                        Cell(writer, perf, table, characters, active, layout, shape, warmup, measure, runs);
                    }
                }
            }
            finally
            {
                table.Dispose();
            }
        }

        static void Row(StreamWriter writer, string text)
        {
            var line = "TlOrigins: " + text;
            writer.WriteLine(line);
            writer.Flush();
            UnityEngine.Debug.Log(line);
        }

        static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

        static void Cell(StreamWriter writer, PerfControl perf, OriginTable table, int characters, int active, OriginLayout layout, OriginJobShape shape, int warmup, int measure, int runs)
        {
            *table.ManagedRuns = 0;
            var medians = new double[runs];
            var samples = new double[measure];
            var hash = 0ul;
            var hashStable = true;
            long allocated = 0;
            double setupMs = 0;
            var capacity = 0;
            var bytesInChunk = 0;
            var typeSizes = "";
            for (var run = 0; run < runs; run++)
            {
                var setupStart = Stopwatch.GetTimestamp();
                using var world = new OriginWorld(table, characters, active, layout, shape);
                setupMs = Ms(setupStart, Stopwatch.GetTimestamp());
                for (var i = 0; i < warmup; i++) world.Step();
                perf.Enable();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < measure; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    world.Step();
                    samples[i] = Ms(start, Stopwatch.GetTimestamp());
                }
                var spent = GC.GetAllocatedBytesForCurrentThread() - before;
                perf.Disable();
                allocated = Math.Max(allocated, spent);
                Array.Sort(samples);
                medians[run] = samples[measure / 2];
                var runHash = world.Hash();
                if (run > 0 && runHash != hash) hashStable = false;
                hash = runHash;
                capacity = world.ChunkCapacity;
                bytesInChunk = world.BytesInChunk;
                typeSizes = world.TypeSizes;
            }
            Array.Sort(medians);
            var ms = medians[runs / 2];
            Row(writer, string.Format(CultureInfo.InvariantCulture,
                "cell characters={0} active={1} layout={2} shape={3} chunkCapacity={4} chunkBytesPerCharacter={5} bytesInChunk={6} stepMs={7:F3} minMs={8:F3} maxMs={9:F3} nsPerCharacter={10:F2} setupMs={11:F0} alloc={12} burst={13} hashStable={14} steps={15} hash={16:x16} types={17}",
                characters, active, layout, shape, capacity, 16384 / capacity, bytesInChunk, ms, medians[0], medians[runs - 1], ms * 1e6 / characters, setupMs, allocated, *table.ManagedRuns == 0, hashStable, warmup + measure, hash, typeSizes));
        }
    }
}
