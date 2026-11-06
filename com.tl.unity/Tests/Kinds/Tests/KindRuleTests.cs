using NUnit.Framework;
using Tl.Unity;
using Unity.Collections;
using Unity.Entities;

namespace KindGame
{
    [TestFixture]
    public class KindRuleTests
    {
        const int Rows = 4;

        KindWorld _game;

        [SetUp]
        public void SetUp()
        {
            _game = new KindWorld(Rows);
        }

        [TearDown]
        public void TearDown()
        {
            _game.Dispose();
        }

        void Steps(int count)
        {
            for (var i = 0; i < count; i++)
                _game.Step();
        }

        [Test]
        public void SpeedBoostOverridesTheNormalWalkByPriorityThenRunTakesOver()
        {
            _game.Start(0, KindWorld.Run);
            _game.Step();
            Assert.AreEqual(2f, _game.Motion(0).X);
            _game.Start(0, KindWorld.SpeedBoost);
            _game.Step();
            Assert.AreEqual(7f, _game.Motion(0).X);
            Assert.AreEqual(2, _game.Entries<LocomotionEntry>(0));
            Steps(19);
            Assert.AreEqual(102f, _game.Motion(0).X);
            _game.Step();
            Assert.AreEqual(104f, _game.Motion(0).X);
            Assert.AreEqual(1, _game.Entries<LocomotionEntry>(0));
        }

        [Test]
        public void LiftsAddUpAndStatChangePulsesFromClipsAndMarkersApplyEachOnce()
        {
            _game.Start(0, KindWorld.Jump);
            Steps(2);
            _game.Start(0, KindWorld.PotionHop);
            Steps(4);
            Assert.AreEqual(44f, _game.Motion(0).Y);
            Assert.AreEqual(-1, _game.Stamina(0));
            _game.Step();
            Assert.AreEqual(9, _game.Stamina(0));
            Steps(8);
            Assert.AreEqual(84f, _game.Motion(0).Y);
            Assert.AreEqual(12f, _game.Motion(0).X);
            Assert.AreEqual(9, _game.Stamina(0));
            var slots = _game.Slots(0);
            Assert.AreEqual(0, slots.Length > 0 ? slots[0].Timeline : 0);
        }

        [Test]
        public void BulletsAndTimelineHitsSumIntoOneHealthWritePerStep()
        {
            _game.Start(0, KindWorld.Stomp);
            _game.Step();
            _game.Damage(1, 7);
            _game.Step();
            Assert.AreEqual(993, _game.Health(1));
            _game.Damage(0, 10);
            _game.Damage(0, 10);
            _game.Step();
            Assert.AreEqual(955, _game.Health(0));
            Steps(2);
            Assert.AreEqual(955, _game.Health(0));
            _game.Step();
            Assert.AreEqual(945, _game.Health(0));
            Assert.AreEqual(1000, _game.Health(2));
            Assert.AreEqual(0, _game.Tick.DroppedPulses);
        }

        [Test]
        public void ADisabledKindRowSkipsItsEntriesUntilEnabledAgain()
        {
            _game.Start(0, KindWorld.Run);
            _game.Step();
            _game.Entities.SetComponentEnabled<LocomotionEnabled>(_game.Rows[0], false);
            Steps(3);
            Assert.AreEqual(2f, _game.Motion(0).X);
            Assert.AreEqual(1, _game.Entries<LocomotionEntry>(0));
            _game.Entities.SetComponentEnabled<LocomotionEnabled>(_game.Rows[0], true);
            _game.Step();
            Assert.AreEqual(4f, _game.Motion(0).X);
        }

        [Test]
        public void StoppingRemovesSpansAtOnceAndHoldsTheSlotForTwoTicks()
        {
            _game.Start(0, KindWorld.Run);
            Steps(3);
            Assert.AreEqual(6f, _game.Motion(0).X);
            _game.Stop(0, KindWorld.Run);
            _game.Step();
            Assert.AreEqual(6f, _game.Motion(0).X);
            Assert.AreEqual(0, _game.Entries<LocomotionEntry>(0));
            Assert.IsFalse(_game.Active<LocomotionActive>(0));
            Assert.AreNotEqual(0, (int)(_game.Slots(0)[0].Flags & global::Tl.PlaybackFlags.Stopping));
            Assert.AreEqual(KindWorld.Run, _game.Slots(0)[0].Timeline);
            _game.Step();
            Assert.AreEqual(0, _game.Slots(0)[0].Timeline);
            Assert.IsFalse(_game.Entities.IsComponentEnabled<TimelinePlaying>(_game.Rows[0]));
        }

        [Test]
        public void AFreedSlotIsReusedLowestFirstAndKeepsItsIndexForItsWholeLife()
        {
            _game.Start(0, KindWorld.Run);
            _game.Start(0, KindWorld.SpeedBoost);
            _game.Step();
            var slots = _game.Slots(0);
            Assert.AreEqual(KindWorld.Run, slots[0].Timeline);
            Assert.AreEqual(KindWorld.SpeedBoost, slots[1].Timeline);
            _game.Stop(0, KindWorld.Run);
            Steps(3);
            Assert.AreEqual(KindWorld.SpeedBoost, _game.Slots(0)[1].Timeline);
            Assert.AreEqual(0, _game.Slots(0)[0].Timeline);
            _game.Start(0, KindWorld.Jump);
            _game.Step();
            Assert.AreEqual(KindWorld.Jump, _game.Slots(0)[0].Timeline);
            Assert.AreEqual(KindWorld.SpeedBoost, _game.Slots(0)[1].Timeline);
        }

        [Test]
        public void StartingOnTheSameChannelStopsThePlaybackItReplaces()
        {
            _game.Start(0, KindWorld.Run);
            Steps(2);
            _game.Start(0, KindWorld.Jump);
            _game.Step();
            var slots = _game.Slots(0);
            Assert.AreNotEqual(0, (int)(slots[0].Flags & global::Tl.PlaybackFlags.Stopping));
            Assert.AreEqual(KindWorld.Jump, slots[1].Timeline);
            Assert.AreEqual(1, _game.Entries<LocomotionEntry>(0));
            Assert.AreEqual(1, _game.Entries<LiftEntry>(0));
        }

        [Test]
        public void ALoopWrapKeepsExactlyOneSpanEntryAndKeepsPlaying()
        {
            _game.Start(0, KindWorld.Run);
            Steps(45);
            Assert.AreEqual(1, _game.Entries<LocomotionEntry>(0));
            Assert.AreEqual(4, _game.Slots(0)[0].Tick);
            Assert.AreEqual(90f, _game.Motion(0).X);
        }

        [Test]
        public void AFiniteTimelineEndRemovesItsEntriesAndFreesTheSlot()
        {
            _game.Start(0, KindWorld.SpeedBoost);
            Steps(20);
            Assert.AreEqual(100f, _game.Motion(0).X);
            Assert.AreEqual(1, _game.Entries<LocomotionEntry>(0));
            _game.Step();
            Assert.AreEqual(100f, _game.Motion(0).X);
            Assert.AreEqual(0, _game.Entries<LocomotionEntry>(0));
            Assert.IsFalse(_game.Active<LocomotionActive>(0));
            Assert.AreEqual(0, _game.Slots(0)[0].Timeline);
        }

        [Test]
        public void APausedPlaybackKeepsItsTickAndContributesNothing()
        {
            _game.Start(0, KindWorld.Run);
            Steps(2);
            _game.Edit(0, 0, p => { p.Flags |= global::Tl.PlaybackFlags.Paused; return p; });
            Steps(3);
            Assert.AreEqual(4f, _game.Motion(0).X);
            Assert.AreEqual(1, _game.Slots(0)[0].Tick);
            _game.Edit(0, 0, p => { p.Flags &= ~global::Tl.PlaybackFlags.Paused; return p; });
            _game.Step();
            Assert.AreEqual(6f, _game.Motion(0).X);
        }

        [Test]
        public void ReversedStepsUndoWhatForwardStepsDid()
        {
            _game.Start(0, KindWorld.Jump);
            Steps(5);
            var up = _game.Motion(0).Y;
            Assert.AreEqual(5 * 6f, up);
            for (var i = 0; i < 4; i++)
                _game.Step(false);
            Assert.AreEqual(0, _game.Slots(0)[0].Tick);
            Assert.AreEqual(6f, _game.Motion(0).Y);
        }

        [Test]
        public void PulsesToAnEntityWithoutTheKindAreCountedAndDropped()
        {
            var bare = _game.Entities.CreateEntity();
            var sender = _game.Tick.BeginPulses(1);
            var writer = sender.Writer;
            writer.Begin(0);
            DamageKind.Pulses(writer).To(bare, 5);
            writer.End();
            _game.Step();
            Assert.AreEqual(1, _game.Tick.DroppedPulses);
            Assert.AreEqual(1000, _game.Health(0));
        }

        [Test]
        public void AStartWhoseKindsTheEntityCannotReachIsDroppedAndCounted()
        {
            var bare = _game.Entities.CreateEntity(typeof(Playback), typeof(TimelineRequest), typeof(TimelinePlaying));
            _game.Entities.GetBuffer<TimelineRequest>(bare).Add(TimelineRequest.Start(0, KindWorld.Run));
            _game.Step();
            Assert.AreEqual(1, _game.Tick.DroppedStarts);
            Assert.AreEqual(0, _game.Entities.GetBuffer<Playback>(bare).Length);
        }

        [Test]
        public void AnOverflowedBufferReturnsInlineAfterItsPulsesAreApplied()
        {
            var inline = DamageKind.InlineCapacity;
            for (var i = 0; i < inline + 4; i++)
                _game.Damage(0, 1);
            _game.TickHandle.Update(_game.World.Unmanaged);
            _game.Entities.CompleteAllTrackedJobs();
            Assert.Greater(_game.Entities.GetBuffer<DamageEntry>(_game.Rows[0]).Capacity, inline);
            _game.VitalsHandle.Update(_game.World.Unmanaged);
            _game.Entities.CompleteAllTrackedJobs();
            Assert.AreEqual(inline, _game.Entities.GetBuffer<DamageEntry>(_game.Rows[0]).Capacity);
            Assert.AreEqual(1000 - (inline + 4), _game.Health(0));
        }

        [Test]
        public void AFeederReadsAnotherEntityThroughAComponentLookupContext()
        {
            var leader = _game.Entities.CreateEntity(typeof(Anchor));
            _game.Entities.SetComponentData(leader, new Anchor { Boost = 3f });
            _game.Entities.SetComponentData(_game.Rows[1], new Movement_TargetData { Value = new Target { Value = leader } });
            _game.Start(0, KindWorld.Run);
            _game.Start(1, KindWorld.Run);
            Steps(2);
            Assert.AreEqual(4f, _game.Motion(0).X);
            Assert.AreEqual(12f, _game.Motion(1).X);
        }

        [Test]
        public void ANextChainEntersTheNamedTimelineAtTickZero()
        {
            _game.Start(0, KindWorld.Vault);
            Steps(5);
            var slots = _game.Slots(0);
            Assert.AreEqual(KindWorld.Waltz, slots[0].Timeline);
            Assert.AreEqual(0, slots[0].Tick);
            Assert.AreEqual(43f, _game.Motion(0).X);
            Steps(3);
            slots = _game.Slots(0);
            Assert.AreEqual(KindWorld.Waltz, slots[0].Timeline);
            Assert.AreEqual(2, slots[0].Tick);
        }

        [Test]
        public void AHoldTimelineKeepsItsLastTickAndEndsQuietly()
        {
            _game.Start(0, KindWorld.Waltz);
            Steps(3);
            Assert.AreEqual(2, _game.Slots(0)[0].Tick);
            _game.Step();
            var slots = _game.Slots(0);
            Assert.AreEqual(KindWorld.Waltz, slots[0].Timeline);
            Assert.AreEqual(2, slots[0].Tick);
            Assert.AreNotEqual(0, (int)(slots[0].Flags & global::Tl.PlaybackFlags.Ended));
            Assert.AreEqual(0, _game.Entries<LocomotionEntry>(0));
            Assert.AreEqual(21f, _game.Motion(0).X);
            _game.Step();
            Assert.AreEqual(2, _game.Slots(0)[0].Tick);
            Assert.AreEqual(21f, _game.Motion(0).X);
        }
    }
}
