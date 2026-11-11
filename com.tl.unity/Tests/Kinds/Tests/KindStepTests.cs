using NUnit.Framework;
using Tl.Unity;

namespace KindGame
{
    [TestFixture]
    public unsafe class KindStepTests
    {
        [Test]
        public void StepInWindowMatchesTheStageSearchOnEveryTick()
        {
            using (var game = new KindWorld(1))
            {
                using var query = game.Entities.CreateEntityQuery(typeof(TimelineLibrary));
                var index = query.GetSingleton<TimelineLibrary>().Kinds;
                var direct = 0;
                var searched = 0;
                for (var t = 0; t < index.TimelineCount; t++)
                {
                    var timeline = (ushort)(t + 1);
                    var windows = index.WindowBase[t + 1] - index.WindowBase[t];
                    var lastTick = (int)System.Math.Min(ushort.MaxValue, index.Timelines[t].Duration + 1);
                    for (var window = 0; window < windows; window++)
                    {
                        if (index.WindowStep[index.WindowBase[t] + window] != null) direct++;
                        else searched++;
                        for (var tick = 0; tick <= lastTick; tick++)
                        {
                            var expected = index.StepOf(timeline, window, (ushort)tick);
                            if (expected != null && (tick < expected->WindowStart || tick >= expected->WindowEnd)) expected = null;
                            var actual = index.StepInWindow(timeline, window, (ushort)tick);
                            if (actual != expected) Assert.Fail("timeline " + timeline + " window " + window + " tick " + tick);
                        }
                    }
                }
                Assert.Greater(direct, 0);
                Assert.Greater(direct + searched, 0);
            }
        }
    }
}
