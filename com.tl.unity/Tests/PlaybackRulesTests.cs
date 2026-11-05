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

        static ushort RunId = 1;
        static ushort JumpId = 2;
        static ushort SlideId = 3;
        static ushort HoldId = 4;
        static ushort StackId = 5;

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

        [Test]
        public void Start_ThenIsPlaying()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                Assert.IsTrue(TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(RunId), 0, in _view, ref native));
                Assert.IsTrue(TimelinePlayer.IsPlaying(ref playbacks, new global::Tl.TimelineId(RunId)));
                Assert.IsFalse(TimelinePlayer.IsPlaying(ref playbacks, new global::Tl.TimelineId(JumpId)));
            }
        }

        [Test]
        public void ChannelReplace_ClosesReplaced()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                Assert.IsTrue(TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(RunId), 0, in _view, ref native));
                Assert.IsTrue(TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(JumpId), 0, in _view, ref native));
                Assert.IsFalse(TimelinePlayer.IsPlaying(ref playbacks, new global::Tl.TimelineId(RunId)));
                Assert.IsTrue(TimelinePlayer.IsPlaying(ref playbacks, new global::Tl.TimelineId(JumpId)));
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
                Assert.IsTrue(TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(StackId), 0, in _view, ref native));
                Assert.IsFalse(TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(StackId), 0, in _view, ref native));
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
                Assert.IsTrue(TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(SlideId), 5, in _view, ref native));
                Assert.IsTrue(TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(SlideId), 1, in _view, ref native));
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
                TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(RunId), 0, in _view, ref native);
                TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(SlideId), 0, in _view, ref native);
                Assert.IsTrue(TimelinePlayer.StopChannel(ref playbacks, _entity.Index, 2, ref native));
                Assert.IsFalse(TimelinePlayer.IsPlaying(ref playbacks, new global::Tl.TimelineId(SlideId)));
                Assert.IsTrue(TimelinePlayer.IsPlaying(ref playbacks, new global::Tl.TimelineId(RunId)));
                TimelinePlayer.StopAll(ref playbacks, _entity.Index, ref native);
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
                TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(SlideId), 3, in _view, ref native);
                Assert.IsTrue(TimelinePlayer.Find(ref playbacks, 2, out var found));
                Assert.AreEqual(SlideId, found.Timeline);
                Assert.AreEqual(3, found.Tick);
                Assert.IsFalse(TimelinePlayer.Find(ref playbacks, 1, out _));
            }
        }

        [Test]
        public void Advance_WrapsLoop()
        {
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(RunId), 9, in _view, ref native);
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
                TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(JumpId), 5, in _view, ref native);
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
                TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(HoldId), 3, in _view, ref native);
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
                TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(SlideId), 7, in _view, ref native);
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
                TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(RunId), 4, in _view, ref native);
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
                TimelinePlayer.Start(ref playbacks, _entity.Index, new global::Tl.TimelineId(RunId), 4, in _view, ref native);
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
            var items = new RequestEntry[4];
            items[0] = new RequestEntry { Entity = 2, Slot = 0, Sequence = 0 };
            items[1] = new RequestEntry { Entity = 0, Slot = 1, Sequence = 1 };
            items[2] = new RequestEntry { Entity = 0, Slot = 0, Sequence = 3 };
            items[3] = new RequestEntry { Entity = 0, Slot = 0, Sequence = 2 };
            fixed (RequestEntry* ptr = items)
            {
                TimelineKernel.SortRequests(ptr, items.Length);
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
            var playbacks = _entities.GetBuffer<Playback>(_entity);
            var slots = _entities.GetBuffer<TimelineSlot>(_entity);
            using (var closed = Closed())
            {
                var native = closed;
                TimelineSlots.Set(ref slots, 7, new global::Tl.TimelineId(JumpId));
                Assert.IsTrue(TimelineSlots.TryGetTimeline(ref slots, 7, out var id));
                Assert.AreEqual(JumpId, id.Value);
                Assert.IsTrue(TimelineSlots.StartFromSlot(ref playbacks, ref slots, _entity.Index, 7, 0, in _view, ref native));
                Assert.IsTrue(TimelinePlayer.IsPlaying(ref playbacks, new global::Tl.TimelineId(JumpId)));
                Assert.IsFalse(TimelineSlots.StartFromSlot(ref playbacks, ref slots, _entity.Index, 9, 0, in _view, ref native));
            }
        }
    }
}
