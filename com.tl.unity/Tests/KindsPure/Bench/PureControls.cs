using System;
using System.Collections.Generic;
using Tl.Gen.Tlb;

namespace KindPure
{
    public enum PureControl
    {
        PauseResume,
        Reverse,
        WorldRewind,
        Rates,
        Seek,
        Handover,
        Loop,
        Stop,
        Republish,
        Mixed,
    }

    public interface IPureRequests
    {
        void Request(int row, global::Tl.RequestOp op, ushort timeline, ushort at);
    }

    public static unsafe class PureControls
    {
        public const ushort Arc = 1, End = 2, Hold = 3, Loop = 4, Walk = 5;
        public const int Rows = 24;
        public const uint Seed = 0x2545F491u;

        public static int StepsOf(PureControl control) => control == PureControl.Mixed ? 240 : 160;

        public static bool Forward(PureControl control, int step)
        {
            switch (control)
            {
                case PureControl.PauseResume: return !(step >= 100 && step < 120);
                case PureControl.Reverse: return !(step >= 80 && step < 100);
                case PureControl.WorldRewind: return !(step >= 25 && step < 60) && !(step >= 90 && step < 93) && !(step >= 130 && step < 150);
                case PureControl.Rates: return !(step >= 70 && step < 90);
                case PureControl.Seek: return !(step >= 85 && step < 110);
                case PureControl.Handover: return !(step >= 70 && step < 85);
                case PureControl.Loop: return !(step >= 40 && step < 100);
                case PureControl.Stop: return !(step >= 70 && step < 80);
                case PureControl.Republish: return !(step >= 100 && step < 115);
                default: return !(step >= 100 && step < 120);
            }
        }

        public static bool SecondLibrary(PureControl control, int step)
        {
            if (control == PureControl.Republish) return (step >= 40 && step < 90) || step >= 130;
            if (control == PureControl.Mixed) return step >= 150 && step < 200;
            return false;
        }

        public static void Requests(PureControl control, int step, ref uint rng, IPureRequests rows)
        {
            for (var r = 0; r < Rows; r++)
            {
                switch (control)
                {
                    case PureControl.PauseResume: PauseResumeRequests(step, r, rows); break;
                    case PureControl.Reverse: ReverseRequests(step, r, rows); break;
                    case PureControl.WorldRewind: WorldRewindRequests(step, r, rows); break;
                    case PureControl.Rates: RatesRequests(step, r, rows); break;
                    case PureControl.Seek: SeekRequests(step, r, rows); break;
                    case PureControl.Handover: HandoverRequests(step, r, rows); break;
                    case PureControl.Loop: LoopRequests(step, r, rows); break;
                    case PureControl.Stop: StopRequests(step, r, rows); break;
                    case PureControl.Republish: RepublishRequests(step, r, rows); break;
                    default: MixedRequests(ref rng, r, rows); break;
                }
            }
        }

        static void Ask(IPureRequests rows, int r, global::Tl.RequestOp op, ushort timeline, int at) => rows.Request(r, op, timeline, (ushort)at);

        static void StartLoopAndWalk(int r, IPureRequests rows)
        {
            Ask(rows, r, global::Tl.RequestOp.Start, Loop, r % 7);
            Ask(rows, r, global::Tl.RequestOp.Start, Walk, r * 5 % 13);
        }

        static void PauseResumeRequests(int step, int r, IPureRequests rows)
        {
            if (step == 0)
            {
                StartLoopAndWalk(r, rows);
                if (r % 4 == 0) Ask(rows, r, global::Tl.RequestOp.Start, Arc, r % 5);
            }
            if (step == 9 && r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Pause, Loop, 0);
            if (step == 21 && r % 3 == 0) Ask(rows, r, global::Tl.RequestOp.Pause, Walk, 0);
            if (step == 33 && r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Resume, Loop, 0);
            if (step == 47 && r % 3 == 1) Ask(rows, r, global::Tl.RequestOp.Pause, Walk, 0);
            if (step == 47 && r % 3 == 0) Ask(rows, r, global::Tl.RequestOp.Resume, Walk, 0);
            if (step == 70 && r % 5 == 0)
            {
                Ask(rows, r, global::Tl.RequestOp.Pause, Loop, 0);
                Ask(rows, r, global::Tl.RequestOp.Resume, Loop, 0);
            }
            if (step == 90) Ask(rows, r, global::Tl.RequestOp.Resume, Walk, 0);
            if (step == 105 && r % 2 == 1) Ask(rows, r, global::Tl.RequestOp.Pause, Loop, 0);
            if (step == 112 && r % 2 == 1) Ask(rows, r, global::Tl.RequestOp.Resume, Loop, 0);
        }

        static void ReverseRequests(int step, int r, IPureRequests rows)
        {
            if (step == 0)
            {
                StartLoopAndWalk(r, rows);
                if (r % 4 == 1) Ask(rows, r, global::Tl.RequestOp.Start, End, r % 6);
            }
            if (step == 7 && r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Reverse, Loop, 1);
            if (step == 19 && r % 3 == 0) Ask(rows, r, global::Tl.RequestOp.Reverse, Walk, 1);
            if (step == 44 && r % 4 == 0) Ask(rows, r, global::Tl.RequestOp.Reverse, Loop, 0);
            if (step == 90 && r % 3 == 0) Ask(rows, r, global::Tl.RequestOp.Reverse, Walk, 0);
            if (step == 130 && r % 2 == 1) Ask(rows, r, global::Tl.RequestOp.Reverse, Loop, 1);
            if (step == 130 && r % 4 == 2)
            {
                Ask(rows, r, global::Tl.RequestOp.Start, End, 17);
                Ask(rows, r, global::Tl.RequestOp.Reverse, End, 1);
            }
        }

        static void WorldRewindRequests(int step, int r, IPureRequests rows)
        {
            if (step == 0)
            {
                Ask(rows, r, global::Tl.RequestOp.Start, (ushort)(1 + r % 5), r % 7);
                Ask(rows, r, global::Tl.RequestOp.Start, (ushort)(1 + (r + 2) % 5), r * 3 % 11);
            }
            if (step == 60 && r % 3 == 0)
            {
                Ask(rows, r, global::Tl.RequestOp.Start, Hold, 2);
                Ask(rows, r, global::Tl.RequestOp.Start, End, 4);
            }
        }

        static void RatesRequests(int step, int r, IPureRequests rows)
        {
            if (step == 0)
            {
                StartLoopAndWalk(r, rows);
                if (r % 3 == 0) Ask(rows, r, global::Tl.RequestOp.Start, Arc, r % 9);
            }
            if (step == 3) Ask(rows, r, global::Tl.RequestOp.Speed, Loop, r % 3 == 0 ? 32 : r % 3 == 1 ? 8 : 24);
            if (step == 40) Ask(rows, r, global::Tl.RequestOp.Speed, Walk, r % 3 == 0 ? 24 : r % 3 == 1 ? 32 : 8);
            if (step == 100) Ask(rows, r, global::Tl.RequestOp.Speed, Loop, 16);
            if (step == 110 && r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Start, Arc, 0);
            if (step == 111 && r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Speed, Arc, r % 4 == 0 ? 32 : 24);
            if (step == 125 && r % 5 == 0) Ask(rows, r, global::Tl.RequestOp.Pause, Walk, 0);
            if (step == 135 && r % 5 == 0) Ask(rows, r, global::Tl.RequestOp.Resume, Walk, 0);
        }

        static void SeekRequests(int step, int r, IPureRequests rows)
        {
            if (step == 0)
            {
                StartLoopAndWalk(r, rows);
                if (r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Start, Hold, r % 9);
                if (r % 3 == 0) Ask(rows, r, global::Tl.RequestOp.Start, End, r % 11);
            }
            if (step == 11 || step == 29 || step == 53 || step == 77 || step == 101 || step == 140)
            {
                if (r % 2 == step % 2) Ask(rows, r, global::Tl.RequestOp.Seek, Loop, (r * 5 + step) % 16);
                else Ask(rows, r, global::Tl.RequestOp.Seek, Walk, (r * 3 + step) % 16);
            }
            if (step == 60 && r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Seek, Hold, r * 3 % 16);
        }

        static void HandoverRequests(int step, int r, IPureRequests rows)
        {
            if (step == 0)
            {
                Ask(rows, r, global::Tl.RequestOp.Start, Arc, r % 13);
                if (r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Start, End, r % 7);
            }
            if (step == 50 && r % 3 == 0) Ask(rows, r, global::Tl.RequestOp.Start, Arc, 0);
            if (step == 100 && r % 2 == 1) Ask(rows, r, global::Tl.RequestOp.Start, Arc, 15);
            if (step == 120 && r % 4 == 0) Ask(rows, r, global::Tl.RequestOp.Reverse, Walk, 1);
        }

        static void LoopRequests(int step, int r, IPureRequests rows)
        {
            if (step == 0)
            {
                Ask(rows, r, global::Tl.RequestOp.Start, Loop, r % 23);
                Ask(rows, r, global::Tl.RequestOp.Start, Walk, r % 29);
            }
            if (step == 120 && r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Reverse, Loop, 1);
            if (step == 140 && r % 3 == 0) Ask(rows, r, global::Tl.RequestOp.Speed, Walk, 24);
        }

        static void StopRequests(int step, int r, IPureRequests rows)
        {
            if (step == 0)
            {
                StartLoopAndWalk(r, rows);
                Ask(rows, r, global::Tl.RequestOp.Start, Arc, r % 5);
                Ask(rows, r, global::Tl.RequestOp.Start, Hold, r % 3);
            }
            if (step == 10 && r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Stop, Loop, 0);
            if (step == 17 && r % 5 == 0) Ask(rows, r, global::Tl.RequestOp.StopAll, 0, 0);
            if (step == 23 && r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Start, Loop, 3);
            if (step == 31 && r % 3 == 0)
            {
                Ask(rows, r, global::Tl.RequestOp.Stop, Walk, 0);
                Ask(rows, r, global::Tl.RequestOp.Start, Walk, 0);
            }
            if (step == 45) Ask(rows, r, global::Tl.RequestOp.Stop, Hold, 0);
            if (step == 60 && r % 4 == 1) Ask(rows, r, global::Tl.RequestOp.Stop, Walk, 0);
            if (step == 61 && r % 4 == 1) Ask(rows, r, global::Tl.RequestOp.Start, End, 2);
            if (step == 75 && r % 3 == 1) Ask(rows, r, global::Tl.RequestOp.Stop, Loop, 0);
            if (step == 100) Ask(rows, r, global::Tl.RequestOp.StopAll, 0, 0);
            if (step == 101)
            {
                Ask(rows, r, global::Tl.RequestOp.Start, Loop, r % 9);
                Ask(rows, r, global::Tl.RequestOp.Start, Arc, 0);
            }
        }

        static void RepublishRequests(int step, int r, IPureRequests rows)
        {
            if (step == 0)
            {
                StartLoopAndWalk(r, rows);
                if (r % 2 == 0) Ask(rows, r, global::Tl.RequestOp.Start, Arc, r % 5);
                if (r % 3 == 0) Ask(rows, r, global::Tl.RequestOp.Start, Hold, r % 4);
            }
            if (step == 60 && r % 2 == 1) Ask(rows, r, global::Tl.RequestOp.Start, End, 1);
        }

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        static void MixedRequests(ref uint rng, int r, IPureRequests rows)
        {
            var n = Next(ref rng);
            var timeline = (ushort)(1 + (n >> 8) % 5);
            switch (n & 15)
            {
                case 0:
                case 1:
                case 2:
                    Ask(rows, r, global::Tl.RequestOp.Start, timeline, (int)((n >> 12) % 12));
                    break;
                case 3:
                    Ask(rows, r, global::Tl.RequestOp.Stop, timeline, 0);
                    break;
                case 4:
                    Ask(rows, r, global::Tl.RequestOp.Speed, timeline, (int)(8 + 8 * ((n >> 16) % 4)));
                    break;
                case 5:
                    Ask(rows, r, global::Tl.RequestOp.Reverse, timeline, (int)((n >> 16) & 1));
                    break;
                case 6:
                    if (((n >> 16) & 7) == 0) Ask(rows, r, global::Tl.RequestOp.Seek, timeline, (int)((n >> 20) % 12));
                    break;
                case 7:
                    if (((n >> 16) & 7) == 0) Ask(rows, r, global::Tl.RequestOp.Pause, timeline, 0);
                    break;
                case 8:
                    Ask(rows, r, global::Tl.RequestOp.Resume, timeline, 0);
                    break;
            }
        }

        static byte[] Raw<T>(T value) where T : unmanaged
        {
            var bytes = new byte[sizeof(T)];
            fixed (byte* at = bytes) *(T*)at = value;
            return bytes;
        }

        static TypedTrack Arcs(int lift, params (uint Start, uint End, int Velocity)[] clips)
        {
            var track = new TypedTrack { Type = typeof(ArcTrack), Data = Raw(new ArcTrack { Scale = 1f }) };
            foreach (var clip in clips)
                track.Clips.Add(new TypedClip { Type = typeof(ArcClip), Start = clip.Start, End = clip.End, Data = Raw(new ArcClip { Velocity = clip.Velocity + lift }) });
            return track;
        }

        static TypedTrack Walks(int lift, params (uint Start, uint End, int Priority)[] clips)
        {
            var track = new TypedTrack { Type = typeof(WalkTrack), Data = Raw(new WalkTrack { Scale = 1f }) };
            foreach (var clip in clips)
            {
                var priority = (byte)(clip.Priority + lift);
                track.Clips.Add(new TypedClip { Type = typeof(WalkClip), Start = clip.Start, End = clip.End, Data = Raw(new WalkClip { Speed = 1 + priority, Priority = priority }) });
            }
            return track;
        }

        static TypedTrack Hits(int lift, uint start, uint end, int amount)
        {
            var track = new TypedTrack { Type = typeof(HitTrack), Data = Raw(new HitTrack { Scale = 1f }) };
            track.Clips.Add(new TypedClip { Type = typeof(HitClip), Start = start, End = end, Data = Raw(new HitClip { Amount = amount + lift }) });
            return track;
        }

        static TypedTrack Stats(int lift, uint start, uint end, int delta)
        {
            var track = new TypedTrack { Type = typeof(StatTrack), Data = Raw(new StatTrack { Scale = 1f }) };
            track.Clips.Add(new TypedClip { Type = typeof(StatClip), Start = start, End = end, Data = Raw(new StatClip { Delta = delta + lift }) });
            return track;
        }

        static TypedTimeline Timeline(string name, uint duration, bool loops, bool hold, string next, params TypedTrack[] tracks)
        {
            var timeline = new TypedTimeline { Name = name, Duration = duration, Loops = loops, Hold = hold, Next = next };
            for (var i = 0; i < tracks.Length; i++)
            {
                tracks[i].Name = "t" + i;
                timeline.Tracks.Add(tracks[i]);
            }
            return timeline;
        }

        public static byte[] Library(bool second)
        {
            var lift = second ? 1 : 0;
            var timelines = new List<TypedTimeline>
            {
                Timeline("arc", 20, false, false, "walk", Arcs(lift, (0, 12, 2), (8, 18, 3)), Walks(lift, (4, 16, 1)), Hits(lift, 2, 9, 3), Stats(lift, 5, 15, 2)),
                Timeline("end", 18, false, false, null, Arcs(lift, (3, 15, 1)), Walks(lift, (0, 18, 0)), Walks(lift, (6, 12, 2)), Hits(lift, 10, 14, 4)),
                Timeline("hold", 16, false, true, null, Walks(lift, (2, 14, 1)), Arcs(lift, (0, 16, 4)), Stats(lift, 3, 8, 1)),
                Timeline("loop", 24, true, false, null, Arcs(lift, (0, 10, 1), (6, 16, 2)), Walks(lift, (4, 20, 2)), Walks(lift, (0, 24, 0)), Hits(lift, 0, 3, 2), Stats(lift, 12, 20, 3)),
                Timeline("walk", 30, true, false, null, Walks(lift, (0, 10, 1), (6, 16, 2)), Walks(lift, (4, 20, 0)), Arcs(lift, (2, 28, 3)), Hits(lift, 15, 25, 1), Stats(lift, 1, 29, 1)),
            };
            var names = new List<string>();
            foreach (var timeline in timelines) names.Add(timeline.Name);
            var manifest = LibraryBake.Manifest(names, new List<string>(), 60);
            var kinds = LibraryBake.Kinds(new[] { typeof(WalkTrack).Assembly });
            var parts = new List<global::Tl.LibraryPart>();
            foreach (var timeline in timelines)
            {
                var tlb = LibraryBake.BakeTyped(timeline, manifest);
                parts.Add(new global::Tl.LibraryPart { Name = timeline.Name, Tlb = tlb, Kinds = LibraryBake.KindPayload(timeline.Name, tlb, kinds), Next = (ushort)global::Tl.LibraryAssembler.NextOf(tlb) });
            }
            return global::Tl.LibraryAssembler.Assemble(parts, 60, global::Tl.LibrarySettings.Default);
        }
    }
}
