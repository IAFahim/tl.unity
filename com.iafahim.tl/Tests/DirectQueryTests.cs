using System;
using System.Collections.Generic;
using Fixtures;
using NUnit.Framework;
using Tl;
using Tl.Unity;
using Unity.Entities;
using UnityEngine;
using UnityRow = Tl.Unity.TimelineComponent;

namespace TlUnityQueries
{
    public readonly struct QueriedFrame : IEquatable<QueriedFrame>
    {
        public readonly int TrackCode;
        public readonly int ClipValue;
        public readonly uint TimelineTick;
        public readonly long Cycle;
        public readonly ushort TrackIndex;

        public QueriedFrame(int trackCode, int clipValue, uint timelineTick, long cycle, ushort trackIndex)
        {
            TrackCode = trackCode;
            ClipValue = clipValue;
            TimelineTick = timelineTick;
            Cycle = cycle;
            TrackIndex = trackIndex;
        }

        public bool Equals(QueriedFrame other) =>
            TrackCode == other.TrackCode && ClipValue == other.ClipValue && TimelineTick == other.TimelineTick
            && Cycle == other.Cycle && TrackIndex == other.TrackIndex;

        public override bool Equals(object obj) => obj is QueriedFrame other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(TrackCode, ClipValue, TimelineTick, Cycle, TrackIndex);

        public static bool operator ==(QueriedFrame left, QueriedFrame right) => left.Equals(right);

        public static bool operator !=(QueriedFrame left, QueriedFrame right) => !left.Equals(right);
    }

    [DisableAutoCreation]
    public partial struct TimelineQueryProbeSystem : ISystem
    {
        public static QueriedFrame[] Observed = new QueriedFrame[8];
        public static int Count;

        public void OnUpdate(ref SystemState state)
        {
            foreach (var timeline in SystemAPI.Query<RefRO<UnityRow>>())
            {
                foreach (var frame in TimelineEcs.Query<AlphaTrack, AlphaClip>(in timeline.ValueRO))
                {
                    Observed[Count++] = new QueriedFrame(
                        frame.Track.Code,
                        frame.Clip.Value,
                        frame.TimelineTick,
                        frame.Cycle,
                        frame.TrackIndex);
                }
            }
        }
    }
}

namespace Tl.Unity.Tests
{
    [TestFixture]
    public sealed class DirectQueryTests
    {
        private static readonly List<TimelineAsset> Loaded = [];

        private World _world;
        private TimelineSystemGroup _group;
        private SystemHandle _probe;
        private Entity _clock;
        private EntityArchetype _rowArchetype;

        [OneTimeSetUp]
        public void OneTimeSetUp() => TimelineConsumers.Reset();

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            foreach (var asset in Loaded) asset.Dispose();
            Loaded.Clear();
        }

        [SetUp]
        public void SetUp()
        {
            TimelineConsumers.Reset();
            _world = new World("TlDirectQuery");
            _group = TimelineEcs.CreateGroup(_world);
            _probe = _world.CreateSystem<TlUnityQueries.TimelineQueryProbeSystem>();
            _clock = _world.EntityManager.CreateEntity(typeof(TimelineClock));
            _rowArchetype = _world.EntityManager.CreateArchetype(typeof(UnityRow), typeof(Health));
        }

        [TearDown]
        public void TearDown() => _world.Dispose();

        private static TextAsset Fixture(string name)
        {
            var asset = Resources.Load<TextAsset>("TlFixtures/" + name);
            Assert.NotNull(asset, "missing fixture: " + name);
            return asset;
        }

        private static void ResetProbe()
        {
            TlUnityQueries.TimelineQueryProbeSystem.Count = 0;
            Array.Clear(TlUnityQueries.TimelineQueryProbeSystem.Observed, 0, TlUnityQueries.TimelineQueryProbeSystem.Observed.Length);
        }

        private static int ObservedCount => TlUnityQueries.TimelineQueryProbeSystem.Count;

        private static TlUnityQueries.QueriedFrame ObservedAt(int index) => TlUnityQueries.TimelineQueryProbeSystem.Observed[index];

        private void Tick(uint gameTick, int delta)
        {
            ResetProbe();
            _world.EntityManager.SetComponentData(_clock, new TimelineClock { GameTick = gameTick, Delta = delta });
            _group.Update();
            _probe.Update(_world.Unmanaged);
        }

        [Test]
        public void TypedQueryObservesTheCurrentStageAndNeverAdvances()
        {
            var asset = TimelineAsset.Load(Fixture("fixture_a").bytes);
            Loaded.Add(asset);
            var row = _world.EntityManager.CreateEntity(_rowArchetype);
            _world.EntityManager.SetComponentData(row, new UnityRow { Reference = asset.Reference });

            ResetProbe();
            Tick(100u, 1);
            Assert.AreEqual(0, ObservedCount, "gap position 1 has no frames");

            Tick(101u, 1);
            Assert.AreEqual(1, ObservedCount, "position 2 holds the 22 clip");
            Assert.AreEqual(new TlUnityQueries.QueriedFrame(5, 22, 2u, 0L, (ushort)0), ObservedAt(0));

            Tick(102u, 1);
            Assert.AreEqual(0, ObservedCount, "clamped completed position 3 has no frames");

            Tick(103u, -1);
            Assert.AreEqual(1, ObservedCount, "reverse re-entry observes position 2 again");
            Assert.AreEqual(new TlUnityQueries.QueriedFrame(5, 22, 2u, 0L, (ushort)0), ObservedAt(0));

            ResetProbe();
            _probe.Update(_world.Unmanaged);
            _probe.Update(_world.Unmanaged);
            Assert.AreEqual(2, ObservedCount, "two updates observe the same frame twice");
            Assert.AreEqual(new TlUnityQueries.QueriedFrame(5, 22, 2u, 0L, (ushort)0), ObservedAt(0));
            Assert.AreEqual(new TlUnityQueries.QueriedFrame(5, 22, 2u, 0L, (ushort)0), ObservedAt(1));

            Assert.AreEqual(2u, _world.EntityManager.GetComponentData<UnityRow>(row).Position, "queries leave the row position untouched");
        }
    }
}
