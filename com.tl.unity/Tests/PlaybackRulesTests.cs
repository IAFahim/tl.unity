using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity.Tests
{
    [TestFixture]
    public unsafe class PlaybackRulesTests
    {
        global::Tl.LibraryView _view;
        IntPtr _memory;
        World _world;
        EntityManager _entities;
        Entity _entity;

        const ushort RunId = 1;
        const ushort JumpId = 2;
        const ushort SlideId = 3;
        const ushort HoldId = 4;
        const ushort StackId = 5;

        [SetUp]
        public void SetUp()
        {
            _memory = Marshal.AllocHGlobal(5 * sizeof(global::Tl.LibraryTimeline));
            var timelines = (global::Tl.LibraryTimeline*)_memory;
            timelines[0] = new global::Tl.LibraryTimeline { Id = RunId, Channel = 1, Loop = 1, Duration = 10 };
            timelines[1] = new global::Tl.LibraryTimeline { Id = JumpId, Channel = 1, Next = RunId, Duration = 6 };
            timelines[2] = new global::Tl.LibraryTimeline { Id = SlideId, Channel = 2, Duration = 8 };
            timelines[3] = new global::Tl.LibraryTimeline { Id = HoldId, Channel = 3, Hold = 1, Duration = 4 };
            timelines[4] = new global::Tl.LibraryTimeline { Id = StackId, Duration = 4 };
            _view = new global::Tl.LibraryView { Version = 1, TimelineCount = 5, Timelines = timelines };
            _world = new World("TlPlaybackRulesTest");
            _entities = _world.EntityManager;
            _entity = _entities.CreateEntity(typeof(Playback), typeof(TimelineSlot), typeof(TimelineRequest));
        }

        [TearDown]
        public void TearDown()
        {
            if (_world != null && _world.IsCreated) _world.Dispose();
            _world = null;
            if (_memory != IntPtr.Zero) Marshal.FreeHGlobal(_memory);
            _memory = IntPtr.Zero;
        }

        NativeList<ClosedPlayback> Closed()
        {
            return new NativeList<ClosedPlayback>(8, Allocator.Temp);
        }

        PlaybackSlots Slots(NativeList<ClosedPlayback> closed)
        {
            return new PlaybackSlots(_entities.GetBuffer<Playback>(_entity), _entity.Index, closed);
        }

        bool Start(ref PlaybackSlots slots, ushort timeline, ushort at)
        {
            return global::Tl.PlaybackRules.Start(ref slots, _view.Timelines, _view.TimelineCount, timeline, at, out _);
        }

        [Test]
        public void Start_ThenIsPlaying()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Assert.IsTrue(Start(ref slots, RunId, 0));
                Assert.IsTrue(global::Tl.PlaybackRules.IsPlaying(ref slots, RunId));
                Assert.IsFalse(global::Tl.PlaybackRules.IsPlaying(ref slots, JumpId));
            }
        }

        [Test]
        public void ChannelReplace_ClosesReplaced()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Assert.IsTrue(Start(ref slots, RunId, 0));
                Assert.IsTrue(Start(ref slots, JumpId, 0));
                Assert.IsFalse(global::Tl.PlaybackRules.IsPlaying(ref slots, RunId));
                Assert.IsTrue(global::Tl.PlaybackRules.IsPlaying(ref slots, JumpId));
                Assert.AreEqual(1, native.Length);
                Assert.AreEqual(RunId, native[0].Value.Timeline);
            }
        }

        [Test]
        public void DuplicateStart_Rejected()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Assert.IsTrue(Start(ref slots, StackId, 0));
                Assert.IsFalse(Start(ref slots, StackId, 0));
                Assert.AreEqual(1, playbacks.Length);
                Assert.AreEqual(0, native.Length);
            }
        }

        [Test]
        public void SameChannelStart_RestartsInPlace()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Assert.IsTrue(Start(ref slots, SlideId, 5));
                Assert.IsTrue(Start(ref slots, SlideId, 1));
                Assert.AreEqual(1, playbacks.Length);
                Assert.AreEqual(1, native.Length);
                Assert.AreEqual(SlideId, native[0].Value.Timeline);
                Assert.AreEqual(1, playbacks[0].Tick);
            }
        }

        [Test]
        public void StopChannel_AndStopAll()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Start(ref slots, RunId, 0);
                Start(ref slots, SlideId, 0);
                Assert.IsTrue(global::Tl.PlaybackRules.StopChannel(ref slots, 2));
                Assert.IsFalse(global::Tl.PlaybackRules.IsPlaying(ref slots, SlideId));
                Assert.IsTrue(global::Tl.PlaybackRules.IsPlaying(ref slots, RunId));
                global::Tl.PlaybackRules.StopAll(ref slots);
                Assert.AreEqual(0, playbacks.Length);
                Assert.AreEqual(2, native.Length);
            }
        }

        [Test]
        public void Find_ReturnsChannelPlayback()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Start(ref slots, SlideId, 3);
                Assert.IsTrue(global::Tl.PlaybackRules.Find(ref slots, 2, out var found));
                Assert.AreEqual(SlideId, found.Timeline);
                Assert.AreEqual(3, found.Tick);
                Assert.IsFalse(global::Tl.PlaybackRules.Find(ref slots, 1, out _));
            }
        }

        [Test]
        public void Advance_WrapsLoop()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Start(ref slots, RunId, 9);
                var slot = playbacks[0];
                Assert.IsTrue(TimelineKernel.AdvancePlayback(ref slot, in _view, true, out var handover, out var removed));
                Assert.IsFalse(handover);
                Assert.IsFalse(removed);
                Assert.AreEqual(0, slot.Tick);
            }
        }

        [Test]
        public void Advance_NextHandover()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Start(ref slots, JumpId, 5);
                var slot = playbacks[0];
                Assert.IsTrue(TimelineKernel.AdvancePlayback(ref slot, in _view, true, out var handover, out var removed));
                Assert.IsTrue(handover);
                Assert.IsFalse(removed);
                Assert.AreEqual(RunId, slot.Timeline);
                Assert.AreEqual(0, slot.Tick);
                Assert.IsTrue((slot.Flags & global::Tl.PlaybackFlags.StartPending) != 0);
            }
        }

        [Test]
        public void Advance_HoldEndsAndStays()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Start(ref slots, HoldId, 3);
                var slot = playbacks[0];
                Assert.IsTrue(TimelineKernel.AdvancePlayback(ref slot, in _view, true, out _, out var removed));
                Assert.IsFalse(removed);
                Assert.IsTrue((slot.Flags & global::Tl.PlaybackFlags.Ended) != 0);
                Assert.AreEqual(3, slot.Tick);
                var frozen = slot;
                Assert.IsTrue(TimelineKernel.AdvancePlayback(ref slot, in _view, true, out _, out removed));
                Assert.IsFalse(removed);
                Assert.AreEqual(frozen.Tick, slot.Tick);
            }
        }

        [Test]
        public void Advance_FiniteEndRemovesWithoutClose()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Start(ref slots, SlideId, 7);
                var slot = playbacks[0];
                Assert.IsTrue(TimelineKernel.AdvancePlayback(ref slot, in _view, true, out _, out var removed));
                Assert.IsTrue(removed);
                Assert.AreEqual(0, native.Length);
            }
        }

        [Test]
        public void Advance_PausedStaysAndClearsPending()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Start(ref slots, RunId, 4);
                var slot = playbacks[0];
                slot.Flags |= global::Tl.PlaybackFlags.Paused | global::Tl.PlaybackFlags.StartPending;
                Assert.IsTrue(TimelineKernel.AdvancePlayback(ref slot, in _view, true, out _, out var removed));
                Assert.IsFalse(removed);
                Assert.AreEqual(4, slot.Tick);
                Assert.IsTrue((slot.Flags & global::Tl.PlaybackFlags.StartPending) == 0);
            }
        }

        [Test]
        public void Advance_ReverseStepsBackward()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                var slots = Slots(native);
                Start(ref slots, RunId, 4);
                var slot = playbacks[0];
                slot.Flags |= global::Tl.PlaybackFlags.Reverse;
                Assert.IsTrue(TimelineKernel.AdvancePlayback(ref slot, in _view, true, out _, out var removed));
                Assert.IsFalse(removed);
                Assert.AreEqual(3, slot.Tick);
            }
        }

        [Test]
        public void SortRequests_OrdersByEntitySlotSequence()
        {
            var items = new global::Tl.RequestRecord[4];
            items[0] = new global::Tl.RequestRecord(2, 0, 0, 0, global::Tl.RequestOp.Start, 0);
            items[1] = new global::Tl.RequestRecord(0, 1, 0, 0, global::Tl.RequestOp.Start, 1);
            items[2] = new global::Tl.RequestRecord(0, 0, 0, 0, global::Tl.RequestOp.Start, 3);
            items[3] = new global::Tl.RequestRecord(0, 0, 0, 0, global::Tl.RequestOp.Start, 2);
            fixed (global::Tl.RequestRecord* ptr = items)
            {
                global::Tl.RequestQueue.Sort(ptr, items.Length);
            }
            Assert.AreEqual(0, items[0].Entity);
            Assert.AreEqual(0, items[0].Slot);
            Assert.AreEqual(2, items[0].Sequence);
            Assert.AreEqual(3, items[1].Sequence);
            Assert.AreEqual(1, items[2].Slot);
            Assert.AreEqual(2, items[3].Entity);
        }

        [Test]
        public void Slots_StartFromSlot()
        {
            var keys = _entities.GetBuffer<TimelineSlot>(_entity);
            using (var closed = Closed())
            {
                var slots = Slots(closed);
                TimelineSlots.Set(ref keys, 7, new global::Tl.TimelineId(JumpId));
                Assert.IsTrue(TimelineSlots.TryGetTimeline(ref keys, 7, out var id));
                Assert.AreEqual(JumpId, id.Value);
                Assert.IsTrue(TimelineSlots.StartFromSlot(ref slots, ref keys, 7, 0, in _view));
                Assert.IsTrue(global::Tl.PlaybackRules.IsPlaying(ref slots, JumpId));
                Assert.IsFalse(TimelineSlots.StartFromSlot(ref slots, ref keys, 9, 0, in _view));
            }
        }
    }
}
