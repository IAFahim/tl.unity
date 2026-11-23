using NUnit.Framework;
using Tl.Unity;

namespace KindPure
{
    [TestFixture]
    public class PrecombineControlTests
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
            public PureWorld Precombined;
            public PureWorld SpanWalk;

            public void Request(int row, global::Tl.RequestOp op, ushort timeline, ushort at)
            {
                Precombined.Request(row, op, timeline, at);
                SpanWalk.Request(row, op, timeline, at);
            }
        }

        struct WhatTheScriptReached
        {
            public global::Tl.PlaybackFlags Flags;
            public bool Rated;
            public bool SampledCopies;
            public int Timelines;
            public int MostListed;

            public void Add(PureWorld world)
            {
                foreach (var row in world.Rows)
                {
                    var slots = world.Entities.GetBuffer<Playback>(row, true);
                    var listed = 0;
                    for (var i = 0; i < slots.Length; i++)
                    {
                        if (slots[i].Timeline == 0) continue;
                        Flags |= slots[i].Flags;
                        Rated |= slots[i].Rate != 0;
                        Timelines |= 1 << slots[i].Timeline;
                        if (global::Tl.KindScore.Listed(slots[i].Flags)) listed++;
                    }
                    SampledCopies |= world.Entities.GetComponentData<TimelinePlaying>(row).Copies != 0;
                    if (listed > MostListed) MostListed = listed;
                }
            }
        }

        internal static ulong DotNetDigest(PureControl control)
        {
            switch (control)
            {
                case PureControl.PauseResume: return 0x82D306A901444EE5UL;
                case PureControl.Reverse: return 0x21169E0A2D902970UL;
                case PureControl.WorldRewind: return 0xCE0C9000F350DCEAUL;
                case PureControl.Rates: return 0x6E94BB8ED2D6D448UL;
                case PureControl.Seek: return 0x353C6F3E47477A92UL;
                case PureControl.Handover: return 0xCCDA47EB590B8F13UL;
                case PureControl.Loop: return 0x9E4C223A745CCF3BUL;
                case PureControl.Stop: return 0x877438935C504D2DUL;
                case PureControl.Republish: return 0xC5E32D459217B66AUL;
                default: return 0x23D8BA11791E0662UL;
            }
        }

        [Test]
        public void PrecombinedRowsMatchTheSpanWalkEveryStepAndTheDotNetDigest([Values] PureControl control, [Values] PureRows rows)
        {
            using var precombined = new PureWorld(PureControls.Rows, _first, _second, false, rows);
            using var spanWalk = new PureWorld(PureControls.Rows, _first, _second, true, PureRows.Eager);
            var both = new SameRequestsToBothWorlds { Precombined = precombined, SpanWalk = spanWalk };
            var rng = PureControls.Seed;
            var second = false;
            var digest = global::Tl.TlStateHash.Begin();
            var reached = default(WhatTheScriptReached);
            var steps = PureControls.StepsOf(control);
            for (var step = 0; step < steps; step++)
            {
                PureControls.Requests(control, step, ref rng, both);
                if (PureControls.SecondLibrary(control, step) != second)
                {
                    second = !second;
                    precombined.HotSwapLibrary(second);
                    spanWalk.HotSwapLibrary(second);
                }
                var forward = PureControls.Forward(control, step);
                precombined.Step(forward);
                spanWalk.Step(forward);
                var expected = spanWalk.Hash();
                var actual = precombined.Hash();
                if (expected != actual)
                    Assert.Fail(control + " with " + rows + " rows: the precombined path first differs from the span walk at step " + step + " (" + actual.ToString("X16") + " vs " + expected.ToString("X16") + ").");
                digest.Lane(actual);
                reached.Add(precombined);
            }
            Assert.IsTrue(precombined.Precombined("KindPure.Locomotion") && precombined.Precombined("KindPure.Lift"), "Locomotion and Lift must read precombined tables in the fast world");
            Assert.IsFalse(spanWalk.Precombined("KindPure.Locomotion") || spanWalk.Precombined("KindPure.Lift"), "the reference world must walk spans and never build a table");
            Assert.AreEqual(DotNetDigest(control).ToString("X16"), digest.Value.ToString("X16"), control + ": the per-step state hashes differ from the .NET host's");
            Assert.GreaterOrEqual(reached.MostListed, 2, "the script never kept two listed playbacks on one row");
            switch (control)
            {
                case PureControl.PauseResume:
                    Assert.IsTrue((reached.Flags & global::Tl.PlaybackFlags.Paused) != 0, "no playback was ever paused");
                    break;
                case PureControl.Reverse:
                case PureControl.Loop:
                    Assert.IsTrue((reached.Flags & global::Tl.PlaybackFlags.Reverse) != 0, "no playback ever ran in reverse");
                    break;
                case PureControl.Rates:
                    Assert.IsTrue(reached.Rated && reached.SampledCopies, "no playback ever ran at a rate with sampled copies");
                    break;
                case PureControl.Handover:
                    Assert.IsTrue((reached.Timelines & (1 << PureControls.Walk)) != 0, "no arc playback ever handed over to walk, the only way walk starts in this script");
                    break;
                case PureControl.Stop:
                    Assert.IsTrue((reached.Flags & global::Tl.PlaybackFlags.Stopping) != 0, "no playback was ever stopping");
                    break;
                case PureControl.Seek:
                case PureControl.WorldRewind:
                    Assert.IsTrue((reached.Flags & global::Tl.PlaybackFlags.Ended) != 0, "no playback ever held or ended");
                    break;
                case PureControl.Mixed:
                    Assert.GreaterOrEqual(reached.MostListed, 3, "the random script kept fewer than three listed playbacks on a row");
                    break;
            }
        }
    }
}
