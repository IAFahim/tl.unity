using NUnit.Framework;
using Unity.Collections;

namespace Tl.Unity.Tests
{
    [TestFixture]
    public class TimelineEventsTests
    {
        struct Cue
        {
            public int Kind;
        }

        [Test]
        public void Drain_SortsByEntitySlotSequence()
        {
            var sink = new TimelineEventSink<Cue>(8, Allocator.Temp);
            try
            {
                sink.Bind(2, 0);
                sink.Raise(new Cue { Kind = 1 });
                sink.Bind(0, 1);
                sink.Raise(new Cue { Kind = 2 });
                sink.Bind(0, 0);
                sink.Raise(new Cue { Kind = 3 });
                sink.Raise(new Cue { Kind = 4 });
                var drained = TimelineEvents.Drain(ref sink, Allocator.Temp);
                try
                {
                    Assert.AreEqual(4, drained.Length);
                    Assert.AreEqual(0, drained[0].Entity.Index);
                    Assert.AreEqual(0, drained[0].Slot);
                    Assert.AreEqual(3, drained[0].Value.Kind);
                    Assert.AreEqual(0, drained[1].Slot);
                    Assert.AreEqual(4, drained[1].Value.Kind);
                    Assert.AreEqual(1, drained[2].Slot);
                    Assert.AreEqual(2, drained[3].Entity.Index);
                }
                finally
                {
                    drained.Dispose();
                }
            }
            finally
            {
                sink.Dispose();
            }
        }

        [Test]
        public void Drain_ClearsSink()
        {
            var sink = new TimelineEventSink<Cue>(4, Allocator.Temp);
            try
            {
                sink.Bind(0, 0);
                sink.Raise(new Cue { Kind = 7 });
                var first = TimelineEvents.Drain(ref sink, Allocator.Temp);
                try
                {
                    Assert.AreEqual(1, first.Length);
                }
                finally
                {
                    first.Dispose();
                }
                var second = TimelineEvents.Drain(ref sink, Allocator.Temp);
                try
                {
                    Assert.AreEqual(0, second.Length);
                }
                finally
                {
                    second.Dispose();
                }
            }
            finally
            {
                sink.Dispose();
            }
        }
    }
}
