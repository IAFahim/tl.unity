using NUnit.Framework;
using Tl.Unity;
using Unity.Entities;

namespace KindPure
{
    [TestFixture]
    public class DenseOracleTests
    {
        byte[] _first;
        byte[] _second;

        [OneTimeSetUp]
        public void BakeBothControlLibrariesOnce()
        {
            _first = PureControls.Library(false);
            _second = PureControls.Library(true);
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
            var aClock = TimelineTicks.DenseClock(a);
            var bClock = TimelineTicks.DenseClock(b);
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
                var ax = TimelineTicks.ClockOf(a, x, aClock);
                var by = TimelineTicks.ClockOf(b, y, bClock);
                var sx = a.GetBuffer<Playback>(x, true);
                var sy = b.GetBuffer<Playback>(y, true);
                if (sx.Length != sy.Length) return at + "dense has " + sx.Length + " slots, explicit " + sy.Length;
                for (var i = 0; i < sx.Length; i++)
                {
                    var p = sx[i];
                    var q = sy[i];
                    var tp = TimelineTicks.Real(in p, ax);
                    var tq = TimelineTicks.Real(in q, by);
                    if (p.Timeline != q.Timeline || tp != tq || p.Channel != q.Channel || p.Flags != q.Flags || p.Rate != q.Rate || p.Phase != q.Phase)
                        return at + "slot " + i + " dense " + Slot(in p, tp) + ", explicit " + Slot(in q, tq);
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
                denseRowSteps += dense.DenseRows();
            }
            Assert.AreEqual(0, plain.DenseRows(), "the explicit world has no dense components and must never run dense");
            UnityEngine.Debug.Log("TlDenseOracle: " + control + " " + rows + " denseRowSteps=" + denseRowSteps + " of " + PureControls.Rows * steps);
            Assert.Greater(denseRowSteps * (control == PureControl.Rates ? 100 : 10), PureControls.Rows * steps, control + " with " + rows + " rows: only " + denseRowSteps + " of " + PureControls.Rows * steps + " row-steps ran dense");
            Assert.AreEqual(PrecombineControlTests.DotNetDigest(control).ToString("X16"), digest.Value.ToString("X16"), control + ": the dense world's per-step state hashes differ from the .NET host's");
        }

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        static void RandomControl(IPureRequests both, int row, uint roll)
        {
            var timeline = (ushort)(1 + (roll >> 8) % 5);
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
        public void DenseRowsMatchExplicitRowsUnderARandomScriptWithRewindsReloadAndSkippedVitals([Values] PureRows rows, [Values(4, 7)] int slots)
        {
            const int Rows = 48;
            const int Steps = 600;
            const int Calm = 9;
            using var dense = new PureWorld(Rows, _first, _second, false, rows, true);
            using var plain = new PureWorld(Rows, _first, _second, false, rows, false);
            var both = new SameRequestsToBothWorlds { Dense = dense, Explicit = plain };
            var rng = 0xC0FFEE12u;
            for (var r = 0; r < Rows; r++)
                for (var k = 1 + (int)(Next(ref rng) % (uint)slots); k > 0; k--)
                    both.Request(r, global::Tl.RequestOp.Start, (ushort)(1 + Next(ref rng) % 5), (ushort)(Next(ref rng) % 40));
            var forward = true;
            var second = false;
            int rewinds = 0, controls = 0, skipped = 0, denseRowSteps = 0, spilled = 0;
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
                    RandomControl(both, r, Next(ref rng));
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
            }
            UnityEngine.Debug.Log("TlDenseOracle: random " + rows + " slots=" + slots + " denseRowSteps=" + denseRowSteps + " of " + Rows * Steps + " rewinds=" + rewinds + " controls=" + controls + " skippedVitals=" + skipped + " spilledRowSteps=" + spilled);
            Assert.AreEqual(0, plain.DenseRows(), "the explicit world has no dense components and must never run dense");
            Assert.Greater(denseRowSteps * 10, Rows * Steps, "only " + denseRowSteps + " of " + Rows * Steps + " row-steps ran dense");
            Assert.Greater(rewinds, 4, "the script rewound the world too rarely");
            Assert.Greater(controls, Rows * 4, "the script sent too few controls");
            Assert.Greater(skipped, 0, "the script never skipped the Vitals system");
            if (slots > 4) Assert.Greater(spilled, 0, "no row spilled past the four inline playback slots");
        }
    }
}
