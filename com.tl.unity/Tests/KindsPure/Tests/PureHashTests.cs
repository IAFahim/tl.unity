using NUnit.Framework;

namespace KindPure
{
    [TestFixture]
    public class PureHashTests
    {
        const int StepsLikeDotNetKindShape = 1 + 40 + 16;

        static byte[] _library;

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
    }
}
