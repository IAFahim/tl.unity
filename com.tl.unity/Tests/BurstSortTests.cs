using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Tl.Unity.Tests
{
    [TestFixture]
    public unsafe class BurstSortTests
    {
        static class Probe
        {
            [BurstDiscard]
            static void MarkManaged(ref int value) => value = 1;

            public static int RanManaged()
            {
                var managed = 0;
                MarkManaged(ref managed);
                return managed;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct SortQueueJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public global::Tl.RequestQueue* Queue;
            public NativeArray<int> Managed;

            public void Execute()
            {
                Managed[0] = Probe.RanManaged();
                global::Tl.RequestQueue.SortStable(Queue);
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct SortWithScratchJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public global::Tl.RequestRecord* Items;
            [NativeDisableUnsafePtrRestriction] public global::Tl.RequestRecord* Scratch;
            public int Count;
            public NativeArray<int> Managed;

            public void Execute()
            {
                Managed[0] = Probe.RanManaged();
                global::Tl.RequestQueue.Sort(Items, Count, Scratch);
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct DrainJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public global::Tl.Events<int> Events;
            public int Count;
            public NativeArray<int> Managed;
            public NativeArray<int> Entities;
            public NativeArray<int> Slots;
            public NativeArray<int> Values;

            public void Execute()
            {
                Managed[0] = Probe.RanManaged();
                for (var i = 0; i < Count; i++)
                {
                    Events.Bind((i * 7919) % 53, (i * 31) % 5 - 2);
                    Events.Raise(i);
                }
                var drained = Events.Drain();
                Managed[1] = drained.Length;
                for (var i = 0; i < drained.Length; i++)
                {
                    Entities[i] = drained[i].Entity.Index;
                    Slots[i] = drained[i].Slot;
                    Values[i] = drained[i].Value;
                }
            }
        }

        static void Fill(global::Tl.RequestQueue* queue, int count)
        {
            for (var i = 0; i < count; i++)
                global::Tl.RequestQueue.Add(queue, (i * 7919) % 53, (i * 31) % 5 - 2, (ushort)i, 0, global::Tl.RequestOp.Start);
        }

        static void AssertOrdered(global::Tl.RequestRecord* items, int count)
        {
            for (var i = 1; i < count; i++)
            {
                var a = items[i - 1];
                var b = items[i];
                var ordered = a.Entity < b.Entity || (a.Entity == b.Entity && (a.Slot < b.Slot || (a.Slot == b.Slot && a.Sequence < b.Sequence)));
                Assert.IsTrue(ordered, "records " + (i - 1) + " and " + i + " are out of order");
            }
        }

        [Test]
        public void SortStable_RunsUnderBurstAndOrdersByEntitySlotSequence()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled);
            var queue = global::Tl.RequestQueue.Create();
            var managed = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                // A cold editor can run the managed copy first; retry with fresh unsorted input until Burst has compiled the job (bounded).
                var clock = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    global::Tl.RequestQueue.Clear(queue);
                    Fill(queue, 500);
                    new SortQueueJob { Queue = queue, Managed = managed }.Run();
                    if (managed[0] != 0) System.Threading.Thread.Sleep(20);
                }
                while (managed[0] != 0 && clock.Elapsed.TotalSeconds < 90);
                Assert.AreEqual(0, managed[0], "SortStable ran as managed code, Burst did not compile the job");
                Assert.AreEqual(500, queue->Count);
                var copy = new global::Tl.RequestRecord[500];
                for (var i = 0; i < copy.Length; i++)
                    copy[i] = queue->Item(i);
                fixed (global::Tl.RequestRecord* ptr = copy)
                    AssertOrdered(ptr, copy.Length);
            }
            finally
            {
                managed.Dispose();
                global::Tl.RequestQueue.Free(queue);
            }
        }

        [Test]
        public void SortWithScratch_RunsUnderBurstAndOrdersByEntitySlotSequence()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled);
            var queue = global::Tl.RequestQueue.Create();
            var managed = new NativeArray<int>(1, Allocator.TempJob);
            var items = new NativeArray<global::Tl.RequestRecord>(500, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var scratch = new NativeArray<global::Tl.RequestRecord>(500, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    global::Tl.RequestQueue.Clear(queue);
                    Fill(queue, 500);
                    for (var i = 0; i < 500; i++)
                        items[i] = queue->Item(i);
                    new SortWithScratchJob
                    {
                        Items = (global::Tl.RequestRecord*)items.GetUnsafePtr(),
                        Scratch = (global::Tl.RequestRecord*)scratch.GetUnsafePtr(),
                        Count = 500,
                        Managed = managed,
                    }.Run();
                    if (managed[0] != 0) System.Threading.Thread.Sleep(20);
                }
                while (managed[0] != 0 && clock.Elapsed.TotalSeconds < 90);
                Assert.AreEqual(0, managed[0], "Sort ran as managed code, Burst did not compile the job");
                AssertOrdered((global::Tl.RequestRecord*)items.GetUnsafePtr(), 500);
            }
            finally
            {
                items.Dispose();
                scratch.Dispose();
                managed.Dispose();
                global::Tl.RequestQueue.Free(queue);
            }
        }

        [Test]
        public void Drain_RunsUnderBurstAndOrdersByEntitySlotSequence()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled);
            const int count = 500;
            var events = new global::Tl.Events<int>(count);
            var managed = new NativeArray<int>(2, Allocator.TempJob);
            var entities = new NativeArray<int>(count, Allocator.TempJob);
            var slots = new NativeArray<int>(count, Allocator.TempJob);
            var values = new NativeArray<int>(count, Allocator.TempJob);
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    new DrainJob { Events = events, Count = count, Managed = managed, Entities = entities, Slots = slots, Values = values }.Run();
                    if (managed[0] != 0) System.Threading.Thread.Sleep(20);
                }
                while (managed[0] != 0 && clock.Elapsed.TotalSeconds < 90);
                Assert.AreEqual(0, managed[0], "Drain ran as managed code, Burst did not compile the job");
                Assert.AreEqual(count, managed[1]);
                for (var i = 1; i < count; i++)
                {
                    var ordered = entities[i - 1] < entities[i]
                        || (entities[i - 1] == entities[i] && (slots[i - 1] < slots[i] || (slots[i - 1] == slots[i] && values[i - 1] < values[i])));
                    Assert.IsTrue(ordered, "events " + (i - 1) + " and " + i + " are out of order");
                }
            }
            finally
            {
                values.Dispose();
                slots.Dispose();
                entities.Dispose();
                managed.Dispose();
                events.Dispose();
            }
        }
    }
}
