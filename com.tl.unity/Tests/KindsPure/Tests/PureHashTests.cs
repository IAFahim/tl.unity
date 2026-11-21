using NUnit.Framework;
using Tl.Unity;
using Unity.Entities;

namespace KindPure
{
    [TestFixture]
    public class PureHashTests
    {
        const int StepsLikeDotNetKindShape = 1 + 40 + 16;

        byte[] _library;

        [OneTimeSetUp]
        public void BakeTheKindShapeLibraryOnce()
        {
            _library = PureShapeBench.Library(PureShapeBench.LiveTracks);
        }

        [TestCase(64, 4, 0x025D8DC5105F05E7UL)]
        [TestCase(64, 12, 0xF9E653C956890F1EUL)]
        [TestCase(300, 4, 0x0E3A8FF84D68D7FEUL)]
        [TestCase(300, 12, 0x4F9E8B886CFE80A5UL)]
        public void PrecombinedKindsHashLikeTheDotNetKindShapePureDomain(int rows, int active, ulong dotNetHash)
        {
            var hash = PureShapeBench.Play(_library, rows, active, StepsLikeDotNetKindShape, out var precombined);
            Assert.IsTrue(precombined, "Locomotion and Lift declare Associative and take only the frame and their folds, so both must read precombined tables");
            Assert.AreEqual(dotNetHash.ToString("X16"), hash.ToString("X16"));
        }

        [Test]
        public unsafe void RatedZeroCrossingStepKeepsFusedKindsActive()
        {
            using var game = new PureWorld(2, _library);
            game.Step();
            using var query = game.Entities.CreateEntityQuery(typeof(TimelineLibrary));
            var index = query.GetSingleton<TimelineLibrary>().Kinds;
            var locoId = index.KindOf(KindIndex.IdOf("KindPure.Locomotion"));
            var liftId = index.KindOf(KindIndex.IdOf("KindPure.Lift"));
            ushort chosenTl = 0;
            ushort chosenTick = 0;
            for (ushort tl = 1; tl <= index.TimelineCount && chosenTl == 0; tl++)
            {
                var dur = (ushort)index.Timelines[tl - 1].Duration;
                for (ushort tk = 0; tk + 1 < dur; tk++)
                {
                    if (index.Values[locoId].RowOf(tl, tk) != null && index.Values[liftId].RowOf(tl, tk) != null
                        && index.Values[locoId].RowOf(tl, (ushort)(tk + 1)) != null && index.Values[liftId].RowOf(tl, (ushort)(tk + 1)) != null)
                    {
                        chosenTl = tl;
                        chosenTick = tk;
                        break;
                    }
                }
            }
            Assert.Greater(chosenTl, 0);
            game.Start(0, chosenTl, chosenTick);
            game.Step();
            game.Entities.GetBuffer<TimelineRequest>(game.Rows[0]).Add(TimelineRequest.SetSpeed(0, chosenTl, 8));
            game.Step();
            Assert.IsTrue(game.Entities.IsComponentEnabled<LocomotionActive>(game.Rows[0]));
            Assert.IsTrue(game.Entities.IsComponentEnabled<LiftActive>(game.Rows[0]));
            game.Step();
            var beforeZero = game.Entities.GetComponentData<Movement_MotionData>(game.Rows[0]).Value;
            game.Step();
            Assert.IsTrue(game.Entities.IsComponentEnabled<LocomotionActive>(game.Rows[0]));
            Assert.IsTrue(game.Entities.IsComponentEnabled<LiftActive>(game.Rows[0]));
            var afterZero = game.Entities.GetComponentData<Movement_MotionData>(game.Rows[0]).Value;
            Assert.AreEqual(beforeZero.X, afterZero.X);
            Assert.AreEqual(beforeZero.Y, afterZero.Y);
        }
    }
}
