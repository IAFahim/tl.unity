using NUnit.Framework;
using Tl;
using Unity.Burst;

namespace KindGame
{
    // K3 Burst-compile receipts: every game-code path the generated Burst chunk
    // jobs call must compile under Burst. Each shim is forced through the Burst
    // backend synchronously by CompileFunctionPointer; a compile failure fails
    // the test. Shims with pure inputs are also invoked and their values
    // asserted. Walk.Evaluate is compile-only: its AnchorLookup context wraps a
    // ComponentLookup that has no storage on defaults, and invoking it would
    // dereference a null lookup instead of testing compilation.
    [TestFixture]
    public class KindBurstTests
    {
        internal delegate float LiftFn(float first, float next);
        internal delegate float LocomotionFn(float x, float first, byte firstPriority, float next, byte nextPriority);
        internal delegate int DamageFn(int health, int first, int next);
        internal delegate int StatChangeFn(int stamina, int first, int next);
        internal delegate float JumpFn(float scale, float velocity, float power);
        internal delegate int HitFn(int amount, bool reverse);
        internal delegate int StatPulseFn(int delta, bool reverse);
        internal delegate int MarkerFn(int delta);
        internal delegate Speed WalkFn(in Frame<WalkTrack, WalkClip> frame, in WalkStat stat, in Target target, in AnchorLookup anchors);

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        static float LiftBurst(float first, float next)
        {
            var total = first;
            Lift.Combine(ref total, in next);
            var motion = new Motion();
            Lift.Apply(in total, ref motion);
            return motion.Y;
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        static float LocomotionBurst(float x, float first, byte firstPriority, float next, byte nextPriority)
        {
            var total = new Speed { Value = first, Priority = firstPriority };
            var other = new Speed { Value = next, Priority = nextPriority };
            Locomotion.Combine(ref total, in other);
            var motion = new Motion { X = x };
            Locomotion.Apply(in total, ref motion);
            return motion.X;
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        static int DamageBurst(int health, int first, int next)
        {
            var total = first;
            Damage.Combine(ref total, in next);
            var body = new Health { Value = health };
            Damage.Apply(in total, ref body);
            return body.Value;
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        static int StatChangeBurst(int stamina, int first, int next)
        {
            var body = new Stamina { Value = stamina };
            StatChange.Apply(in first, ref body);
            StatChange.Apply(in next, ref body);
            return body.Value;
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        static float JumpBurst(float scale, float velocity, float power)
        {
            var track = new ArcTrack { Scale = scale };
            var clip = new ArcClip { Velocity = velocity };
            var frame = new Frame<ArcTrack, ArcClip>(in track, in clip, 0, 12, 0, 0, global::Tl.FrameFlags.None);
            Jump.Fold(in frame, out var lift);
            var boost = new JumpPower { Value = power };
            return Jump.Evaluate(in lift, in boost);
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        static int HitBurst(int amount, bool reverse)
        {
            var track = new HitTrack { Scale = 1f };
            var clip = new HitClip { Amount = amount };
            var flags = reverse ? global::Tl.FrameFlags.Reverse : global::Tl.FrameFlags.None;
            var frame = new Frame<HitTrack, HitClip>(in track, in clip, 2, 2, 0, 0, flags);
            return Hit.Start(in frame);
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        static int StatPulseBurst(int delta, bool reverse)
        {
            var track = new StatTrack { Scale = 1f };
            var clip = new StatClip { Delta = delta };
            var flags = reverse ? global::Tl.FrameFlags.Reverse : global::Tl.FrameFlags.None;
            var frame = new Frame<StatTrack, StatClip>(in track, in clip, 0, 1, 0, 0, flags);
            return ChangeStat.Start(in frame);
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        static int MarkerBurst(int delta)
        {
            var marker = new StepMarker { Delta = delta };
            var frame = new global::Tl.MarkerFrame<StepMarker>(in marker, 6, 0, global::Tl.FrameFlags.None);
            return Steps.OnStep(in frame);
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        static Speed WalkBurst(in Frame<WalkTrack, WalkClip> frame, in WalkStat stat, in Target target, in AnchorLookup anchors)
        {
            return Walk.Evaluate(in frame, in stat, in target, in anchors);
        }

        [Test]
        public void BurstIsEnabledForKindJobs()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled);
        }

        [Test]
        public void LiftSumsUnderBurst()
        {
            var fn = BurstCompiler.CompileFunctionPointer<LiftFn>(LiftBurst);
            Assert.AreEqual(7f, fn.Invoke(3f, 4f));
        }

        [Test]
        public void HigherPriorityWalkWinsUnderBurst()
        {
            var fn = BurstCompiler.CompileFunctionPointer<LocomotionFn>(LocomotionBurst);
            Assert.AreEqual(7f, fn.Invoke(0f, 2f, 0, 7f, 10));
        }

        [Test]
        public void TiedPriorityKeepsEntryOrderUnderBurst()
        {
            var fn = BurstCompiler.CompileFunctionPointer<LocomotionFn>(LocomotionBurst);
            Assert.AreEqual(9f, fn.Invoke(0f, 2f, 0, 9f, 0));
        }

        [Test]
        public void DamagePulsesSumIntoOneWriteUnderBurst()
        {
            var fn = BurstCompiler.CompileFunctionPointer<DamageFn>(DamageBurst);
            Assert.AreEqual(965, fn.Invoke(1000, 25, 10));
        }

        [Test]
        public void StatPulsesApplyEachOnceUnderBurst()
        {
            var fn = BurstCompiler.CompileFunctionPointer<StatChangeFn>(StatChangeBurst);
            Assert.AreEqual(9, fn.Invoke(0, -1, 10));
        }

        [Test]
        public void JumpFoldAndFeedMultiplyUnderBurst()
        {
            var fn = BurstCompiler.CompileFunctionPointer<JumpFn>(JumpBurst);
            Assert.AreEqual(12f, fn.Invoke(2f, 3f, 2f));
        }

        [Test]
        public void HitPulseFiresForwardOnlyUnderBurst()
        {
            var fn = BurstCompiler.CompileFunctionPointer<HitFn>(HitBurst);
            Assert.AreEqual(25, fn.Invoke(25, false));
            Assert.AreEqual(0, fn.Invoke(25, true));
        }

        [Test]
        public void StatPulseFollowsDirectionUnderBurst()
        {
            var fn = BurstCompiler.CompileFunctionPointer<StatPulseFn>(StatPulseBurst);
            Assert.AreEqual(-1, fn.Invoke(-1, false));
            Assert.AreEqual(1, fn.Invoke(-1, true));
        }

        [Test]
        public void MarkerPulseCarriesItsDeltaUnderBurst()
        {
            var fn = BurstCompiler.CompileFunctionPointer<MarkerFn>(MarkerBurst);
            Assert.AreEqual(10, fn.Invoke(10));
        }

        [Test]
        public void WalkFeederWithLookupContextCompilesUnderBurst()
        {
            BurstCompiler.CompileFunctionPointer<WalkFn>(WalkBurst);
        }
    }
}
