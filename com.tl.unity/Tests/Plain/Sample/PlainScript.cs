namespace PlainGame
{
    public static class PlainScript
    {
        public const ulong Pinned = 13943614770702343869UL;
        public const int Rows = 4;
        public const int Steps = 600;
        public const ulong EventsPinned = 6607956939547305625UL;

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        static ulong Mix(ulong h, float v) { var s = new global::Tl.TlStateHash { Value = h }; s.FloatLane(v); return s.Value; }

        static ulong MixInt(ulong h, int v) { var s = new global::Tl.TlStateHash { Value = h }; s.Int(v); return s.Value; }

        public static ulong Play(PlainHost host, int steps)
        {
            var ids = new ushort[] { Timelines.Jump, Timelines.PotionHop, Timelines.Run, Timelines.SpeedBoost, Timelines.Stomp };
            var rng = 0x2545F491u;
            var h = global::Tl.TlStateHash.Seed;
            for (var step = 0; step < steps; step++)
            {
                var forward = !(step >= 200 && step < 240);
                for (var e = 0; e < Rows; e++)
                {
                    var r = Next(ref rng);
                    switch (r & 15)
                    {
                        case 0:
                        case 1:
                            host.Start(e, ids[(r >> 8) % 5]);
                            break;
                        case 2:
                            host.Stop(e, ids[(r >> 8) % 5]);
                            break;
                        case 3:
                            if (((r >> 8) & 7) == 0) host.StopChannel(e, (byte)(1 + ((r >> 12) & 1)));
                            break;
                        case 4:
                            if (((r >> 8) & 15) == 0) host.StopAll(e);
                            break;
                        case 5:
                        case 6:
                            host.Damage((int)((r >> 8) & 3), 3 + (int)((r >> 10) & 7));
                            break;
                        case 7:
                            host.ChangeStamina(e, (int)((r >> 8) & 3) - 1);
                            break;
                    }
                }
                host.Step(forward);
                for (var e = 0; e < Rows; e++)
                {
                    h = Mix(h, host.Motion[e].X);
                    h = Mix(h, host.Motion[e].Y);
                    h = MixInt(h, host.Stamina[e].Value);
                    h = MixInt(h, host.Health[e].Value);
                }
            }
            return h;
        }

        public static ulong PlayEvents(PlainHost host, int steps)
        {
            var ids = new ushort[] { Timelines.Jump, Timelines.PotionHop, Timelines.Run, Timelines.SpeedBoost, Timelines.Stomp, Timelines.Whistle };
            var rng = 0x9E3779B9u;
            var h = global::Tl.TlStateHash.Seed;
            for (var step = 0; step < steps; step++)
            {
                var forward = !(step >= 200 && step < 240);
                for (var e = 0; e < Rows; e++)
                {
                    var r = Next(ref rng);
                    switch (r & 7)
                    {
                        case 0:
                        case 1:
                        case 2:
                            host.Start(e, ids[(r >> 8) % 6]);
                            break;
                        case 3:
                            host.Stop(e, ids[(r >> 8) % 6]);
                            break;
                        case 4:
                            if (((r >> 8) & 7) == 0) host.StopAll(e);
                            break;
                    }
                }
                host.Step(forward);
                foreach (var cue in host.DrainCues())
                {
                    h = MixInt(h, cue.Entity.Index);
                    h = MixInt(h, cue.Slot);
                    h = MixInt(h, cue.Value.Voice);
                }
            }
            return h;
        }
    }
}
