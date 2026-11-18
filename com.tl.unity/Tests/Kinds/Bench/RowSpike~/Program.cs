using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;

public struct ScoreRow
{
    public int LiftFixed;
    public int Damage;
    public int Stat;
    public int Spare;
    public int WalkKey;
    public int End;
    public int Successor;
    public float Walk;
}

public struct RowTotals
{
    public int LiftFixed;
    public int Damage;
    public int Stat;
    public int WalkKey;
    public float Walk;
    public int Spare;
    public ulong EndedLow;
    public ulong EndedHigh;
}

public static unsafe class RowNet
{
    const int TimelineCount = 500;
    const int AddressableRows = 65536;
    const int EndBit = unchecked((int)0x80000000);
    const int NoWalk = int.MinValue;
    const float FixedToFloat = 1f / 65536f;

    static int DurationOf(int t) => 30 + t * 7 % 31;

    const int BlockRows = 64;

    static int FirstRowOf(int t) => BlockRows * (t + 1) - DurationOf(t);

    static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    static ScoreRow* BuildRows(out int rowCount)
    {
        var rows = (ScoreRow*)NativeMemory.AlignedAlloc((nuint)(AddressableRows * sizeof(ScoreRow)), 64);
        NativeMemory.Clear(rows, (nuint)(AddressableRows * sizeof(ScoreRow)));
        var random = 0x9E3779B9u;
        rowCount = BlockRows * TimelineCount;
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
                row->WalkKey = priority < 0 ? NoWalk : priority << 16 | (first + tick);
                row->Walk = priority < 0 ? 0f : Next(ref random) % 2001 / 100f;
                row->Damage = Next(ref random) % 20 == 0 ? 1 + (int)(Next(ref random) % 9) : 0;
                row->Stat = Next(ref random) % 15 == 0 ? (int)(Next(ref random) % 7) - 3 : 0;
                row->End = tick == duration - 1 ? EndBit : 0;
                row->Successor = tick == duration - 1 ? successor : 0;
            }
        }
        return rows;
    }

    static ScoreRow* RowAt(ScoreRow* rows, ushort origin, uint frame) => rows + (ushort)(origin + frame);

    static void Accumulate(ushort* origins, int count, ScoreRow* rows, uint frame, RowTotals* totals)
    {
        var sums = Vector256<int>.Zero;
        var best = Vector256.Create(NoWalk);
        for (var c = 0; c < count; c++)
        {
            var row = Avx.LoadAlignedVector256((int*)RowAt(rows, origins[c], frame));
            sums = Avx2.Add(sums, row);
            best = Avx2.Max(best, row);
        }
        var lastOfBlock = Vector256.Create((short)(BlockRows - 1));
        var now = Vector256.Create((short)frame);
        var tail = stackalloc short[16];
        ulong endedLow = 0, endedHigh = 0;
        for (var first = 0; first < count; first += 16)
        {
            Vector256<short> group;
            if (first + 16 <= count) group = Avx.LoadVector256((short*)(origins + first));
            else
            {
                for (var i = 0; i < 16; i++) tail[i] = first + i < count ? (short)origins[first + i] : (short)0;
                group = Avx.LoadVector256(tail);
            }
            var ends = Avx2.CompareEqual(Avx2.And(Avx2.Add(group, now), lastOfBlock), lastOfBlock);
            var packed = (uint)Avx2.MoveMask(Avx2.PackSignedSaturate(ends, Vector256<short>.Zero).AsByte());
            var bits = (ulong)((packed & 0xFFu) | ((packed >> 8) & 0xFF00u));
            if (first + 16 > count) bits &= (1ul << (count - first)) - 1;
            if (first < 64) endedLow |= bits << first;
            else endedHigh |= bits << (first - 64);
        }
        var walkKey = best.GetElement(4);
        totals->LiftFixed = sums.GetElement(0);
        totals->Damage = sums.GetElement(1);
        totals->Stat = sums.GetElement(2);
        totals->WalkKey = walkKey;
        totals->Walk = walkKey >= 0 ? rows[walkKey & 0xFFFF].Walk : 0f;
        totals->EndedLow = endedLow;
        totals->EndedHigh = endedHigh;
    }

    static void HandOver(ushort* origins, ulong ended, int firstCell, ScoreRow* rows, uint frame)
    {
        while (ended != 0)
        {
            var c = firstCell + BitOperations.TrailingZeroCount(ended);
            ended &= ended - 1;
            var row = RowAt(rows, origins[c], frame);
            if (row->End != 0) origins[c] = (ushort)(row->Successor - (int)(frame + 1));
        }
    }

    sealed class Game
    {
        public int Count;
        public int Cells;
        public ushort* Origins;
        public RowTotals* Totals;
        public float* MotionX;
        public float* MotionY;
        public int* Health;
        public int* Stamina;
        public float* Walk;
        public float* Power;
        public float* Boost;
        public int* Target;
        public ScoreRow* Rows;
        public uint Frame;

        public Game(ScoreRow* rows, int count, int cells)
        {
            Rows = rows;
            Count = count;
            Cells = cells;
            Origins = (ushort*)NativeMemory.AlignedAlloc((nuint)((long)count * cells * 2), 64);
            Totals = (RowTotals*)NativeMemory.AlignedAlloc((nuint)((long)count * sizeof(RowTotals)), 64);
            MotionX = Alloc<float>(count);
            MotionY = Alloc<float>(count);
            Health = Alloc<int>(count);
            Stamina = Alloc<int>(count);
            Walk = Alloc<float>(count);
            Power = Alloc<float>(count);
            Boost = Alloc<float>(count);
            Target = Alloc<int>(count);
            var firstRow = new int[TimelineCount];
            for (var t = 0; t < TimelineCount; t++) firstRow[t] = FirstRowOf(t);
            for (var e = 0; e < count; e++)
            {
                for (var c = 0; c < cells; c++)
                {
                    var timeline = (e * 7 + c * 13) % TimelineCount;
                    Origins[(long)e * cells + c] = (ushort)(firstRow[timeline] + (e * 31 + c * 17) % DurationOf(timeline));
                }
                Walk[e] = 1f + e % 3 * 0.5f;
                Power[e] = 0.5f + e % 4 * 0.25f;
                Boost[e] = 1f + e % 5 * 0.25f;
                Target[e] = (e * 17 + 3) % count;
                MotionX[e] = 0f;
                MotionY[e] = 0f;
                Health[e] = 1000;
                Stamina[e] = 100;
            }
        }

        static T* Alloc<T>(int count) where T : unmanaged => (T*)NativeMemory.AlignedAlloc((nuint)((long)count * sizeof(T)), 64);

        public void StepRange(int first, int end)
        {
            for (var e = first; e < end; e++)
            {
                var origins = Origins + (long)e * Cells;
                var total = Totals + e;
                Accumulate(origins, Cells, Rows, Frame, total);
                HandOver(origins, total->EndedLow, 0, Rows, Frame);
                HandOver(origins, total->EndedHigh, 64, Rows, Frame);
                MotionY[e] += total->LiftFixed * FixedToFloat * Power[e];
                if (total->WalkKey >= 0) MotionX[e] += total->Walk * Walk[e] * Boost[Target[e]];
                Health[e] -= total->Damage;
                Stamina[e] += total->Stat;
            }
        }

        public ulong Hash()
        {
            var hash = 14695981039346656037ul;
            for (var e = 0; e < Count; e++)
            {
                hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(MotionX[e])) * 1099511628211ul;
                hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(MotionY[e])) * 1099511628211ul;
                hash = (hash ^ (uint)Health[e]) * 1099511628211ul;
                hash = (hash ^ (uint)Stamina[e]) * 1099511628211ul;
            }
            return hash;
        }
    }

    sealed class Workers
    {
        readonly Thread[] _threads;
        readonly Barrier _start;
        readonly Barrier _done;
        Game _game;
        int _next;
        const int Block = 256;

        public Workers(int count)
        {
            _start = new Barrier(count + 1);
            _done = new Barrier(count + 1);
            _threads = new Thread[count];
            for (var i = 0; i < count; i++)
            {
                _threads[i] = new Thread(Run) { IsBackground = true };
                _threads[i].Start();
            }
        }

        void Run()
        {
            while (true)
            {
                _start.SignalAndWait();
                var game = _game;
                while (true)
                {
                    var first = Interlocked.Add(ref _next, Block) - Block;
                    if (first >= game.Count) break;
                    game.StepRange(first, Math.Min(first + Block, game.Count));
                }
                _done.SignalAndWait();
            }
        }

        public void Step(Game game)
        {
            _game = game;
            _next = 0;
            _start.SignalAndWait();
            _done.SignalAndWait();
            game.Frame++;
        }
    }

    public static void Main(string[] args)
    {
        var shapes = args.Length > 0 ? args[0] : "10000x40,10000x100,50000x100,1000000x100";
        var workerCount = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : Environment.ProcessorCount - 1;
        var rows = BuildRows(out var rowCount);
        var workers = new Workers(workerCount);
        Console.WriteLine($"env dotnet={Environment.Version} avx2={Avx2.IsSupported} workers={workerCount} rows={rowCount}");
        foreach (var shape in shapes.Split(','))
        {
            var parts = shape.Split('x');
            var count = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var cells = int.Parse(parts[1], CultureInfo.InvariantCulture);
            var game = new Game(rows, count, cells);
            for (var i = 0; i < 17; i++) workers.Step(game);
            var samples = new double[32];
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 32; i++)
            {
                var start = Stopwatch.GetTimestamp();
                workers.Step(game);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Array.Sort(samples);
            var median = samples[16];
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "cell entities={0} cells={1} totalMs={2:F3} nsPerVisit={3:F4} alloc={4} hash={5:X16}", count, cells, median, median * 1e6 / ((double)count * cells), allocated, game.Hash()));
        }
    }
}
