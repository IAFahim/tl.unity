using System;
using Tl.Unity;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace KindGame
{
    [InternalBufferCapacity(0)]
    public struct ScoreBegin : IBufferElementData
    {
        public const ushort None = 0xFFFF;
        public const ushort Reentered = 0x8000;
        public const ushort TickMask = 0x7FFF;

        public ushort Value;

        public static bool Synthetic(ushort begin, uint window)
        {
            if (begin == None) return false;
            var tick = (uint)(begin & TickMask);
            var start = window & 0xFFFFu;
            var end = window >> 16;
            return tick >= start && tick < end && ((begin & Reentered) != 0 || tick != start);
        }
    }

    [BurstCompile]
    public unsafe struct ScoreBeginJob : IJobChunk
    {
        [ReadOnly] public BufferTypeHandle<Playback> PlaybackHandle;
        [ReadOnly] public ComponentTypeHandle<TimelineClock> ClockHandle;
        public BufferTypeHandle<ScoreBegin> BeginHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (!chunk.Has(ref BeginHandle)) return;
            var playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);
            var begins = chunk.GetBufferAccessor(ref BeginHandle);
            var clocks = chunk.Has(ref ClockHandle) ? (ushort*)chunk.GetRequiredComponentDataPtrRO(ref ClockHandle) : null;
            for (var row = 0; row < chunk.Count; row++)
            {
                var slots = (global::Tl.Playback*)playbacks[row].GetUnsafeReadOnlyPtr();
                var count = playbacks[row].Length;
                var begin = begins[row];
                if (begin.Length < count)
                {
                    var had = begin.Length;
                    begin.ResizeUninitialized(count);
                    for (var s = had; s < count; s++) begin[s] = new ScoreBegin { Value = ScoreBegin.None };
                }
                var values = (ushort*)begin.GetUnsafePtr();
                var clock = clocks == null ? (ushort)0 : clocks[row];
                for (var s = 0; s < count; s++)
                {
                    var slot = slots + s;
                    if (!ScoreSlots.Live(slot))
                    {
                        values[s] = ScoreBegin.None;
                        continue;
                    }
                    var tick = TimelineTicks.Real(slot, clock);
                    if (ScoreSlots.Began(slot))
                        values[s] = (ushort)(tick | ((slot->Flags & global::Tl.PlaybackFlags.ReenterPending) != 0 ? ScoreBegin.Reentered : 0));
                    else if (values[s] != ScoreBegin.None && tick < (values[s] & ScoreBegin.TickMask))
                        values[s] = ScoreBegin.None;
                }
            }
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct ScoreBeginSystem : ISystem
    {
        EntityQuery _query;
        BufferTypeHandle<Playback> _playbacks;
        ComponentTypeHandle<TimelineClock> _clocks;
        BufferTypeHandle<ScoreBegin> _begins;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = new EntityQueryBuilder(Allocator.Temp).WithAll<Playback, TimelineScored>().WithAllRW<ScoreBegin>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _playbacks = state.GetBufferTypeHandle<Playback>(true);
            _clocks = state.GetComponentTypeHandle<TimelineClock>(true);
            _begins = state.GetBufferTypeHandle<ScoreBegin>(false);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _playbacks.Update(ref state);
            _clocks.Update(ref state);
            _begins.Update(ref state);
            state.Dependency = new ScoreBeginJob { PlaybackHandle = _playbacks, ClockHandle = _clocks, BeginHandle = _begins }.ScheduleParallel(_query, state.Dependency);
        }
    }

    public static unsafe class ScoreSlots
    {
        public const global::Tl.PlaybackFlags Asleep = global::Tl.PlaybackFlags.StartPending | global::Tl.PlaybackFlags.Stopping | global::Tl.PlaybackFlags.Ended;

        public static bool Live(global::Tl.Playback* slot) => slot->Timeline != 0 && (slot->Flags & Asleep) == 0;

        public static bool Began(global::Tl.Playback* slot) => (slot->Flags & global::Tl.PlaybackFlags.Began) != 0;

        public static bool Paused(global::Tl.Playback* slot) => (slot->Flags & global::Tl.PlaybackFlags.Paused) != 0;

        public static bool Backward(global::Tl.Playback* slot, bool forward) => forward ? (slot->Flags & global::Tl.PlaybackFlags.Reverse) != 0 : (slot->Flags & global::Tl.PlaybackFlags.Reverse) == 0;

        public static global::Tl.FrameFlags FlagsAt(ushort tick, uint duration, bool backward, byte feedFlags)
        {
            var flags = (backward ? global::Tl.FrameFlags.Reverse : global::Tl.FrameFlags.None)
                | ((feedFlags & ScoreFeedFlags.Synthetic) != 0 ? global::Tl.FrameFlags.Synthetic : global::Tl.FrameFlags.None);
            if (tick == 0) flags |= global::Tl.FrameFlags.TimelineStart;
            if (tick == (ushort)(duration - 1)) flags |= global::Tl.FrameFlags.TimelineEnd;
            return flags;
        }

        public static float BlendAt(global::Tl.LibraryStep* step, ushort tick) => step->FactorSpan == 0 ? 0f : (step->FactorSpan <= 1 ? 0.5f : (tick - step->FactorStart) / (float)(step->FactorSpan - 1));
    }

    public static unsafe class ScoreBake
    {
        public static void WalkFrames(ref ScoreIndex score, in KindIndex index)
        {
            var kind = index.KindOf(LocomotionKind.Id);
            if (kind < 0) return;
            var lane = score.Lanes + kind;
            lane->FrameStride = sizeof(global::Tl.Frame<WalkTrack, WalkClip>);
            var bytes = (long)Math.Max(1, lane->Count) * lane->FrameStride;
            lane->Frames = (byte*)UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent);
            UnsafeUtility.MemClear(lane->Frames, bytes);
            for (var t = 0; t < index.TimelineCount; t++)
            {
                var duration = (ushort)index.Timelines[t].Duration;
                for (var tick = 0; tick < duration; tick++)
                {
                    var row = score.RowBase[t] + tick;
                    var key = row * score.KindCount + kind;
                    for (var f = score.Spans.First(key); f < score.Spans.End(key); f++)
                    {
                        if (score.Spans.Feeds[f].Case != 0) return;
                        var step = score.Spans.Feeds[f].Step;
                        var at = (ushort)tick;
                        var first = (byte*)step->FrameRow + sizeof(WalkTrack);
                        var second = step->FactorSpan == 0 ? null : first + sizeof(WalkClip);
                        global::TlUnityGenerated.TlUnityFrames.Frame_0_3((byte*)step->FrameRow, first, second, ScoreSlots.BlendAt(step, at), at, (ushort)(step->WindowEnd - step->WindowStart), (ushort)(at - step->WindowStart), (ushort)step->TrackIndex, duration, 1, global::Tl.FrameFlags.None, out _, out _, out var frame);
                        *(global::Tl.Frame<WalkTrack, WalkClip>*)(lane->Frames + (long)(lane->Start[row] + f - score.Spans.First(key)) * lane->FrameStride) = frame;
                    }
                }
            }
            score.FramedKinds |= 1ul << kind;
        }
    }

    public unsafe struct ScoreLanes
    {
        public const int Width = 8;
        const int Sleeping = (int)(ScoreSlots.Asleep | global::Tl.PlaybackFlags.Paused);

        public uint* Words;
        public int* WordOf;
        public int* LengthOf;
        public uint* ClockOf;
        public int* FirstOf;
        public int* FeedsOf;
        public int* BackwardOf;
        public int* RowBase;
        public int* Starts;
        public int Kinds;
        public int Kind;
        public bool Forward;

        public int DecodeScalar(int slot)
        {
            var most = 0;
            for (var l = 0; l < Width; l++)
            {
                var inRange = slot < LengthOf[l];
                var at = WordOf[l] + 2 * slot;
                var low = inRange ? Words[at] : 0u;
                var high = inRange ? Words[at + 1] : 0u;
                var timeline = (int)(low & 0xFFFFu);
                var stored = low >> 16;
                var flags = (int)(high >> 8) & 0xFF;
                var live = timeline != 0 && (flags & Sleeping) == 0;
                var reverse = (flags & 1) != 0;
                var plain = (high & 0x00FFFE00u) == 0;
                var tick = plain ? (reverse ? stored - ClockOf[l] : stored + ClockOf[l]) & 0xFFFFu : stored;
                var key = ((live ? RowBase[timeline - 1] : 0) + (int)tick) * Kinds + Kind;
                FirstOf[l] = live ? Starts[key] : 0;
                FeedsOf[l] = live ? Starts[key + 1] - FirstOf[l] : 0;
                BackwardOf[l] = reverse == Forward ? 1 : 0;
                if (FeedsOf[l] > most) most = FeedsOf[l];
            }
            return most;
        }

        public int DecodeAvx2(int slot)
        {
            var zero = global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(0);
            var one = global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(1);
            var inRange = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_cmpgt_epi32(global::Unity.Burst.Intrinsics.X86.Avx.mm256_loadu_si256(LengthOf), global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(slot));
            var at = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_add_epi32(global::Unity.Burst.Intrinsics.X86.Avx.mm256_loadu_si256(WordOf), global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(2 * slot));
            var low = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_mask_i32gather_epi32(zero, Words, at, inRange, 4);
            var high = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_mask_i32gather_epi32(zero, Words + 1, at, inRange, 4);
            var timeline = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_and_si256(low, global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(0xFFFF));
            var stored = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_srli_epi32(low, 16);
            var flags = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_and_si256(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_srli_epi32(high, 8), global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(0xFF));
            var awake = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_cmpeq_epi32(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_and_si256(flags, global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(Sleeping)), zero);
            var live = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_andnot_si256(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_cmpeq_epi32(timeline, zero), awake);
            var reverse = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_cmpeq_epi32(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_and_si256(flags, one), one);
            var plain = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_cmpeq_epi32(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_and_si256(high, global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(0x00FFFE00)), zero);
            var clock = global::Unity.Burst.Intrinsics.X86.Avx.mm256_loadu_si256(ClockOf);
            var moved = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_and_si256(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_blendv_epi8(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_add_epi32(stored, clock), global::Unity.Burst.Intrinsics.X86.Avx2.mm256_sub_epi32(stored, clock), reverse), global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(0xFFFF));
            var tick = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_blendv_epi8(stored, moved, plain);
            var origin = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_mask_i32gather_epi32(zero, RowBase - 1, timeline, live, 4);
            var key = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_add_epi32(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_mullo_epi32(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_add_epi32(origin, tick), global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(Kinds)), global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(Kind));
            var first = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_mask_i32gather_epi32(zero, Starts, key, live, 4);
            var end = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_mask_i32gather_epi32(zero, Starts + 1, key, live, 4);
            var feeds = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_sub_epi32(end, first);
            var backward = Forward ? reverse : global::Unity.Burst.Intrinsics.X86.Avx2.mm256_cmpeq_epi32(reverse, zero);
            global::Unity.Burst.Intrinsics.X86.Avx.mm256_storeu_si256(FirstOf, first);
            global::Unity.Burst.Intrinsics.X86.Avx.mm256_storeu_si256(FeedsOf, feeds);
            global::Unity.Burst.Intrinsics.X86.Avx.mm256_storeu_si256(BackwardOf, global::Unity.Burst.Intrinsics.X86.Avx2.mm256_and_si256(backward, one));
            var most = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_max_epi32(feeds, global::Unity.Burst.Intrinsics.X86.Avx2.mm256_permute2x128_si256(feeds, feeds, 1));
            most = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_max_epi32(most, global::Unity.Burst.Intrinsics.X86.Avx2.mm256_shuffle_epi32(most, 0x4E));
            most = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_max_epi32(most, global::Unity.Burst.Intrinsics.X86.Avx2.mm256_shuffle_epi32(most, 0xB1));
            return most.SInt0;
        }

        public static void GatherScalar(int feed, float* folds, int* firstOf, int* feedsOf, int* backwardOf, float* foldOf, int* takeOf)
        {
            for (var l = 0; l < Width; l++)
            {
                var take = feed < feedsOf[l];
                takeOf[l] = take ? -1 : 0;
                foldOf[l] = take ? folds[2 * (firstOf[l] + feed) + backwardOf[l]] : 0f;
            }
        }

        public static void GatherAvx2(int feed, float* folds, int* firstOf, int* feedsOf, int* backwardOf, float* foldOf, int* takeOf)
        {
            var at = global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(feed);
            var take = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_cmpgt_epi32(global::Unity.Burst.Intrinsics.X86.Avx.mm256_loadu_si256(feedsOf), at);
            var index = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_add_epi32(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_slli_epi32(global::Unity.Burst.Intrinsics.X86.Avx2.mm256_add_epi32(global::Unity.Burst.Intrinsics.X86.Avx.mm256_loadu_si256(firstOf), at), 1), global::Unity.Burst.Intrinsics.X86.Avx.mm256_loadu_si256(backwardOf));
            var fold = global::Unity.Burst.Intrinsics.X86.Avx2.mm256_mask_i32gather_ps(global::Unity.Burst.Intrinsics.X86.Avx.mm256_set1_epi32(0), folds, index, take, 4);
            global::Unity.Burst.Intrinsics.X86.Avx.mm256_storeu_ps(foldOf, fold);
            global::Unity.Burst.Intrinsics.X86.Avx.mm256_storeu_si256(takeOf, take);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe struct ScoreMovementJob : IJobChunk
    {
        [NativeDisableUnsafePtrRestriction] public ScoreIndex Score;
        [NativeDisableUnsafePtrRestriction] public KindIndex Index;
        public bool Forward;
        public bool Lanes;
        public int LaneWidth;
        public int LiftKindIndex;
        public int LocomotionKindIndex;
        public int StatChangeKindIndex;
        [ReadOnly] public BufferTypeHandle<Playback> PlaybackHandle;
        [ReadOnly] public ComponentTypeHandle<TimelineClock> ClockHandle;
        [ReadOnly] public BufferTypeHandle<ScoreBegin> BeginHandle;
        public ComponentTypeHandle<Movement_MotionData> MotionHandle;
        [ReadOnly] public ComponentTypeHandle<Movement_WalkStatData> WalkStatHandle;
        [ReadOnly] public ComponentTypeHandle<Movement_JumpPowerData> JumpPowerHandle;
        [ReadOnly] public ComponentTypeHandle<Movement_TargetData> TargetHandle;
        public ComponentTypeHandle<Movement_StaminaData> StaminaHandle;
        [ReadOnly] public ComponentTypeHandle<LiftEnabled> LiftEnabledHandle;
        [ReadOnly] public ComponentTypeHandle<LocomotionEnabled> LocomotionEnabledHandle;
        [ReadOnly] public ComponentTypeHandle<StatChangeEnabled> StatChangeEnabledHandle;
        public BufferTypeHandle<StatChangeEntry> StatChangeEntryHandle;
        public ComponentTypeHandle<StatChangeActive> StatChangeActiveHandle;
        public AnchorLookup Anchors;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);
            var clocks = chunk.Has(ref ClockHandle) ? (ushort*)chunk.GetRequiredComponentDataPtrRO(ref ClockHandle) : null;
            var motion = chunk.Has(ref MotionHandle) ? (Motion*)chunk.GetRequiredComponentDataPtrRW(ref MotionHandle) : null;
            var walkStat = chunk.Has(ref WalkStatHandle) ? (WalkStat*)chunk.GetRequiredComponentDataPtrRO(ref WalkStatHandle) : null;
            var jumpPower = chunk.Has(ref JumpPowerHandle) ? (JumpPower*)chunk.GetRequiredComponentDataPtrRO(ref JumpPowerHandle) : null;
            var target = chunk.Has(ref TargetHandle) ? (Target*)chunk.GetRequiredComponentDataPtrRO(ref TargetHandle) : null;
            var stamina = chunk.Has(ref StaminaHandle) ? (Stamina*)chunk.GetRequiredComponentDataPtrRW(ref StaminaHandle) : null;
            if (motion != null && jumpPower != null && chunk.Has(ref LiftEnabledHandle))
            {
                if (Lanes && (Score.FoldedKinds & (1ul << LiftKindIndex)) != 0)
                    LiftLanes(in chunk, playbacks, clocks, motion, jumpPower, chunk.GetEnableableBits(ref LiftEnabledHandle));
                else
                    Lift(in chunk, playbacks, clocks, motion, jumpPower, chunk.GetEnableableBits(ref LiftEnabledHandle));
            }
            if (motion != null && walkStat != null && target != null && chunk.Has(ref LocomotionEnabledHandle))
                Locomotion(in chunk, playbacks, clocks, chunk.Has(ref BeginHandle) ? chunk.GetBufferAccessor(ref BeginHandle) : default, motion, walkStat, target, chunk.GetEnableableBits(ref LocomotionEnabledHandle));
            if (stamina != null && chunk.Has(ref StatChangeEnabledHandle))
                StatChange(in chunk, playbacks, clocks, stamina, chunk.GetEnableableBits(ref StatChangeEnabledHandle));
            if (stamina != null && chunk.Has(ref StatChangeEntryHandle) && chunk.Has(ref StatChangeEnabledHandle))
                SentStatChanges(in chunk, stamina, chunk.GetEnableableBits(ref StatChangeEnabledHandle));
        }

        void Lift(in ArchetypeChunk chunk, BufferAccessor<Playback> playbacks, ushort* clocks, Motion* motion, JumpPower* jumpPower, v128 enabled)
        {
            var count = chunk.Count;
            var rows = stackalloc int[128];
            var rowCount = 0;
            for (var word = 0; word < 2; word++)
            {
                var bits = word == 0 ? enabled.ULong0 : enabled.ULong1;
                while (bits != 0)
                {
                    var row = (word << 6) + (int)global::Unity.Mathematics.math.tzcnt(bits);
                    bits &= bits - 1;
                    if (row < count) rows[rowCount++] = row;
                }
            }
            LiftRows(playbacks, clocks, motion, jumpPower, rows, rowCount);
        }

        void LiftRows(BufferAccessor<Playback> playbacks, ushort* clocks, Motion* motion, JumpPower* jumpPower, int* rows, int rowCount)
        {
            for (var r = 0; r < rowCount; r++)
            {
                var row = rows[r];
                var slots = (global::Tl.Playback*)playbacks[row].GetUnsafeReadOnlyPtr();
                var slotCount = playbacks[row].Length;
                var clock = clocks == null ? (ushort)0 : clocks[row];
                float total = default;
                var has = false;
                for (var s = 0; s < slotCount; s++)
                {
                    var slot = slots + s;
                    if (!ScoreSlots.Live(slot) || ScoreSlots.Paused(slot)) continue;
                    var tick = TimelineTicks.Real(slot, clock);
                    var backward = ScoreSlots.Backward(slot, Forward);
                    var key = Score.KeyOf(slot->Timeline, tick, LiftKindIndex);
                    var end = Score.Spans.End(key);
                    for (var f = Score.Spans.First(key); f < end; f++)
                    {
                        var feed = Score.Spans.Feeds + f;
                        var step = feed->Step;
                        float lift;
                        if (step->FoldLane == null)
                        {
                            var timeline = Index.Timelines + (slot->Timeline - 1);
                            var first = (byte*)step->FrameRow + sizeof(ArcTrack);
                            var second = step->FactorSpan == 0 ? null : first + sizeof(ArcClip);
                            global::TlUnityGenerated.TlUnityFrames.Frame_0_1((byte*)step->FrameRow, first, second, ScoreSlots.BlendAt(step, tick), tick, (ushort)(step->WindowEnd - step->WindowStart), (ushort)(tick - step->WindowStart), (ushort)step->TrackIndex, (ushort)timeline->Duration, backward ? (sbyte)-1 : (sbyte)1, ScoreSlots.FlagsAt(tick, timeline->Duration, backward, feed->Flags), out _, out _, out var frame);
                            Jump.Fold(in frame, out lift);
                        }
                        else
                            lift = *(float*)((byte*)step->FoldLane + (((uint)tick - step->WindowStart) * 2u + (backward ? 1u : 0u)) * 4u);
                        var value = Jump.Evaluate(in lift, in jumpPower[row]);
                        if (has) global::KindGame.Lift.Combine(ref total, in value);
                        else
                        {
                            total = value;
                            has = true;
                        }
                    }
                }
                if (has) global::KindGame.Lift.Apply(in total, ref motion[row]);
            }
        }

        [global::Unity.Burst.CompilerServices.SkipLocalsInit]
        void LiftLanes(in ArchetypeChunk chunk, BufferAccessor<Playback> playbacks, ushort* clocks, Motion* motion, JumpPower* jumpPower, v128 enabled)
        {
            const int width = 8;
            var count = chunk.Count;
            var rows = stackalloc int[128];
            var rowCount = 0;
            for (var word = 0; word < 2; word++)
            {
                var bits = word == 0 ? enabled.ULong0 : enabled.ULong1;
                while (bits != 0)
                {
                    var row = (word << 6) + (int)global::Unity.Mathematics.math.tzcnt(bits);
                    bits &= bits - 1;
                    if (row < count) rows[rowCount++] = row;
                }
            }
            var wordOf = stackalloc int[width];
            var lengthOf = stackalloc int[width];
            var clockOf = stackalloc uint[width];
            var powerOf = stackalloc JumpPower[width];
            var totalOf = stackalloc float[width];
            var hasOf = stackalloc int[width];
            var firstOf = stackalloc int[width];
            var feedsOf = stackalloc int[width];
            var backwardOf = stackalloc int[width];
            var foldOf = stackalloc float[width];
            var takeOf = stackalloc int[width];
            var folds = (float*)Score.Lanes[LiftKindIndex].Fold;
            var starts = Score.Lanes[LiftKindIndex].Start;
            var rowBase = Score.RowBase;
            const int kinds = 1;
            const int kind = 0;
            for (var group = 0; group < rowCount; group += width)
            {
                var lanes = rowCount - group < width ? rowCount - group : width;
                var longest = 0;
                var words = (uint*)playbacks[rows[group]].GetUnsafeReadOnlyPtr();
                var near = true;
                for (var l = 0; l < lanes; l++)
                {
                    var distance = ((long)(byte*)playbacks[rows[group + l]].GetUnsafeReadOnlyPtr() - (long)(byte*)words) / 4;
                    near &= distance > int.MinValue / 2 && distance < int.MaxValue / 2;
                }
                if (!near)
                {
                    LiftRows(playbacks, clocks, motion, jumpPower, rows + group, lanes);
                    continue;
                }
                for (var l = 0; l < width; l++)
                {
                    if (l < lanes)
                    {
                        var row = rows[group + l];
                        wordOf[l] = (int)(((long)(byte*)playbacks[row].GetUnsafeReadOnlyPtr() - (long)(byte*)words) / 4);
                        lengthOf[l] = playbacks[row].Length;
                        clockOf[l] = clocks == null ? 0u : clocks[row];
                        powerOf[l] = jumpPower[row];
                    }
                    else
                    {
                        wordOf[l] = 0;
                        lengthOf[l] = 0;
                        clockOf[l] = 0;
                        powerOf[l] = default;
                    }
                    totalOf[l] = 0f;
                    hasOf[l] = 0;
                    if (lengthOf[l] > longest) longest = lengthOf[l];
                }
                for (var s = 0; s < longest; s++)
                {
                    var lanesOfSlot = new ScoreLanes { Words = words, WordOf = wordOf, LengthOf = lengthOf, ClockOf = clockOf, FirstOf = firstOf, FeedsOf = feedsOf, BackwardOf = backwardOf, RowBase = rowBase, Starts = starts, Kinds = kinds, Kind = kind, Forward = Forward };
                    var most = global::Unity.Burst.Intrinsics.X86.Avx2.IsAvx2Supported ? lanesOfSlot.DecodeAvx2(s) : lanesOfSlot.DecodeScalar(s);
                    for (var j = 0; j < most; j++)
                    {
                        if (global::Unity.Burst.Intrinsics.X86.Avx2.IsAvx2Supported) ScoreLanes.GatherAvx2(j, folds, firstOf, feedsOf, backwardOf, foldOf, takeOf);
                        else ScoreLanes.GatherScalar(j, folds, firstOf, feedsOf, backwardOf, foldOf, takeOf);
                        for (var l = 0; l < LaneWidth; l++)
                        {
                            global::Unity.Burst.CompilerServices.Loop.ExpectVectorized();
                            var take = takeOf[l] != 0;
                            var value = Jump.Evaluate(in foldOf[l], in powerOf[l]);
                            var combined = totalOf[l];
                            global::KindGame.Lift.Combine(ref combined, in value);
                            var next = hasOf[l] != 0 ? combined : value;
                            totalOf[l] = take ? next : totalOf[l];
                            hasOf[l] |= take ? 1 : 0;
                        }
                    }
                }
                for (var l = 0; l < lanes; l++)
                    if (hasOf[l] != 0) global::KindGame.Lift.Apply(in totalOf[l], ref motion[rows[group + l]]);
            }
        }

        void Locomotion(in ArchetypeChunk chunk, BufferAccessor<Playback> playbacks, ushort* clocks, BufferAccessor<ScoreBegin> begins, Motion* motion, WalkStat* walkStat, Target* target, v128 enabled)
        {
            var count = chunk.Count;
            var hasBegins = begins.Length != 0;
            var framed = Lanes && (Score.FramedKinds & (1ul << LocomotionKindIndex)) != 0;
            for (var word = 0; word < 2; word++)
            {
                var bits = word == 0 ? enabled.ULong0 : enabled.ULong1;
                while (bits != 0)
                {
                    var row = (word << 6) + (int)global::Unity.Mathematics.math.tzcnt(bits);
                    bits &= bits - 1;
                    if (row >= count) break;
                    var slots = (global::Tl.Playback*)playbacks[row].GetUnsafeReadOnlyPtr();
                    var slotCount = playbacks[row].Length;
                    var clock = clocks == null ? (ushort)0 : clocks[row];
                    var beginOf = hasBegins && begins[row].Length >= slotCount ? (ushort*)begins[row].GetUnsafeReadOnlyPtr() : null;
                    Speed total = default;
                    var has = false;
                    for (var s = 0; s < slotCount; s++)
                    {
                        var slot = slots + s;
                        if (!ScoreSlots.Live(slot) || ScoreSlots.Paused(slot)) continue;
                        var tick = TimelineTicks.Real(slot, clock);
                        var backward = ScoreSlots.Backward(slot, Forward);
                        var timeline = Index.Timelines + (slot->Timeline - 1);
                        if (framed)
                        {
                            var lane = Score.Lanes + LocomotionKindIndex;
                            var at = Score.RowOf(slot->Timeline, tick);
                            var extra = ScoreSlots.FlagsAt(tick, timeline->Duration, backward, 0);
                            var begin = beginOf == null ? ScoreBegin.None : beginOf[s];
                            var stop = lane->Start[at + 1];
                            for (var i = lane->Start[at]; i < stop; i++)
                            {
                                var baked = (global::Tl.Frame<WalkTrack, WalkClip>*)(lane->Frames + (long)i * lane->FrameStride);
                                var synthetic = ScoreBegin.Synthetic(begin, lane->Window[i]) ? global::Tl.FrameFlags.Synthetic : global::Tl.FrameFlags.None;
                                var frame = new global::Tl.Frame<WalkTrack, WalkClip>(in baked->Track, in baked->Clip, baked->TimelineTick, baked->ClipLength, baked->WithinClip, baked->TrackIndex, baked->Flags | extra | synthetic);
                                var value = Walk.Evaluate(in frame, in walkStat[row], in target[row], in Anchors);
                                if (has) global::KindGame.Locomotion.Combine(ref total, in value);
                                else
                                {
                                    total = value;
                                    has = true;
                                }
                            }
                            continue;
                        }
                        var key = Score.KeyOf(slot->Timeline, tick, LocomotionKindIndex);
                        var end = Score.Spans.End(key);
                        var windows = Index.Sec.Windows(Index.Sec.Timelines + (slot->Timeline - 1));
                        var beginTick = beginOf == null ? ScoreBegin.None : beginOf[s];
                        for (var f = Score.Spans.First(key); f < end; f++)
                        {
                            var feed = Score.Spans.Feeds + f;
                            var step = feed->Step;
                            var first = (byte*)step->FrameRow + sizeof(WalkTrack);
                            var second = step->FactorSpan == 0 ? null : first + sizeof(WalkClip);
                            var window = windows[feed->Window];
                            var spanFlags = ScoreBegin.Synthetic(beginTick, window.Start | (uint)window.End << 16) ? ScoreFeedFlags.Synthetic : feed->Flags;
                            global::TlUnityGenerated.TlUnityFrames.Frame_0_3((byte*)step->FrameRow, first, second, ScoreSlots.BlendAt(step, tick), tick, (ushort)(step->WindowEnd - step->WindowStart), (ushort)(tick - step->WindowStart), (ushort)step->TrackIndex, (ushort)timeline->Duration, backward ? (sbyte)-1 : (sbyte)1, ScoreSlots.FlagsAt(tick, timeline->Duration, backward, spanFlags), out _, out _, out var frame);
                            var value = Walk.Evaluate(in frame, in walkStat[row], in target[row], in Anchors);
                            if (has) global::KindGame.Locomotion.Combine(ref total, in value);
                            else
                            {
                                total = value;
                                has = true;
                            }
                        }
                    }
                    if (has) global::KindGame.Locomotion.Apply(in total, ref motion[row]);
                }
            }
        }

        void SentStatChanges(in ArchetypeChunk chunk, Stamina* stamina, v128 enabled)
        {
            var sent = chunk.GetBufferAccessor(ref StatChangeEntryHandle);
            var active = chunk.GetEnabledMask(ref StatChangeActiveHandle);
            for (var row = 0; row < chunk.Count; row++)
            {
                var queue = sent[row];
                if (queue.Length == 0) continue;
                if (((row < 64 ? enabled.ULong0 >> row : enabled.ULong1 >> (row - 64)) & 1ul) == 0) continue;
                var data = (byte*)queue.GetUnsafeReadOnlyPtr();
                for (var i = 0; i < queue.Length; i++)
                {
                    var entry = (global::Tl.KindEntry*)(data + (long)i * sizeof(StatChangeEntry));
                    if (entry->Timeline != 0) continue;
                    var value = *(int*)((byte*)entry + 8);
                    global::KindGame.StatChange.Apply(in value, ref stamina[row]);
                }
                queue.Clear();
                active[row] = false;
            }
        }

        void StatChange(in ArchetypeChunk chunk, BufferAccessor<Playback> playbacks, ushort* clocks, Stamina* stamina, v128 enabled)
        {
            var count = chunk.Count;
            for (var word = 0; word < 2; word++)
            {
                var bits = word == 0 ? enabled.ULong0 : enabled.ULong1;
                while (bits != 0)
                {
                    var row = (word << 6) + (int)global::Unity.Mathematics.math.tzcnt(bits);
                    bits &= bits - 1;
                    if (row >= count) break;
                    var slots = (global::Tl.Playback*)playbacks[row].GetUnsafeReadOnlyPtr();
                    var slotCount = playbacks[row].Length;
                    var clock = clocks == null ? (ushort)0 : clocks[row];
                    for (var s = 0; s < slotCount; s++)
                    {
                        var slot = slots + s;
                        if (!ScoreSlots.Live(slot)) continue;
                        var tick = TimelineTicks.Real(slot, clock);
                        var backward = ScoreSlots.Backward(slot, Forward);
                        var timeline = Index.Timelines + (slot->Timeline - 1);
                        var pulses = ScoreSlots.Began(slot) ? Score.Begins : Score.Arrivals;
                        var key = Score.KeyOf(slot->Timeline, tick, StatChangeKindIndex);
                        var end = pulses.End(key);
                        for (var f = pulses.First(key); f < end; f++)
                        {
                            var feed = pulses.Feeds + f;
                            var flags = ScoreSlots.FlagsAt(tick, timeline->Duration, backward, feed->Flags);
                            int value;
                            if (feed->Case == 0)
                            {
                                var step = feed->Step;
                                if (step == null) continue;
                                var first = (byte*)step->FrameRow + sizeof(StatTrack);
                                var second = step->FactorSpan == 0 ? null : first + sizeof(StatClip);
                                global::TlUnityGenerated.TlUnityFrames.Frame_0_0((byte*)step->FrameRow, first, second, ScoreSlots.BlendAt(step, tick), tick, (ushort)(step->WindowEnd - step->WindowStart), (ushort)(tick - step->WindowStart), (ushort)step->TrackIndex, (ushort)timeline->Duration, backward ? (sbyte)-1 : (sbyte)1, flags, out _, out _, out var frame);
                                value = ChangeStat.Start(in frame);
                            }
                            else
                            {
                                var marker = Index.MarkerOf(slot->Timeline, feed->Window);
                                if (marker == null) continue;
                                var stepMarker = *(StepMarker*)marker;
                                var markerFrame = new global::Tl.MarkerFrame<StepMarker>(in stepMarker, tick, 0, flags);
                                value = Steps.OnStep(in markerFrame);
                            }
                            global::KindGame.StatChange.Apply(in value, ref stamina[row]);
                        }
                    }
                }
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    public unsafe struct ScoreVitalsJob : IJobChunk
    {
        [NativeDisableUnsafePtrRestriction] public ScoreIndex Score;
        [NativeDisableUnsafePtrRestriction] public KindIndex Index;
        public bool Forward;
        public int DamageKindIndex;
        [ReadOnly] public BufferTypeHandle<Playback> PlaybackHandle;
        [ReadOnly] public ComponentTypeHandle<TimelineClock> ClockHandle;
        public ComponentTypeHandle<Vitals_HealthData> HealthHandle;
        [ReadOnly] public ComponentTypeHandle<DamageEnabled> DamageEnabledHandle;
        public BufferTypeHandle<DamageEntry> DamageEntryHandle;
        public ComponentTypeHandle<DamageActive> DamageActiveHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (!chunk.Has(ref HealthHandle) || !chunk.Has(ref DamageEnabledHandle)) return;
            var playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);
            var clocks = chunk.Has(ref ClockHandle) ? (ushort*)chunk.GetRequiredComponentDataPtrRO(ref ClockHandle) : null;
            var health = (Health*)chunk.GetRequiredComponentDataPtrRW(ref HealthHandle);
            var enabled = chunk.GetEnableableBits(ref DamageEnabledHandle);
            var count = chunk.Count;
            var sent = chunk.Has(ref DamageEntryHandle) ? chunk.GetBufferAccessor(ref DamageEntryHandle) : default;
            var active = chunk.Has(ref DamageEntryHandle) ? chunk.GetEnabledMask(ref DamageActiveHandle) : default;
            for (var word = 0; word < 2; word++)
            {
                var bits = word == 0 ? enabled.ULong0 : enabled.ULong1;
                while (bits != 0)
                {
                    var row = (word << 6) + (int)global::Unity.Mathematics.math.tzcnt(bits);
                    bits &= bits - 1;
                    if (row >= count) break;
                    var slots = (global::Tl.Playback*)playbacks[row].GetUnsafeReadOnlyPtr();
                    var slotCount = playbacks[row].Length;
                    var clock = clocks == null ? (ushort)0 : clocks[row];
                    int total = default;
                    var has = false;
                    for (var s = 0; s < slotCount; s++)
                    {
                        var slot = slots + s;
                        if (!ScoreSlots.Live(slot)) continue;
                        var tick = TimelineTicks.Real(slot, clock);
                        var backward = ScoreSlots.Backward(slot, Forward);
                        var timeline = Index.Timelines + (slot->Timeline - 1);
                        var pulses = ScoreSlots.Began(slot) ? Score.Begins : Score.Arrivals;
                        var key = Score.KeyOf(slot->Timeline, tick, DamageKindIndex);
                        var end = pulses.End(key);
                        for (var f = pulses.First(key); f < end; f++)
                        {
                            var feed = pulses.Feeds + f;
                            var step = feed->Step;
                            if (step == null) continue;
                            var first = (byte*)step->FrameRow + sizeof(HitTrack);
                            var second = step->FactorSpan == 0 ? null : first + sizeof(HitClip);
                            global::TlUnityGenerated.TlUnityFrames.Frame_1_0((byte*)step->FrameRow, first, second, ScoreSlots.BlendAt(step, tick), tick, (ushort)(step->WindowEnd - step->WindowStart), (ushort)(tick - step->WindowStart), (ushort)step->TrackIndex, (ushort)timeline->Duration, backward ? (sbyte)-1 : (sbyte)1, ScoreSlots.FlagsAt(tick, timeline->Duration, backward, feed->Flags), out _, out _, out var frame);
                            var value = Hit.Start(in frame);
                            if (has) global::KindGame.Damage.Combine(ref total, in value);
                            else
                            {
                                total = value;
                                has = true;
                            }
                        }
                    }
                    if (sent.Length != 0 && sent[row].Length != 0)
                    {
                        var queue = sent[row];
                        var data = (byte*)queue.GetUnsafeReadOnlyPtr();
                        for (var i = 0; i < queue.Length; i++)
                        {
                            var entry = (global::Tl.KindEntry*)(data + (long)i * sizeof(DamageEntry));
                            if (entry->Timeline != 0) continue;
                            var value = *(int*)((byte*)entry + 8);
                            if (has) global::KindGame.Damage.Combine(ref total, in value);
                            else
                            {
                                total = value;
                                has = true;
                            }
                        }
                        queue.Clear();
                        active[row] = false;
                    }
                    if (has) global::KindGame.Damage.Apply(in total, ref health[row]);
                }
            }
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct ScoreMovementSystem : ISystem
    {
        EntityQuery _query;
        BufferTypeHandle<Playback> _playbacks;
        ComponentTypeHandle<TimelineClock> _clocks;
        BufferTypeHandle<ScoreBegin> _begins;
        ComponentTypeHandle<Movement_MotionData> _motion;
        ComponentTypeHandle<Movement_WalkStatData> _walkStat;
        ComponentTypeHandle<Movement_JumpPowerData> _jumpPower;
        ComponentTypeHandle<Movement_TargetData> _target;
        ComponentTypeHandle<Movement_StaminaData> _stamina;
        ComponentTypeHandle<LiftEnabled> _liftEnabled;
        ComponentTypeHandle<LocomotionEnabled> _locomotionEnabled;
        ComponentTypeHandle<StatChangeEnabled> _statChangeEnabled;
        BufferTypeHandle<StatChangeEntry> _statChangeEntries;
        ComponentTypeHandle<StatChangeActive> _statChangeActive;
        ComponentLookup<Anchor> _anchors;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = new EntityQueryBuilder(Allocator.Temp).WithAll<Playback, TimelineScored>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _playbacks = state.GetBufferTypeHandle<Playback>(true);
            _clocks = state.GetComponentTypeHandle<TimelineClock>(true);
            _begins = state.GetBufferTypeHandle<ScoreBegin>(true);
            _motion = state.GetComponentTypeHandle<Movement_MotionData>(false);
            _walkStat = state.GetComponentTypeHandle<Movement_WalkStatData>(true);
            _jumpPower = state.GetComponentTypeHandle<Movement_JumpPowerData>(true);
            _target = state.GetComponentTypeHandle<Movement_TargetData>(true);
            _stamina = state.GetComponentTypeHandle<Movement_StaminaData>(false);
            _liftEnabled = state.GetComponentTypeHandle<LiftEnabled>(true);
            _locomotionEnabled = state.GetComponentTypeHandle<LocomotionEnabled>(true);
            _statChangeEnabled = state.GetComponentTypeHandle<StatChangeEnabled>(true);
            _statChangeEntries = state.GetBufferTypeHandle<StatChangeEntry>(false);
            _statChangeActive = state.GetComponentTypeHandle<StatChangeActive>(false);
            _anchors = state.GetComponentLookup<Anchor>(true);
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<ScoreLibrary>();
            state.RequireForUpdate<TimelineStep>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var score = SystemAPI.GetSingleton<ScoreLibrary>().Index;
            _playbacks.Update(ref state);
            _clocks.Update(ref state);
            _begins.Update(ref state);
            _motion.Update(ref state);
            _walkStat.Update(ref state);
            _jumpPower.Update(ref state);
            _target.Update(ref state);
            _stamina.Update(ref state);
            _liftEnabled.Update(ref state);
            _locomotionEnabled.Update(ref state);
            _statChangeEnabled.Update(ref state);
            _statChangeEntries.Update(ref state);
            _statChangeActive.Update(ref state);
            _anchors.Update(ref state);
            var job = new ScoreMovementJob
            {
                Score = score,
                Index = library.Kinds,
                Forward = SystemAPI.GetSingleton<TimelineStep>().Forward,
                Lanes = SystemAPI.GetSingleton<ScoreLibrary>().Lanes,
                LaneWidth = 8,
                LiftKindIndex = library.Kinds.KindOf(LiftKind.Id),
                LocomotionKindIndex = library.Kinds.KindOf(LocomotionKind.Id),
                StatChangeKindIndex = library.Kinds.KindOf(StatChangeKind.Id),
                PlaybackHandle = _playbacks,
                ClockHandle = _clocks,
                BeginHandle = _begins,
                MotionHandle = _motion,
                WalkStatHandle = _walkStat,
                JumpPowerHandle = _jumpPower,
                TargetHandle = _target,
                StaminaHandle = _stamina,
                LiftEnabledHandle = _liftEnabled,
                LocomotionEnabledHandle = _locomotionEnabled,
                StatChangeEnabledHandle = _statChangeEnabled,
                StatChangeEntryHandle = _statChangeEntries,
                StatChangeActiveHandle = _statChangeActive,
                Anchors = new AnchorLookup { Anchors = _anchors },
            };
            state.Dependency = job.ScheduleParallel(_query, state.Dependency);
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct ScoreVitalsSystem : ISystem
    {
        EntityQuery _query;
        BufferTypeHandle<Playback> _playbacks;
        ComponentTypeHandle<TimelineClock> _clocks;
        ComponentTypeHandle<Vitals_HealthData> _health;
        ComponentTypeHandle<DamageEnabled> _damageEnabled;
        BufferTypeHandle<DamageEntry> _damageEntries;
        ComponentTypeHandle<DamageActive> _damageActive;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = new EntityQueryBuilder(Allocator.Temp).WithAll<Playback, TimelineScored>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _playbacks = state.GetBufferTypeHandle<Playback>(true);
            _clocks = state.GetComponentTypeHandle<TimelineClock>(true);
            _health = state.GetComponentTypeHandle<Vitals_HealthData>(false);
            _damageEnabled = state.GetComponentTypeHandle<DamageEnabled>(true);
            _damageEntries = state.GetBufferTypeHandle<DamageEntry>(false);
            _damageActive = state.GetComponentTypeHandle<DamageActive>(false);
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<ScoreLibrary>();
            state.RequireForUpdate<TimelineStep>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            _playbacks.Update(ref state);
            _clocks.Update(ref state);
            _health.Update(ref state);
            _damageEnabled.Update(ref state);
            _damageEntries.Update(ref state);
            _damageActive.Update(ref state);
            var job = new ScoreVitalsJob
            {
                Score = SystemAPI.GetSingleton<ScoreLibrary>().Index,
                Index = library.Kinds,
                Forward = SystemAPI.GetSingleton<TimelineStep>().Forward,
                DamageKindIndex = library.Kinds.KindOf(DamageKind.Id),
                PlaybackHandle = _playbacks,
                ClockHandle = _clocks,
                HealthHandle = _health,
                DamageEnabledHandle = _damageEnabled,
                DamageEntryHandle = _damageEntries,
                DamageActiveHandle = _damageActive,
            };
            state.Dependency = job.ScheduleParallel(_query, state.Dependency);
        }
    }
}
