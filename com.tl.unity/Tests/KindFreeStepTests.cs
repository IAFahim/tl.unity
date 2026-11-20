using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Entities;

namespace Tl.Unity.Tests
{
    [TestFixture]
    public unsafe class KindFreeStepTests
    {
        const ushort Loop = 1;
        const ushort Stomp = 2;

        IntPtr _memory;
        World _world;
        EntityManager _entities;
        Entity _library;
        Entity _row;
        SystemHandle _requests;
        SystemHandle _advance;

        [SetUp]
        public void SetUp()
        {
            _memory = Marshal.AllocHGlobal(2 * sizeof(global::Tl.LibraryTimeline));
            var timelines = (global::Tl.LibraryTimeline*)_memory;
            timelines[0] = new global::Tl.LibraryTimeline { Id = Loop, Loop = 1, Duration = 40 };
            timelines[1] = new global::Tl.LibraryTimeline { Id = Stomp, Channel = 1, Duration = 30 };
            var view = new global::Tl.LibraryView { Version = 1, TimelineCount = 2, Timelines = timelines };
            _world = new World("TlKindFreeSteps");
            _entities = _world.EntityManager;
            _library = _entities.CreateEntity();
            _entities.AddComponentData(_library, new TimelineLibrary { View = view });
            _entities.AddComponentData(_library, new TimelineStep { Forward = true });
            _row = _entities.CreateEntity(typeof(Playback), typeof(TimelineRequest));
            _requests = _world.CreateSystem<TimelineRequestSystem>();
            _advance = _world.CreateSystem<TimelineAdvanceSystem>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_world != null && _world.IsCreated) _world.Dispose();
            _world = null;
            if (_memory != IntPtr.Zero) Marshal.FreeHGlobal(_memory);
            _memory = IntPtr.Zero;
        }

        void Steps(int count)
        {
            for (var i = 0; i < count; i++)
            {
                _requests.Update(_world.Unmanaged);
                _advance.Update(_world.Unmanaged);
                _entities.CompleteAllTrackedJobs();
            }
        }

        int TickOf(ushort timeline)
        {
            foreach (var playback in _entities.GetBuffer<Playback>(_row))
                if (playback.Timeline == timeline)
                    return playback.Tick;
            return -1;
        }

        [Test]
        public void AWorldRewindMovesKindFreePlaybacksBackward()
        {
            _entities.GetBuffer<TimelineRequest>(_row).Add(TimelineRequest.Start(-1, Loop));
            Steps(8);
            var forwardTick = TickOf(Loop);
            Assert.Greater(forwardTick, 3);
            _entities.SetComponentData(_library, new TimelineStep { Forward = false });
            Steps(3);
            Assert.AreEqual(forwardTick - 3, TickOf(Loop));
            _entities.SetComponentData(_library, new TimelineStep { Forward = true });
            Steps(2);
            Assert.AreEqual(forwardTick - 1, TickOf(Loop));
        }

        [Test]
        public void EveryVerbByIdAndResetReachTheKindFreeRules()
        {
            var requests = _entities.GetBuffer<TimelineRequest>(_row);
            requests.Start(Loop);
            requests.Start(Stomp, 4);
            Steps(1);
            Assert.AreEqual(1, TickOf(Loop));
            Assert.AreEqual(5, TickOf(Stomp));
            _entities.GetBuffer<TimelineRequest>(_row).Pause(Loop);
            Steps(2);
            Assert.AreEqual(1, TickOf(Loop));
            _entities.GetBuffer<TimelineRequest>(_row).Resume(Loop);
            Steps(1);
            Assert.AreEqual(2, TickOf(Loop));
            _entities.GetBuffer<TimelineRequest>(_row).Seek(Loop, 20);
            Steps(1);
            Assert.AreEqual(20, TickOf(Loop));
            _entities.GetBuffer<TimelineRequest>(_row).Reverse(Loop, true);
            Steps(1);
            Assert.AreEqual(19, TickOf(Loop));
            _entities.GetBuffer<TimelineRequest>(_row).Reverse(Loop, false);
            _entities.GetBuffer<TimelineRequest>(_row).SetSpeed(Loop, 32);
            Steps(1);
            Assert.AreEqual(21, TickOf(Loop));
            _entities.GetBuffer<TimelineRequest>(_row).Stop(Stomp);
            Steps(1);
            Assert.AreEqual(-1, TickOf(Stomp));
            _entities.GetBuffer<TimelineRequest>(_row).Reset();
            Steps(1);
            foreach (var playback in _entities.GetBuffer<Playback>(_row))
                Assert.AreEqual(0, playback.Timeline);
        }
    }
}
