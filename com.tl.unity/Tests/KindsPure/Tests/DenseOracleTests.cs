using System;
using NUnit.Framework;
using Tl.Unity;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace KindPure
{
    [TestFixture]
    public class DenseOracleTests
    {
        byte[] _first;
        byte[] _second;
        byte[] _shapeFirst;
        byte[] _shapeSecond;

        [OneTimeSetUp]
        public void BakeBothControlLibrariesOnce()
        {
            _first = PureControls.Library(false);
            _second = PureControls.Library(true);
            _shapeFirst = PureShapeBench.Library(PureShapeBench.LiveTracks);
            _shapeSecond = PureShapeBench.Library(PureShapeBench.LiveTracks - 1);
        }

        sealed class SameRequestsToBothWorlds : IPureRequests
        {
            public PureWorld Dense;
            public PureWorld Explicit;

            public void Request(int row, global::Tl.RequestOp op, ushort timeline, ushort at)
            {
                Dense.Request(row, op, timeline, at);
                Explicit.Request(row, op, timeline, at);
            }
        }

        static string Slot(in Playback slot, ushort tick) => "timeline " + slot.Timeline + " tick " + tick + " channel " + slot.Channel + " flags " + slot.Flags + " rate " + slot.Rate + " phase " + slot.Phase;

        static unsafe string FirstDifference(PureWorld dense, PureWorld plain, int step)
        {
            var a = dense.Entities;
            var b = plain.Entities;
            a.CompleteAllTrackedJobs();
            b.CompleteAllTrackedJobs();
            var px = stackalloc global::Tl.Playback[KindSlots.MaxSlots];
            var py = stackalloc global::Tl.Playback[KindSlots.MaxSlots];
            for (var r = 0; r < dense.Rows.Length; r++)
            {
                var x = dense.Rows[r];
                var y = plain.Rows[r];
                var at = "step " + step + " row " + r + ": ";
                var mx = a.GetComponentData<Movement_MotionData>(x).Value;
                var my = b.GetComponentData<Movement_MotionData>(y).Value;
                if (*(ulong*)&mx != *(ulong*)&my) return at + "motion dense (" + mx.X + ", " + mx.Y + ") explicit (" + my.X + ", " + my.Y + ")";
                if (a.GetComponentData<Movement_StaminaData>(x).Value.Value != b.GetComponentData<Movement_StaminaData>(y).Value.Value) return at + "stamina differs";
                if (a.GetComponentData<Vitals_HealthData>(x).Value.Value != b.GetComponentData<Vitals_HealthData>(y).Value.Value) return at + "health differs";
                if (a.IsComponentEnabled<TimelinePlaying>(x) != b.IsComponentEnabled<TimelinePlaying>(y)) return at + "playing bit differs";
                if (a.GetComponentData<TimelinePlaying>(x).Copies != b.GetComponentData<TimelinePlaying>(y).Copies) return at + "sampled copies differ";
                if (a.IsComponentEnabled<LocomotionActive>(x) != b.IsComponentEnabled<LocomotionActive>(y)) return at + "Locomotion active bit differs";
                if (a.IsComponentEnabled<LiftActive>(x) != b.IsComponentEnabled<LiftActive>(y)) return at + "Lift active bit differs";
                if (a.IsComponentEnabled<StatChangeActive>(x) != b.IsComponentEnabled<StatChangeActive>(y)) return at + "StatChange active bit differs";
                if (a.IsComponentEnabled<DamageActive>(x) != b.IsComponentEnabled<DamageActive>(y)) return at + "Damage active bit differs";
                if (a.GetBuffer<LocomotionEntry>(x, true).Length != b.GetBuffer<LocomotionEntry>(y, true).Length) return at + "Locomotion entries differ";
                if (a.GetBuffer<LiftEntry>(x, true).Length != b.GetBuffer<LiftEntry>(y, true).Length) return at + "Lift entries differ";
                if (a.GetBuffer<StatChangeEntry>(x, true).Length != b.GetBuffer<StatChangeEntry>(y, true).Length) return at + "StatChange entries differ";
                if (a.GetBuffer<DamageEntry>(x, true).Length != b.GetBuffer<DamageEntry>(y, true).Length) return at + "Damage entries differ";
                var nx = TimelineTicks.Read(a, x, px, KindSlots.MaxSlots);
                var ny = TimelineTicks.Read(b, y, py, KindSlots.MaxSlots);
                if (nx != ny) return at + "dense has " + nx + " slots, explicit " + ny;
                for (var i = 0; i < nx; i++)
                {
                    var p = *(Playback*)(px + i);
                    var q = *(Playback*)(py + i);
                    if (p.Timeline != q.Timeline || p.Tick != q.Tick || p.Channel != q.Channel || p.Flags != q.Flags || p.Rate != q.Rate || p.Phase != q.Phase)
                        return at + "slot " + i + " dense " + Slot(in p, p.Tick) + ", explicit " + Slot(in q, q.Tick);
                }
            }
            return "";
        }

        [Test]
        public void DenseRowsMatchExplicitRowsEveryStepThroughEveryControl([Values] PureControl control, [Values] PureRows rows)
        {
            using var dense = new PureWorld(PureControls.Rows, _first, _second, false, rows, true);
            using var plain = new PureWorld(PureControls.Rows, _first, _second, false, rows, false);
            var both = new SameRequestsToBothWorlds { Dense = dense, Explicit = plain };
            var rng = PureControls.Seed;
            var second = false;
            var digest = global::Tl.TlStateHash.Begin();
            var denseRowSteps = 0;
            var denseWhileRated = 0;
            var steps = PureControls.StepsOf(control);
            for (var step = 0; step < steps; step++)
            {
                PureControls.Requests(control, step, ref rng, both);
                if (PureControls.SecondLibrary(control, step) != second)
                {
                    second = !second;
                    dense.HotSwapLibrary(second);
                    plain.HotSwapLibrary(second);
                }
                var forward = PureControls.Forward(control, step);
                dense.Step(forward);
                plain.Step(forward);
                var differs = FirstDifference(dense, plain, step);
                if (differs.Length != 0)
                    Assert.Fail(control + " with " + rows + " rows: the dense world left the explicit one at " + differs);
                digest.Lane(dense.Hash());
                var denseRows = dense.DenseRows();
                denseRowSteps += denseRows;
                if (control == PureControl.Rates && step >= 3) denseWhileRated += denseRows;
            }
            Assert.AreEqual(0, plain.DenseRows(), "the unfused world must never run dense");
            UnityEngine.Debug.Log("TlDenseOracle: " + control + " " + rows + " denseRowSteps=" + denseRowSteps + " of " + PureControls.Rows * steps);
            Assert.AreEqual(PrecombineControlTests.DotNetDigest(control).ToString("X16"), digest.Value.ToString("X16"), control + ": the dense world's per-step state hashes differ from the .NET host's");
            if (rows == PureRows.Lazy) Assert.AreEqual(0, denseRowSteps, "lazy rows (TimelineClock) are opt-in and stay off the dense path");
            else
            {
                var dotNet = DotNetDenseRowSteps(control);
                var least = control == PureControl.Rates ? dotNet / 2 : Math.Max(PureControls.Rows * steps / 10, dotNet / 2);
                Assert.GreaterOrEqual(denseRowSteps, least, control + " with " + rows + " rows: only " + denseRowSteps + " of " + PureControls.Rows * steps + " row-steps ran dense; the .NET host runs " + dotNet + " of them dense");
                if (control == PureControl.Rates) Assert.AreEqual(0, denseWhileRated, "Rates: a row ran dense while one of its playbacks was rated");
            }
        }

        static int DotNetDenseRowSteps(PureControl control)
        {
            switch (control)
            {
                case PureControl.PauseResume: return 2940;
                case PureControl.Reverse: return 3738;
                case PureControl.WorldRewind: return 1907;
                case PureControl.Rates: return 48;
                case PureControl.Seek: return 2070;
                case PureControl.Handover: return 3760;
                case PureControl.Loop: return 3656;
                case PureControl.Stop: return 3006;
                case PureControl.Republish: return 2588;
                default: return 856;
            }
        }

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        static void RandomControl(IPureRequests both, int row, uint roll, int timelines)
        {
            var timeline = (ushort)(1 + (roll >> 8) % (uint)timelines);
            var at = (ushort)((roll >> 16) % 40);
            switch (roll & 31)
            {
                case 6: case 7: case 8: both.Request(row, global::Tl.RequestOp.Pause, timeline, 0); return;
                case 9: case 10: case 11: both.Request(row, global::Tl.RequestOp.Resume, timeline, 0); return;
                case 12: case 13: case 14: both.Request(row, global::Tl.RequestOp.Reverse, timeline, (ushort)(at & 1)); return;
                case 15: case 16: both.Request(row, global::Tl.RequestOp.Speed, timeline, 32); return;
                case 17: both.Request(row, global::Tl.RequestOp.Speed, timeline, 8); return;
                case 18: case 19: both.Request(row, global::Tl.RequestOp.Speed, timeline, 16); return;
                case 20: case 21: case 22: both.Request(row, global::Tl.RequestOp.Seek, timeline, at); return;
                case 23: case 24: case 25: both.Request(row, global::Tl.RequestOp.Stop, timeline, 0); return;
                case 26: both.Request(row, global::Tl.RequestOp.StopAll, 0, 0); return;
                case 27: both.Request(row, global::Tl.RequestOp.Reset, 0, 0); return;
                default: both.Request(row, global::Tl.RequestOp.Start, timeline, at); return;
            }
        }

        [Test]
        public void DenseRowsMatchExplicitRowsUnderARandomScriptWithRewindsReloadAndSkippedVitals([Values] PureRows rows, [Values(4, 7, 24)] int slots)
        {
            const int Rows = 48;
            const int Steps = 600;
            const int Calm = 9;
            var wideRows = slots > 16;
            var timelines = wideRows ? 40 : 5;
            using var dense = new PureWorld(Rows, wideRows ? _shapeFirst : _first, wideRows ? _shapeSecond : _second, false, rows, true);
            using var plain = new PureWorld(Rows, wideRows ? _shapeFirst : _first, wideRows ? _shapeSecond : _second, false, rows, false);
            var both = new SameRequestsToBothWorlds { Dense = dense, Explicit = plain };
            var rng = 0xC0FFEE12u;
            for (var r = 0; r < Rows; r++)
                for (var k = 1 + (int)(Next(ref rng) % (uint)slots); k > 0; k--)
                    both.Request(r, global::Tl.RequestOp.Start, (ushort)(1 + Next(ref rng) % (uint)timelines), (ushort)(Next(ref rng) % 40));
            var forward = true;
            var second = false;
            int rewinds = 0, controls = 0, skipped = 0, denseRowSteps = 0, spilled = 0, wide = 0;
            for (var step = 0; step < Steps; step++)
            {
                if (step % 97 == 41 || Next(ref rng) % 211 == 0)
                {
                    forward = !forward;
                    rewinds++;
                }
                if (step == Steps / 2 || step == Steps * 3 / 4)
                {
                    second = !second;
                    dense.HotSwapLibrary(second);
                    plain.HotSwapLibrary(second);
                }
                for (var r = 0; r < Rows; r++)
                {
                    if (Next(ref rng) % Calm != 0) continue;
                    RandomControl(both, r, Next(ref rng), timelines);
                    controls++;
                }
                var vitals = step % 23 is < 5 or > 10;
                if (!vitals) skipped++;
                dense.Step(forward, vitals);
                plain.Step(forward, vitals);
                var differs = FirstDifference(dense, plain, step);
                if (differs.Length != 0)
                    Assert.Fail(rows + " rows, " + slots + " slots: the dense world left the explicit one at " + differs);
                denseRowSteps += dense.DenseRows();
                spilled += dense.SpilledRows();
                wide += dense.DenseRowsWiderThan(16);
            }
            UnityEngine.Debug.Log("TlDenseOracle: random " + rows + " slots=" + slots + " denseRowSteps=" + denseRowSteps + " of " + Rows * Steps + " rewinds=" + rewinds + " controls=" + controls + " skippedVitals=" + skipped + " spilledRowSteps=" + spilled + " denseRowStepsOver16Slots=" + wide);
            Assert.AreEqual(0, plain.DenseRows(), "the unfused world must never run dense");
            if (rows == PureRows.Lazy) Assert.AreEqual(0, denseRowSteps, "lazy rows (TimelineClock) are opt-in and stay off the dense path");
            else Assert.Greater(denseRowSteps * 10, Rows * Steps, "only " + denseRowSteps + " of " + Rows * Steps + " row-steps ran dense (the .NET random oracle requires 10%)");
            Assert.Greater(rewinds, 4, "the script rewound the world too rarely");
            Assert.Greater(controls, Rows * 4, "the script sent too few controls");
            Assert.Greater(skipped, 0, "the script never skipped the Vitals system");
            if (slots > 4) Assert.Greater(spilled, 0, "no row spilled past the four inline playback slots");
            if (slots > 16 && rows != PureRows.Lazy) Assert.Greater(wide, 0, "no dense row held more than 16 slots, so the wide due scan never ran");
        }

        [Test]
        public void AConsumerThatSkipsStepsStillGetsEveryTimelineAndGamePulseExactlyOnce()
        {
            const int Rows = 24, Steps = 120, Skipped = Steps / 4;
            using var dense = new PureWorld(Rows, _first, _second, false, PureRows.Eager, true);
            using var plain = new PureWorld(Rows, _first, _second, false, PureRows.Eager, false);
            using var every = new PureWorld(Rows, _first, _second, false, PureRows.Eager, true);
            foreach (var world in new[] { dense, plain, every })
                for (var row = 0; row < Rows; row++)
                    world.Start(row, PureControls.Loop, (ushort)(row % 7));
            var denseRowSteps = 0;
            for (var step = 0; step < Steps; step++)
            {
                var skip = step % 4 == 1;
                if (skip)
                    foreach (var world in new[] { dense, plain, every })
                        for (var row = 0; row < Rows; row++)
                        {
                            world.Damage(row, 7);
                            world.Damage(row, 11);
                            world.Stat(row, 100 + row);
                        }
                dense.Step(true, !skip);
                plain.Step(true, !skip);
                every.Step(true, true);
                var differs = FirstDifference(dense, plain, step);
                if (differs.Length != 0)
                    Assert.Fail("step " + step + ": the dense world left the explicit one at " + differs);
                denseRowSteps += dense.DenseRows();
                if (skip) continue;
                differs = FirstDifference(dense, every, step);
                if (differs.Length != 0)
                    Assert.Fail("step " + step + ": a consumer that skipped steps read other pulses than one that ran every step, at " + differs);
            }
            UnityEngine.Debug.Log("TlDenseOracle: skippedConsumer denseRowSteps=" + denseRowSteps + " of " + Rows * Steps);
            Assert.GreaterOrEqual(denseRowSteps * 5, Rows * Steps * 2, "only " + denseRowSteps + " of " + Rows * Steps + " row-steps ran dense while the consumer skipped");
            for (var row = 0; row < Rows; row++)
                Assert.Less(Health(every, row), 1_000_000 - Skipped * 18, "row " + row + " never received a timeline hit next to its game pulses");
        }

        [Test]
        public void SkippingTheFusedScheduleKeepsEveryPulseAcrossWrapsAndRewinds()
        {
            const int rows = 24, steps = 120;
            using var dense = new PureWorld(rows, _first, true);
            using var plain = new PureWorld(rows, _first, false);
            for (var row = 0; row < rows; row++)
            {
                dense.Start(row, PureControls.Loop, (ushort)row);
                plain.Start(row, PureControls.Loop, (ushort)row);
            }
            for (var step = 0; step < steps; step++)
            {
                var run = step % 7 < 5 || step == steps - 1;
                var forward = step % 11 < 8;
                dense.Step(forward, true, run);
                plain.Step(forward, true, run);
                if (run) Assert.AreEqual("", FirstDifference(dense, plain, step));
                if (step == 4) Assert.Greater(dense.DenseRows(), 0);
            }
        }

        [Test]
        public unsafe void FlushedHitsKeepTheExplicitEntryOrderWhenTheConsumerSkipsAStep()
        {
            using var dense = new PureWorld(1, _first, true);
            dense.Start(0, PureControls.Loop, 9);
            dense.Step(true);
            dense.Step(true);
            dense.Step(true);
            var slot = default(global::Tl.Playback);
            var entity = dense.Rows[0];
            TimelineTicks.Read(dense.Entities, entity, &slot, 1);
            Assert.AreEqual(11, slot.Tick);
            Assert.AreEqual(1, dense.DenseRows());

            dense.Step(true, true, false);
            AppendStatPulse(dense, 17);
            AppendStatPulse(dense, -17);
            dense.Step(true, true, false);

            var entries = dense.Entities.GetBuffer<StatChangeEntry>(entity, true);
            Assert.AreEqual(3, entries.Length);
            var bytes = (byte*)entries.GetUnsafeReadOnlyPtr();
            Assert.AreEqual(3, *(int*)(bytes + 8));
            Assert.AreEqual(17, *(int*)(bytes + sizeof(StatChangeEntry) + 8));
            Assert.AreEqual(-17, *(int*)(bytes + 2 * sizeof(StatChangeEntry) + 8));
        }

        [Test]
        public void PendingPulseEntriesKeepAnEligibleRowOnTheExplicitPath()
        {
            using var pending = new PureWorld(1, _first, true);
            using var empty = new PureWorld(1, _first, true);
            foreach (var world in new[] { pending, empty })
            {
                world.Start(0, PureControls.Loop, 5);
                world.Step(true);
                world.Step(true);
                Assert.AreEqual(0, world.DenseRows());
            }

            AppendStatPulse(pending, 17);
            pending.Step(true);
            empty.Step(true);

            Assert.AreEqual(0, pending.DenseRows());
            Assert.AreEqual(1, empty.DenseRows());
        }

        unsafe void AppendStatPulse(PureWorld world, int value)
        {
            var entity = world.Rows[0];
            var entries = world.Entities.GetBuffer<StatChangeEntry>(entity);
            entries.Add(new StatChangeEntry { Flags = global::Tl.KindEntryFlags.Pulse });
            *(int*)((byte*)entries.GetUnsafePtr() + (entries.Length - 1) * sizeof(StatChangeEntry) + 8) = value;
            world.Entities.SetComponentEnabled<StatChangeActive>(entity, true);
        }

        [Test]
        public void EqualPrioritiesGoToTheLaterSlotAcrossAFreedAndReusedSlot()
        {
            const int Rows = 24, Steps = 40;
            using var dense = new PureWorld(Rows, _first, _second, false, PureRows.Eager, true);
            using var plain = new PureWorld(Rows, _first, _second, false, PureRows.Eager, false);
            using var walk = new PureWorld(Rows, _first, _second, true, PureRows.Eager);
            var worlds = new[] { dense, plain, walk };
            var lastX = new float[Rows];
            var denseRowSteps = 0;
            for (var step = 0; step < Steps; step++)
            {
                foreach (var world in worlds)
                    for (var row = 0; row < Rows; row++)
                    {
                        if (step == 0) world.Request(row, global::Tl.RequestOp.Start, PureControls.Loop, 0);
                        if (step == 0 || step == 20)
                        {
                            world.Request(row, global::Tl.RequestOp.Start, PureControls.Walk, 20);
                            world.Request(row, global::Tl.RequestOp.Reverse, PureControls.Walk, 1);
                        }
                        if (step == 10) world.Request(row, global::Tl.RequestOp.Stop, PureControls.Walk, 0);
                        if (step == 21) world.Request(row, global::Tl.RequestOp.Stop, PureControls.Loop, 0);
                        if (step == 26) world.Request(row, global::Tl.RequestOp.Start, PureControls.Loop, 6);
                    }
                foreach (var world in worlds) world.Step(true);
                var differs = FirstDifference(dense, plain, step);
                if (differs.Length != 0)
                    Assert.Fail("step " + step + ": the dense world left the explicit one at " + differs);
                Assert.AreEqual(walk.Hash(), dense.Hash(), "step " + step + ": the dense world left the span walk");
                denseRowSteps += dense.DenseRows();
                var tie = step is >= 1 and <= 3 ? -1f : (step is >= 5 and <= 9) || step == 26 ? -3f : 0f;
                for (var row = 0; row < Rows; row++)
                {
                    var x = X(dense, row);
                    if (tie != 0f) Assert.AreEqual(tie, x - lastX[row], "step " + step + " row " + row + ": equal priorities must go to the later slot");
                    lastX[row] = x;
                }
            }
            UnityEngine.Debug.Log("TlDenseOracle: equalPriorities denseRowSteps=" + denseRowSteps + " of " + Rows * Steps);
            Assert.Greater(denseRowSteps * 2, Rows * Steps, "only " + denseRowSteps + " of " + Rows * Steps + " row-steps ran dense, so the vector Pick never met a tie");
        }

        static int Health(PureWorld world, int row) => world.Entities.GetComponentData<Vitals_HealthData>(world.Rows[row]).Value.Value;

        static float X(PureWorld world, int row) => world.Entities.GetComponentData<Movement_MotionData>(world.Rows[row]).Value.X;
    }
}
