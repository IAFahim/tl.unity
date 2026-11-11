using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using CorePlayback = global::Tl.Playback;

namespace Tl.Unity.Tests
{
    public unsafe class GlideKernelTests
    {
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        struct GlideJob : IJob
        {
            public NativeArray<CorePlayback> Slots;
            [ReadOnly] public NativeArray<LibraryTimeline> Timelines;
            public NativeArray<int> Proof;
            public bool Forward;

            [BurstDiscard]
            static void MarkManaged(ref int compiled) => compiled = 0;

            public void Execute()
            {
                var compiled = 1;
                MarkManaged(ref compiled);
                Proof[0] = compiled;
                var reverse = SlotWord.WorldReverse(Forward);
                var slots = (CorePlayback*)Slots.GetUnsafePtr();
                var horizon = Horizon.OfRun(slots, Slots.Length, (LibraryTimeline*)Timelines.GetUnsafeReadOnlyPtr(), Timelines.Length, reverse, 255);
                Proof[1] = horizon;
                var steps = Math.Min(17, (int)horizon);
                Proof[2] = steps;
                for (var step = 0; step < steps; step++) GlideKernel.RunScalar((ulong*)slots, Slots.Length, reverse);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void GlideAndHorizonRunUnderBurstAndMatchTheExistingMotionRules(bool forward)
        {
            const int count = 257;
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            Assert.IsTrue(BurstCompiler.IsEnabled);
            var slots = new NativeArray<CorePlayback>(count, Allocator.TempJob);
            var timelines = new NativeArray<LibraryTimeline>(1, Allocator.TempJob);
            var proof = new NativeArray<int>(3, Allocator.TempJob);
            try
            {
                var timeline = new LibraryTimeline { Duration = 128, Loop = 1 };
                timelines[0] = timeline;
                var expected = new CorePlayback[count];
                for (var i = 0; i < count; i++)
                {
                    var value = new CorePlayback { Timeline = 1, Tick = (ushort)(30 + i % 20), Channel = (byte)i, Phase = (byte)i, Flags = (i & 1) == 0 ? PlaybackFlags.None : PlaybackFlags.Reverse };
                    if (i % 7 == 0) value = new CorePlayback { Tick = 65535, Flags = (PlaybackFlags)255, Rate = 255, Phase = 255 };
                    else if (i % 5 == 0) { value.Flags |= PlaybackFlags.Paused; value.Rate = 255; value.Phase = 255; }
                    else if (i % 11 == 0) { value.Flags |= PlaybackFlags.Ended; value.Rate = 1; value.Phase = 15; }
                    slots[i] = value;
                    expected[i] = value;
                }
                new GlideJob { Slots = slots, Timelines = timelines, Proof = proof, Forward = forward }.Schedule().Complete();
                Assert.AreEqual(1, proof[0], "GlideJob ran managed code.");
                Assert.GreaterOrEqual(proof[1], 17);
                Assert.AreEqual(17, proof[2]);
                for (var step = 0; step < proof[2]; step++)
                    for (var i = 0; i < count; i++)
                        if (expected[i].Timeline != 0) Assert.AreEqual(AdvanceResult.Kept, PlaybackRules.Step(ref expected[i], &timeline, 1, forward));
                for (var i = 0; i < count; i++)
                {
                    var actual = slots[i];
                    var reference = expected[i];
                    Assert.AreEqual(*(ulong*)&reference, *(ulong*)&actual, "slot " + i);
                }
            }
            finally
            {
                proof.Dispose();
                timelines.Dispose();
                slots.Dispose();
            }
        }
    }
}
