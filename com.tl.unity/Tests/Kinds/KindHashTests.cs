using System;
using NUnit.Framework;
using Unity.Entities;

namespace KindGame
{
    [TestFixture]
    public class KindHashTests
    {
        const ulong Pinned = 13943614770702343869UL;

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        static ulong Mix(ulong h, float v) => (h ^ (ulong)BitConverter.SingleToInt32Bits(v)) * 1099511628211UL;

        static ulong MixInt(ulong h, int v) => (h ^ (ulong)(uint)v) * 1099511628211UL;

        [Test]
        public void SixHundredStepKindScriptHashesLikeDotNet()
        {
            const int n = 4;
            using (var game = new KindWorld(n))
            {
                var ids = new ushort[] { KindWorld.Jump, KindWorld.PotionHop, KindWorld.Run, KindWorld.SpeedBoost, KindWorld.Stomp };
                var rng = 0x2545F491u;
                var h = 14695981039346656037UL;
                for (var step = 0; step < 600; step++)
                {
                    var forward = !(step >= 200 && step < 240);
                    for (var e = 0; e < n; e++)
                    {
                        var r = Next(ref rng);
                        switch (r & 15)
                        {
                            case 0:
                            case 1:
                                game.Start(e, ids[(r >> 8) % 5]);
                                break;
                            case 2:
                                game.Stop(e, ids[(r >> 8) % 5]);
                                break;
                            case 3:
                                if (((r >> 8) & 7) == 0) game.StopChannel(e, (byte)(1 + ((r >> 12) & 1)));
                                break;
                            case 4:
                                if (((r >> 8) & 15) == 0) game.StopAll(e);
                                break;
                            case 5:
                            case 6:
                                game.Damage((int)((r >> 8) & 3), 3 + (int)((r >> 10) & 7));
                                break;
                            case 7:
                                game.Stat(e, (int)((r >> 8) & 3) - 1);
                                break;
                        }
                    }
                    game.Step(forward);
                    for (var e = 0; e < n; e++)
                    {
                        var motion = game.Motion(e);
                        h = Mix(h, motion.X);
                        h = Mix(h, motion.Y);
                        h = MixInt(h, game.Stamina(e));
                        h = MixInt(h, game.Health(e));
                    }
                }
                Assert.AreEqual(Pinned, h);
                Assert.AreEqual(0, game.Tick.DroppedPulses);
            }
        }
    }
}
