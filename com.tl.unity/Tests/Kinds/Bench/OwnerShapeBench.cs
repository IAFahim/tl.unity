using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Tl.Gen.Tlb;
using Tl.Unity;
using Unity.Collections;
using Unity.Entities;

namespace KindGame
{
    public static unsafe class OwnerShapeBench
    {
        const int TimelineCount = 500;
        const int TracksPerTimeline = 16;
        const int ShortestDuration = 30;
        const int DurationSpread = 31;
        const int BlendOverlapTicks = 5;

        public static void RunBatch()
        {
            var args = Environment.GetCommandLineArgs();
            var log = Path.Combine(UnityEngine.Application.dataPath, "..", "OwnerShapeBench.log");
            var rowsList = "10000,50000";
            var activeList = "40,100";
            var liveList = "4,8,16";
            var modes = "eager,lazy";
            var warmup = 32;
            var measure = 64;
            string perfControl = null;
            string perfAck = null;
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-shapeLog") log = args[i + 1];
                if (args[i] == "-shapeRows") rowsList = args[i + 1];
                if (args[i] == "-shapeActive") activeList = args[i + 1];
                if (args[i] == "-shapeLive") liveList = args[i + 1];
                if (args[i] == "-shapeModes") modes = args[i + 1];
                if (args[i] == "-shapeWarmup") warmup = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-shapeMeasure") measure = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-perfControl") perfControl = args[i + 1];
                if (args[i] == "-perfAck") perfAck = args[i + 1];
            }
            Unity.Burst.BurstCompiler.Options.EnableBurstCompileSynchronously = true;
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobDebuggerEnabled = false;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Disabled;
            using var writer = new StreamWriter(log, append: true);
            Row(writer, "env unity=" + UnityEngine.Application.unityVersion + " burst=" + Unity.Burst.BurstCompiler.IsEnabled + " safety=" + Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks + " workers=" + Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + " timelines=" + TimelineCount + " tracks=" + TracksPerTimeline + " durations=" + ShortestDuration + "-" + (ShortestDuration + DurationSpread - 1) + " warmup=" + warmup + " measure=" + measure);
            var perf = new PerfSwitch(perfControl, perfAck);
            foreach (var liveText in liveList.Split(','))
            {
                var liveTracks = int.Parse(liveText, CultureInfo.InvariantCulture);
                var library = Library(liveTracks);
                if (Array.IndexOf(args, "-shapeDump") >= 0)
                {
                    using var dumped = new KindWorld(1, library);
                    Row(writer, Dump(dumped, 1));
                }
                foreach (var rowsText in rowsList.Split(','))
                foreach (var activeText in activeList.Split(','))
                foreach (var mode in modes.Split(','))
                    Cell(writer, library, liveTracks, int.Parse(rowsText, CultureInfo.InvariantCulture), int.Parse(activeText, CultureInfo.InvariantCulture), mode, warmup, measure, perf);
            }
        }

        static void Row(StreamWriter writer, string text)
        {
            var line = "TlShapeBench: " + text;
            writer.WriteLine(line);
            writer.Flush();
            UnityEngine.Debug.Log(line);
        }

        public static int DurationOf(int timelineIndex) => ShortestDuration + timelineIndex * 7 % DurationSpread;

        public static bool Blends(int track) => track < 4 || track == 8 || track == 9;

        public static byte[] Library(int liveTracks)
        {
            var names = new List<string>();
            for (var i = 0; i < TimelineCount; i++)
                names.Add("shape" + (i + 1).ToString("D4", CultureInfo.InvariantCulture));
            var manifest = LibraryBake.Manifest(names, new List<string>(), 60);
            var kinds = LibraryBake.Kinds(new[] { typeof(WalkTrack).Assembly });
            var parts = new List<global::Tl.LibraryPart>();
            var state = 0x9E3779B9u;
            for (var i = 0; i < TimelineCount; i++)
            {
                var duration = DurationOf(i);
                var timeline = new TypedTimeline { Name = names[i], Duration = (uint)duration, Loops = true };
                for (var track = 0; track < TracksPerTimeline; track++)
                {
                    var covered = Math.Max(1, duration * liveTracks / TracksPerTimeline);
                    var start = (uint)(Next(ref state) % (uint)(duration - covered + 1));
                    var end = start + (uint)covered;
                    var split = Blends(track) && covered >= 2 * BlendOverlapTicks - 1;
                    var firstEnd = split ? start + (uint)((covered + BlendOverlapTicks) / 2) : end;
                    var secondStart = firstEnd - BlendOverlapTicks;
                    var seed = i + track;
                    TypedTrack typed;
                    if (track < 8)
                    {
                        typed = Track("Jump" + track, typeof(ArcTrack), Raw(new ArcTrack { Scale = 1f }));
                        Clip(typed, typeof(ArcClip), Raw(new ArcClip { Velocity = 1 + seed % 5 }), start, firstEnd);
                        if (split) Clip(typed, typeof(ArcClip), Raw(new ArcClip { Velocity = 1 + (seed + 2) % 5 }), secondStart, end);
                    }
                    else if (track < 12)
                    {
                        var priority = (byte)(seed % 3);
                        typed = Track("Walk" + track, typeof(WalkTrack), Raw(new WalkTrack { Scale = 1f }));
                        Clip(typed, typeof(WalkClip), Raw(new WalkClip { Speed = 1 + priority, Priority = priority }), start, firstEnd);
                        if (split) Clip(typed, typeof(WalkClip), Raw(new WalkClip { Speed = 1 + priority, Priority = priority }), secondStart, end);
                    }
                    else if (track < 14)
                    {
                        typed = Track("Hit" + track, typeof(HitTrack), Raw(new HitTrack { Scale = 1f }));
                        Clip(typed, typeof(HitClip), Raw(new HitClip { Amount = 1 + seed % 5 }), start, end);
                    }
                    else
                    {
                        typed = Track("Stat" + track, typeof(StatTrack), Raw(new StatTrack { Scale = 1f }));
                        Clip(typed, typeof(StatClip), Raw(new StatClip { Delta = 1 + seed % 3 }), start, end);
                    }
                    timeline.Tracks.Add(typed);
                }
                var tlb = LibraryBake.BakeTyped(timeline, manifest);
                parts.Add(new global::Tl.LibraryPart { Name = names[i], Tlb = tlb, Kinds = LibraryBake.KindPayload(names[i], tlb, kinds), Next = 0 });
            }
            return global::Tl.LibraryAssembler.Assemble(parts, 60, global::Tl.LibrarySettings.Default);
        }

        static TypedTrack Track(string name, Type trackType, byte[] trackData) => new TypedTrack { Name = name, Type = trackType, Data = trackData };

        static void Clip(TypedTrack track, Type clipType, byte[] clipData, uint start, uint end)
        {
            track.Clips.Add(new TypedClip { Name = clipType.Name, Type = clipType, Start = start, End = end, Data = clipData });
        }

        static byte[] Raw<T>(T value) where T : unmanaged
        {
            var bytes = new byte[sizeof(T)];
            fixed (byte* at = bytes)
                *(T*)at = value;
            return bytes;
        }

        static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        static void Cell(StreamWriter writer, byte[] library, int liveTracks, int rows, int active, string mode, int warmup, int measure, PerfSwitch perf)
        {
            using var game = new KindWorld(rows, library);
            if (mode != "eager")
            {
                using var all = game.Entities.CreateEntityQuery(typeof(Playback));
                game.Entities.AddComponent<TimelineHorizon>(all);
                if (mode == "lazy")
                {
                    game.Entities.AddComponent<TimelineClock>(all);
                    game.Entities.AddComponent<PlaybackDue>(all);
                }
            }
            game.Entities.SetComponentData(game.TickHandle, new TimelineGlide { Limit = mode == "eager" ? (byte)0 : (byte)global::Tl.Horizon.Max });
            var setup = Stopwatch.StartNew();
            for (var e = 0; e < rows; e++)
            {
                var requests = game.Entities.GetBuffer<TimelineRequest>(game.Rows[e]);
                for (var k = 0; k < active; k++)
                {
                    var timeline = (ushort)(1 + (e * 7 + k * 13) % TimelineCount);
                    requests.Add(TimelineRequest.Start(0, timeline, (ushort)((e * 31 + k * 17) % ShortestDuration)));
                }
            }
            game.Step();
            game.Entities.CompleteAllTrackedJobs();
            setup.Stop();
            for (var i = 0; i < warmup; i++)
                game.Step();
            game.Entities.CompleteAllTrackedJobs();
            var tick = new double[measure];
            var kinds = new double[measure];
            var toMs = 1000.0 / Stopwatch.Frequency;
            var before = GC.GetAllocatedBytesForCurrentThread();
            perf.Enable();
            for (var i = 0; i < measure; i++)
            {
                var start = Stopwatch.GetTimestamp();
                game.TickHandle.Update(game.World.Unmanaged);
                game.Entities.CompleteAllTrackedJobs();
                var ticked = Stopwatch.GetTimestamp();
                game.MovementHandle.Update(game.World.Unmanaged);
                game.VitalsHandle.Update(game.World.Unmanaged);
                game.Entities.CompleteAllTrackedJobs();
                tick[i] = (ticked - start) * toMs;
                kinds[i] = (Stopwatch.GetTimestamp() - ticked) * toMs;
            }
            perf.Disable();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var spans = LiveSpans(game);
            var kindMs = Median(kinds);
            var tickMs = Median(tick);
            Row(writer, string.Format(CultureInfo.InvariantCulture,
                "cell rows={0} active={1} live={2} mode={3} tickMs={4:F3} kindMs={5:F3} totalMs={6:F3} spans={7} wallNsPerSpan={8:F2} heapBytesPerRow={9} chunkBytesPerRow={10} setupMs={11:F1} alloc={12} blendSteps={13}/{14} hash={15:X16}",
                rows, active, liveTracks, mode, tickMs, kindMs, tickMs + kindMs, spans, spans == 0 ? 0 : (tickMs + kindMs) * 1e6 / spans, HeapBytes(game) / rows, ChunkBytes(game), setup.Elapsed.TotalMilliseconds, allocated, BlendSteps(game, out var steps), steps, Hash(game)));
        }

        static double Median(double[] values)
        {
            var copy = (double[])values.Clone();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }

        static long LiveSpans(KindWorld game)
        {
            game.Entities.CompleteAllTrackedJobs();
            long total = 0;
            foreach (var row in game.Rows)
                total += game.Entities.GetBuffer<LiftEntry>(row, true).Length + game.Entities.GetBuffer<LocomotionEntry>(row, true).Length;
            return total;
        }

        static long HeapBytes(KindWorld game)
        {
            long total = 0;
            foreach (var row in game.Rows)
            {
                total += Spilled(game.Entities.GetBuffer<Playback>(row, true).Capacity, 4, sizeof(Playback));
                total += Spilled(game.Entities.GetBuffer<LiftEntry>(row, true).Capacity, LiftKind.InlineCapacity, sizeof(LiftEntry));
                total += Spilled(game.Entities.GetBuffer<LocomotionEntry>(row, true).Capacity, LocomotionKind.InlineCapacity, sizeof(LocomotionEntry));
                total += Spilled(game.Entities.GetBuffer<DamageEntry>(row, true).Capacity, DamageKind.InlineCapacity, sizeof(DamageEntry));
                total += Spilled(game.Entities.GetBuffer<StatChangeEntry>(row, true).Capacity, StatChangeKind.InlineCapacity, sizeof(StatChangeEntry));
                if (game.Entities.HasComponent<PlaybackDue>(row))
                    total += Spilled(game.Entities.GetBuffer<PlaybackDue>(row, true).Capacity, 4, sizeof(PlaybackDue));
            }
            return total;
        }

        static long Spilled(int capacity, int inline, int size) => capacity > inline ? (long)capacity * size : 0;

        static int ChunkBytes(KindWorld game) => 16384 / game.Entities.GetChunk(game.Rows[0]).Capacity;

        static int BlendSteps(KindWorld game, out int steps)
        {
            using var query = game.Entities.CreateEntityQuery(typeof(TimelineLibrary));
            var index = query.GetSingleton<TimelineLibrary>().Kinds;
            var blended = 0;
            steps = 0;
            for (var i = 0; i < index.StepTableLength; i++)
            {
                var step = index.StepTable[i];
                if (step == null) continue;
                steps++;
                if (step->FactorSpan != 0) blended++;
            }
            return blended;
        }

        static string Dump(KindWorld game, ushort timeline)
        {
            using var query = game.Entities.CreateEntityQuery(typeof(TimelineLibrary));
            var index = query.GetSingleton<TimelineLibrary>().Kinds;
            var tk = index.Sec.Timelines + (timeline - 1);
            var windows = index.Sec.Windows(tk);
            var text = new StringBuilder();
            text.Append("dump timeline=").Append(timeline).Append(" duration=").Append(index.Timelines[timeline - 1].Duration).Append(" windows=").Append(tk->WindowCount).Append('\n');
            for (var w = 0; w < tk->WindowCount; w++)
            {
                text.Append("  window ").Append(w).Append(" track=").Append(windows[w].Track).Append(" pair=").Append(windows[w].Pair).Append(" ticks=").Append(windows[w].Start).Append("..").Append(windows[w].End).Append(" flags=").Append(windows[w].Flags);
                for (var k = index.PairStart[timeline - 1]; k < index.PairStart[timeline]; k++)
                    if ((int)(index.Pairs[k] >> 16) == w)
                    {
                        var feeder = index.Sec.Feeders + (index.Pairs[k] & 0xffff);
                        text.Append(" feeder=").Append(index.Pairs[k] & 0xffff).Append(" kind=").Append(index.Sec.NameOf(feeder->Kind)).Append(" trigger=").Append(feeder->Trigger);
                    }
                var step = index.WindowStep[index.WindowBase[timeline - 1] + w];
                if (step != null) text.Append(" step=").Append(step->WindowStart).Append("..").Append(step->WindowEnd).Append(" factor=").Append(step->FactorStart).Append('+').Append(step->FactorSpan);
                text.Append('\n');
            }
            return text.ToString();
        }

        static ulong Hash(KindWorld game)
        {
            game.Entities.CompleteAllTrackedJobs();
            var hash = 14695981039346656037ul;
            for (var row = 0; row < game.Rows.Length; row++)
            {
                var buffer = game.Entities.GetBuffer<Playback>(game.Rows[row], true);
                var clock = game.Entities.HasComponent<TimelineClock>(game.Rows[row]) ? game.Entities.GetComponentData<TimelineClock>(game.Rows[row]).Value : (ushort)0;
                for (var slot = 0; slot < buffer.Length; slot++)
                {
                    var copy = buffer[slot];
                    copy.Tick = TimelineTicks.Real(in copy, clock);
                    var data = (byte*)&copy;
                    for (var i = 0; i < sizeof(Playback); i++)
                        hash = (hash ^ data[i]) * 1099511628211ul;
                }
                var motion = game.Motion(row);
                hash = (hash ^ (ulong)BitConverter.SingleToInt32Bits(motion.X)) * 1099511628211ul;
                hash = (hash ^ (ulong)BitConverter.SingleToInt32Bits(motion.Y)) * 1099511628211ul;
                hash = (hash ^ (ulong)game.Health(row)) * 1099511628211ul;
                hash = (hash ^ (ulong)game.Stamina(row)) * 1099511628211ul;
            }
            return hash;
        }

        sealed class PerfSwitch
        {
            readonly string _control;
            readonly string _ack;

            public PerfSwitch(string control, string ack)
            {
                _control = control;
                _ack = ack;
            }

            public void Enable() => Send("enable");

            public void Disable() => Send("disable");

            void Send(string command)
            {
                if (_control == null) return;
                using (var control = new FileStream(_control, FileMode.Open, FileAccess.Write))
                {
                    var bytes = Encoding.ASCII.GetBytes(command + "\n");
                    control.Write(bytes, 0, bytes.Length);
                }
                if (_ack == null) return;
                using var ack = new FileStream(_ack, FileMode.Open, FileAccess.Read);
                var buffer = new byte[16];
                ack.Read(buffer, 0, buffer.Length);
            }
        }
    }
}
