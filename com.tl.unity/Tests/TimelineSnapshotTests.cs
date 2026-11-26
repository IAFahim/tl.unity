using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity.Tests
{
    [TestFixture]
    public unsafe class TimelineSnapshotTests
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
        TimelineSnapshotter _snapshotter;
        TimelineSnapshot _snapshot;

        [SetUp]
        public void SetUp()
        {
            _memory = Marshal.AllocHGlobal(2 * sizeof(global::Tl.LibraryTimeline));
            var timelines = (global::Tl.LibraryTimeline*)_memory;
            timelines[0] = new global::Tl.LibraryTimeline { Id = Loop, Loop = 1, Duration = 40 };
            timelines[1] = new global::Tl.LibraryTimeline { Id = Stomp, Channel = 1, Duration = 30 };
            var view = new global::Tl.LibraryView { Version = 1, TimelineCount = 2, Timelines = timelines };
            _world = new World("TlSnapshotTests");
            _entities = _world.EntityManager;
            _library = _entities.CreateEntity();
            _entities.AddComponentData(_library, new TimelineLibrary { View = view });
            _entities.AddComponentData(_library, new TimelineStep { Forward = true });
            _row = _entities.CreateEntity(typeof(Playback), typeof(TimelineRequest));
            _requests = _world.CreateSystem<TimelineRequestSystem>();
            _advance = _world.CreateSystem<TimelineAdvanceSystem>();
            _snapshot = TimelineSnapshot.Create(Allocator.Persistent, 1024);
        }

        [TearDown]
        public void TearDown()
        {
            _snapshotter.Dispose();
            _snapshot.Dispose();
            if (_world != null && _world.IsCreated) _world.Dispose();
            _world = null;
            if (_memory != IntPtr.Zero) Marshal.FreeHGlobal(_memory);
            _memory = IntPtr.Zero;
        }

        void StepOnce()
        {
            _requests.Update(_world.Unmanaged);
            _advance.Update(_world.Unmanaged);
            _entities.CompleteAllTrackedJobs();
        }

        List<Playback[]> RunTicks(int count)
        {
            var trace = new List<Playback[]>(count);
            for (var i = 0; i < count; i++)
            {
                StepOnce();
                trace.Add(_entities.GetBuffer<Playback>(_row).AsNativeArray().ToArray());
            }
            return trace;
        }

        [Test]
        public void KindFreeSnapshotAndRestoreMatchFirstRunBitForBitWithZeroWarmAllocations()
        {
            var requests = _entities.GetBuffer<TimelineRequest>(_row);
            requests.Start(Loop);
            requests.Start(Stomp, 4);

            RunTicks(12);
            Assert.IsTrue(_snapshotter.Capture(_entities, ref _snapshot));
            var expected = RunTicks(25);

            Assert.IsTrue(_snapshotter.Restore(_entities, in _snapshot));
            var actual = RunTicks(25);

            Assert.AreEqual(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.AreEqual(expected[i].Length, actual[i].Length, "Tick " + i + " slot count");
                for (var s = 0; s < expected[i].Length; s++)
                    Assert.AreEqual(expected[i][s], actual[i][s], "Tick " + i + " slot " + s);
            }

            long warmAllocated = 0;
            var allOk = true;
            for (var i = 0; i < 10; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var capOk = _snapshotter.Capture(_entities, ref _snapshot);
                StepOnce();
                var resOk = _snapshotter.Restore(_entities, in _snapshot);
                StepOnce();
                warmAllocated += GC.GetAllocatedBytesForCurrentThread() - before;
                allOk &= capOk && resOk;
            }
            Assert.IsTrue(allOk);
            Assert.AreEqual(0L, warmAllocated, "Warm Capture, Restore, and step allocate zero managed bytes");
        }
    }
}
