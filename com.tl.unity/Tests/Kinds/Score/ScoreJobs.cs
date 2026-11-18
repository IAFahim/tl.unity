using Tl.Unity;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace KindGame
{
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
        public ComponentTypeHandle<Movement_MotionData> MotionHandle;
        [ReadOnly] public ComponentTypeHandle<Movement_WalkStatData> WalkStatHandle;
        [ReadOnly] public ComponentTypeHandle<Movement_JumpPowerData> JumpPowerHandle;
        [ReadOnly] public ComponentTypeHandle<Movement_TargetData> TargetHandle;
        public ComponentTypeHandle<Movement_StaminaData> StaminaHandle;
        [ReadOnly] public ComponentTypeHandle<LiftEnabled> LiftEnabledHandle;
        [ReadOnly] public ComponentTypeHandle<LocomotionEnabled> LocomotionEnabledHandle;
        [ReadOnly] public ComponentTypeHandle<StatChangeEnabled> StatChangeEnabledHandle;
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
                Locomotion(in chunk, playbacks, clocks, motion, walkStat, target, chunk.GetEnableableBits(ref LocomotionEnabledHandle));
            if (stamina != null && chunk.Has(ref StatChangeEnabledHandle))
                StatChange(in chunk, playbacks, clocks, stamina, chunk.GetEnableableBits(ref StatChangeEnabledHandle));
        }

        void Lift(in ArchetypeChunk chunk, BufferAccessor<Playback> playbacks, ushort* clocks, Motion* motion, JumpPower* jumpPower, v128 enabled)
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
            var slotsOf = stackalloc ulong*[width];
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
            var folds = (float*)Score.FoldBits;
            var starts = Score.Spans.Start;
            var rowBase = Score.RowBase;
            var kinds = Score.KindCount;
            var kind = LiftKindIndex;
            for (var group = 0; group < rowCount; group += width)
            {
                var lanes = rowCount - group < width ? rowCount - group : width;
                var longest = 0;
                for (var l = 0; l < width; l++)
                {
                    if (l < lanes)
                    {
                        var row = rows[group + l];
                        slotsOf[l] = (ulong*)playbacks[row].GetUnsafeReadOnlyPtr();
                        lengthOf[l] = playbacks[row].Length;
                        clockOf[l] = clocks == null ? 0u : clocks[row];
                        powerOf[l] = jumpPower[row];
                    }
                    else
                    {
                        slotsOf[l] = null;
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
                    var most = 0;
                    for (var l = 0; l < width; l++)
                    {
                        feedsOf[l] = 0;
                        if (s >= lengthOf[l]) continue;
                        var word = slotsOf[l][s];
                        var timeline = (int)(word & global::Tl.SlotWord.TimelineMask);
                        var flags = (global::Tl.PlaybackFlags)(byte)(word >> 40);
                        if (timeline == 0 || (flags & (ScoreSlots.Asleep | global::Tl.PlaybackFlags.Paused)) != 0) continue;
                        var stored = (uint)(word >> 16) & 0xFFFFu;
                        var reverse = (flags & global::Tl.PlaybackFlags.Reverse) != 0;
                        var tick = (word & global::Tl.SlotWord.NotPlainMask) == 0 ? (reverse ? stored - clockOf[l] : stored + clockOf[l]) & 0xFFFFu : stored;
                        var key = (rowBase[timeline - 1] + (int)tick) * kinds + kind;
                        firstOf[l] = starts[key];
                        feedsOf[l] = starts[key + 1] - firstOf[l];
                        backwardOf[l] = (Forward ? reverse : !reverse) ? 1 : 0;
                        if (feedsOf[l] > most) most = feedsOf[l];
                    }
                    for (var j = 0; j < most; j++)
                    {
                        for (var l = 0; l < width; l++)
                        {
                            takeOf[l] = j < feedsOf[l] ? 1 : 0;
                            foldOf[l] = takeOf[l] != 0 ? folds[2 * (firstOf[l] + j) + backwardOf[l]] : 0f;
                        }
                        for (var l = 0; l < LaneWidth; l++)
                        {
                            global::Unity.Burst.CompilerServices.Loop.ExpectVectorized();
                            var value = Jump.Evaluate(in foldOf[l], in powerOf[l]);
                            var combined = totalOf[l];
                            global::KindGame.Lift.Combine(ref combined, in value);
                            var next = hasOf[l] != 0 ? combined : value;
                            totalOf[l] = takeOf[l] != 0 ? next : totalOf[l];
                            hasOf[l] |= takeOf[l];
                        }
                    }
                }
                for (var l = 0; l < lanes; l++)
                    if (hasOf[l] != 0) global::KindGame.Lift.Apply(in totalOf[l], ref motion[rows[group + l]]);
            }
        }

        void Locomotion(in ArchetypeChunk chunk, BufferAccessor<Playback> playbacks, ushort* clocks, Motion* motion, WalkStat* walkStat, Target* target, v128 enabled)
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
                    Speed total = default;
                    var has = false;
                    for (var s = 0; s < slotCount; s++)
                    {
                        var slot = slots + s;
                        if (!ScoreSlots.Live(slot) || ScoreSlots.Paused(slot)) continue;
                        var tick = TimelineTicks.Real(slot, clock);
                        var backward = ScoreSlots.Backward(slot, Forward);
                        var timeline = Index.Timelines + (slot->Timeline - 1);
                        var key = Score.KeyOf(slot->Timeline, tick, LocomotionKindIndex);
                        var end = Score.Spans.End(key);
                        for (var f = Score.Spans.First(key); f < end; f++)
                        {
                            var feed = Score.Spans.Feeds + f;
                            var step = feed->Step;
                            var first = (byte*)step->FrameRow + sizeof(WalkTrack);
                            var second = step->FactorSpan == 0 ? null : first + sizeof(WalkClip);
                            global::TlUnityGenerated.TlUnityFrames.Frame_0_3((byte*)step->FrameRow, first, second, ScoreSlots.BlendAt(step, tick), tick, (ushort)(step->WindowEnd - step->WindowStart), (ushort)(tick - step->WindowStart), (ushort)step->TrackIndex, (ushort)timeline->Duration, backward ? (sbyte)-1 : (sbyte)1, ScoreSlots.FlagsAt(tick, timeline->Duration, backward, feed->Flags), out _, out _, out var frame);
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

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (!chunk.Has(ref HealthHandle) || !chunk.Has(ref DamageEnabledHandle)) return;
            var playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);
            var clocks = chunk.Has(ref ClockHandle) ? (ushort*)chunk.GetRequiredComponentDataPtrRO(ref ClockHandle) : null;
            var health = (Health*)chunk.GetRequiredComponentDataPtrRW(ref HealthHandle);
            var enabled = chunk.GetEnableableBits(ref DamageEnabledHandle);
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
        ComponentTypeHandle<Movement_MotionData> _motion;
        ComponentTypeHandle<Movement_WalkStatData> _walkStat;
        ComponentTypeHandle<Movement_JumpPowerData> _jumpPower;
        ComponentTypeHandle<Movement_TargetData> _target;
        ComponentTypeHandle<Movement_StaminaData> _stamina;
        ComponentTypeHandle<LiftEnabled> _liftEnabled;
        ComponentTypeHandle<LocomotionEnabled> _locomotionEnabled;
        ComponentTypeHandle<StatChangeEnabled> _statChangeEnabled;
        ComponentLookup<Anchor> _anchors;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = new EntityQueryBuilder(Allocator.Temp).WithAll<Playback, TimelineScored>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _playbacks = state.GetBufferTypeHandle<Playback>(true);
            _clocks = state.GetComponentTypeHandle<TimelineClock>(true);
            _motion = state.GetComponentTypeHandle<Movement_MotionData>(false);
            _walkStat = state.GetComponentTypeHandle<Movement_WalkStatData>(true);
            _jumpPower = state.GetComponentTypeHandle<Movement_JumpPowerData>(true);
            _target = state.GetComponentTypeHandle<Movement_TargetData>(true);
            _stamina = state.GetComponentTypeHandle<Movement_StaminaData>(false);
            _liftEnabled = state.GetComponentTypeHandle<LiftEnabled>(true);
            _locomotionEnabled = state.GetComponentTypeHandle<LocomotionEnabled>(true);
            _statChangeEnabled = state.GetComponentTypeHandle<StatChangeEnabled>(true);
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
            _motion.Update(ref state);
            _walkStat.Update(ref state);
            _jumpPower.Update(ref state);
            _target.Update(ref state);
            _stamina.Update(ref state);
            _liftEnabled.Update(ref state);
            _locomotionEnabled.Update(ref state);
            _statChangeEnabled.Update(ref state);
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
                MotionHandle = _motion,
                WalkStatHandle = _walkStat,
                JumpPowerHandle = _jumpPower,
                TargetHandle = _target,
                StaminaHandle = _stamina,
                LiftEnabledHandle = _liftEnabled,
                LocomotionEnabledHandle = _locomotionEnabled,
                StatChangeEnabledHandle = _statChangeEnabled,
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

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _query = new EntityQueryBuilder(Allocator.Temp).WithAll<Playback, TimelineScored>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _playbacks = state.GetBufferTypeHandle<Playback>(true);
            _clocks = state.GetComponentTypeHandle<TimelineClock>(true);
            _health = state.GetComponentTypeHandle<Vitals_HealthData>(false);
            _damageEnabled = state.GetComponentTypeHandle<DamageEnabled>(true);
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
            };
            state.Dependency = job.ScheduleParallel(_query, state.Dependency);
        }
    }
}
