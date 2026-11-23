using System;
using System.Collections.Generic;
using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace KindPure
{
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public partial struct PureMovementSystem : ISystem
    {
        TimelineJobs.Movement _jobs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _jobs = TimelineJobs.Movement.Create(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }

    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public unsafe partial struct PureSpanWalkMovementSystem : ISystem
    {
        const int MostKinds = 64;

        EntityQuery _query;
        BufferTypeHandle<Playback> _playback;
        ComponentTypeHandle<TimelineClock> _clock;
        ComponentTypeHandle<TimelinePlaying> _playing;
        ComponentTypeHandle<Movement_MotionData> _motion;
        ComponentTypeHandle<Movement_WalkStatData> _walkStat;
        ComponentTypeHandle<Movement_JumpPowerData> _jumpPower;
        ComponentTypeHandle<Movement_StaminaData> _stamina;
        BufferTypeHandle<LocomotionEntry> _locomotionEntry;
        ComponentTypeHandle<LocomotionActive> _locomotionActive;
        ComponentTypeHandle<LocomotionEnabled> _locomotionEnabled;
        BufferTypeHandle<LiftEntry> _liftEntry;
        ComponentTypeHandle<LiftActive> _liftActive;
        ComponentTypeHandle<LiftEnabled> _liftEnabled;
        BufferTypeHandle<StatChangeEntry> _statChangeEntry;
        ComponentTypeHandle<StatChangeActive> _statChangeActive;
        ComponentTypeHandle<StatChangeEnabled> _statChangeEnabled;
        global::Tl.KindValues* _noTables;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _query = new EntityQueryBuilder(Allocator.Temp).WithAll<Playback>().WithAny<LocomotionEntry, LiftEntry, StatChangeEntry>().Build(ref state);
            _playback = state.GetBufferTypeHandle<Playback>(true);
            _clock = state.GetComponentTypeHandle<TimelineClock>(true);
            _playing = state.GetComponentTypeHandle<TimelinePlaying>(true);
            _motion = state.GetComponentTypeHandle<Movement_MotionData>(false);
            _walkStat = state.GetComponentTypeHandle<Movement_WalkStatData>(true);
            _jumpPower = state.GetComponentTypeHandle<Movement_JumpPowerData>(true);
            _stamina = state.GetComponentTypeHandle<Movement_StaminaData>(false);
            _locomotionEntry = state.GetBufferTypeHandle<LocomotionEntry>(false);
            _locomotionActive = state.GetComponentTypeHandle<LocomotionActive>(false);
            _locomotionEnabled = state.GetComponentTypeHandle<LocomotionEnabled>(true);
            _liftEntry = state.GetBufferTypeHandle<LiftEntry>(false);
            _liftActive = state.GetComponentTypeHandle<LiftActive>(false);
            _liftEnabled = state.GetComponentTypeHandle<LiftEnabled>(true);
            _statChangeEntry = state.GetBufferTypeHandle<StatChangeEntry>(false);
            _statChangeActive = state.GetComponentTypeHandle<StatChangeActive>(false);
            _statChangeEnabled = state.GetComponentTypeHandle<StatChangeEnabled>(true);
            var bytes = MostKinds * sizeof(global::Tl.KindValues);
            _noTables = (global::Tl.KindValues*)UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent);
            UnsafeUtility.MemClear(_noTables, bytes);
        }

        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();
            UnsafeUtility.Free(_noTables, Allocator.Persistent);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            _playback.Update(ref state);
            _clock.Update(ref state);
            _playing.Update(ref state);
            _motion.Update(ref state);
            _walkStat.Update(ref state);
            _jumpPower.Update(ref state);
            _stamina.Update(ref state);
            _locomotionEntry.Update(ref state);
            _locomotionActive.Update(ref state);
            _locomotionEnabled.Update(ref state);
            _liftEntry.Update(ref state);
            _liftActive.Update(ref state);
            _liftEnabled.Update(ref state);
            _statChangeEntry.Update(ref state);
            _statChangeActive.Update(ref state);
            _statChangeEnabled.Update(ref state);
            var indexWithoutTables = library.Kinds;
            indexWithoutTables.Values = _noTables;
            var job = new MovementChunkJob
            {
                View = library.View,
                Index = indexWithoutTables,
                Forward = step.Forward,
                PlaybackHandle = _playback,
                ClockHandle = _clock,
                PlayingHandle = _playing,
                MotionHandle = _motion,
                WalkStatHandle = _walkStat,
                JumpPowerHandle = _jumpPower,
                StaminaHandle = _stamina,
                LocomotionEntryHandle = _locomotionEntry,
                LocomotionActiveHandle = _locomotionActive,
                LocomotionEnabledHandle = _locomotionEnabled,
                LiftEntryHandle = _liftEntry,
                LiftActiveHandle = _liftActive,
                LiftEnabledHandle = _liftEnabled,
                StatChangeEntryHandle = _statChangeEntry,
                StatChangeActiveHandle = _statChangeActive,
                StatChangeEnabledHandle = _statChangeEnabled,
            };
            state.Dependency = job.ScheduleParallel(_query, state.Dependency);
        }
    }

    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(TimelineSimulationGroup))]
    [UpdateAfter(typeof(TimelineKindTickSystem))]
    public partial struct PureVitalsSystem : ISystem
    {
        TimelineJobs.Vitals _jobs;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TimelineLibrary>();
            state.RequireForUpdate<TimelineStep>();
            _jobs = TimelineJobs.Vitals.Create(ref state);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var library = SystemAPI.GetSingleton<TimelineLibrary>();
            var step = SystemAPI.GetSingleton<TimelineStep>();
            state.Dependency = _jobs.Schedule(ref state, library.View, library.Kinds, step.Forward, state.Dependency);
        }
    }

    [DisableAutoCreation]
    public unsafe partial struct PureSwapEveryTimelineSystem : ISystem
    {
        EntityQuery _playbacks;
        BufferTypeHandle<Playback> _playbackHandle;
        ComponentTypeHandle<TimelinePlaying> _playingHandle;
        ComponentTypeHandle<TimelineClock> _clockHandle;

        public void OnCreate(ref SystemState state)
        {
            _playbacks = new EntityQueryBuilder(Allocator.Temp).WithAllRW<Playback>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _playbackHandle = state.GetBufferTypeHandle<Playback>(false);
            _playingHandle = state.GetComponentTypeHandle<TimelinePlaying>(false);
            _clockHandle = state.GetComponentTypeHandle<TimelineClock>(true);
            state.RequireForUpdate<TimelineLibrary>();
        }

        public void OnUpdate(ref SystemState state)
        {
            state.CompleteDependency();
            var index = SystemAPI.GetSingleton<TimelineLibrary>().Kinds;
            var everyTimeline = new NativeArray<ushort>(index.TimelineCount, Allocator.TempJob);
            for (var i = 0; i < everyTimeline.Length; i++)
                everyTimeline[i] = (ushort)(i + 1);
            var handles = default(KindHandleSet);
            for (var k = 0; k < index.KindCount; k++)
            {
                if ((index.Bound & (1ul << k)) == 0) continue;
                KindHandleSet.Put(ref handles, k, state.GetDynamicComponentTypeHandle(index.EntryTypes[k]), state.GetDynamicComponentTypeHandle(index.ActiveTypes[k]));
            }
            _playbackHandle.Update(ref state);
            _playingHandle.Update(ref state);
            _clockHandle.Update(ref state);
            new TlTimelineSwapJob
            {
                Index = index,
                PlaybackHandle = _playbackHandle,
                PlayingHandle = _playingHandle,
                ClockHandle = _clockHandle,
                Handles = handles,
                Swapped = everyTimeline,
            }.Run(_playbacks);
            everyTimeline.Dispose();
        }
    }

    [BurstCompile]
    public struct PureResumeEveryRowJob : IJobChunk
    {
        public BufferTypeHandle<TimelineRequest> RequestHandle;
        public int Every;
        public int Step;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in global::Unity.Burst.Intrinsics.v128 chunkEnabledMask)
        {
            var requests = chunk.GetBufferAccessor(ref RequestHandle);
            for (var row = 0; row < chunk.Count; row++)
                if ((unfilteredChunkIndex + row + Step) % Every == 0)
                    requests[row].Add(TimelineRequest.Resume(0, 1));
        }
    }

    [BurstCompile]
    [DisableAutoCreation]
    public partial struct PureRequestSystem : ISystem
    {
        public int Every;
        int _step;
        EntityQuery _rows;
        BufferTypeHandle<TimelineRequest> _requests;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _rows = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TimelineRequest>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(ref state);
            _requests = state.GetBufferTypeHandle<TimelineRequest>(false);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (Every <= 0) return;
            _requests.Update(ref state);
            state.Dependency = new PureResumeEveryRowJob { RequestHandle = _requests, Every = Every, Step = _step++ }.ScheduleParallel(_rows, state.Dependency);
        }
    }

    public enum PureRows
    {
        Eager,
        Glide,
        Lazy,
    }

    public sealed unsafe class PureWorld : IDisposable
    {
        public readonly World World;
        public readonly EntityManager Entities;
        public readonly SystemHandle TickHandle;
        public readonly SystemHandle MovementHandle;
        public readonly SystemHandle VitalsHandle;
        public readonly SystemHandle RequestHandle;
        public readonly Entity[] Rows;
        readonly TlLibrary _library;
        readonly TlLibrary _second;
        readonly EntityQuery _step;
        readonly EntityQuery _published;
        readonly SystemHandle _swap;
        bool _forward = true;

        public PureWorld(int count, byte[] library) : this(count, library, null, false, PureRows.Eager)
        {
        }

        public PureWorld(int count, byte[] library, byte[] second, bool spanWalk, PureRows rows)
        {
            World = new World("TlKindsPure");
            Entities = World.EntityManager;
            _library = TlLibrary.FromBytes(library);
            _library.Publish(Entities);
            if (second != null)
                _second = TlLibrary.FromBytes(second);
            _step = Entities.CreateEntityQuery(typeof(TimelineStep));
            _published = Entities.CreateEntityQuery(typeof(TimelineLibrary));
            TickHandle = World.CreateSystem<TimelineKindTickSystem>();
            MovementHandle = spanWalk ? World.CreateSystem<PureSpanWalkMovementSystem>() : World.CreateSystem<PureMovementSystem>();
            VitalsHandle = World.CreateSystem<PureVitalsSystem>();
            _swap = World.CreateSystem<PureSwapEveryTimelineSystem>();
            RequestHandle = World.CreateSystem<PureRequestSystem>();
            Rows = new Entity[count];
            for (var i = 0; i < count; i++)
                Rows[i] = Spawn(rows);
        }

        Entity Spawn(PureRows rows)
        {
            var types = new List<ComponentType>
            {
                typeof(Playback),
                typeof(TimelineRequest),
                typeof(TimelinePlaying),
                typeof(Movement_MotionData),
                typeof(Movement_WalkStatData),
                typeof(Movement_JumpPowerData),
                typeof(Movement_StaminaData),
                typeof(Vitals_HealthData),
            };
            types.AddRange(LocomotionKind.Types);
            types.AddRange(LiftKind.Types);
            types.AddRange(StatChangeKind.Types);
            types.AddRange(DamageKind.Types);
            if (rows != PureRows.Eager)
                types.Add(typeof(TimelineHorizon));
            if (rows == PureRows.Lazy)
            {
                types.Add(typeof(TimelineClock));
                types.Add(typeof(PlaybackDue));
            }
            var entity = Entities.CreateEntity(types.ToArray());
            Entities.SetComponentEnabled<TimelinePlaying>(entity, false);
            LocomotionKind.Init(Entities, entity);
            LiftKind.Init(Entities, entity);
            StatChangeKind.Init(Entities, entity);
            DamageKind.Init(Entities, entity);
            Entities.SetComponentData(entity, new Movement_WalkStatData { Value = new WalkStat { Value = 1f } });
            Entities.SetComponentData(entity, new Movement_JumpPowerData { Value = new JumpPower { Value = 1f } });
            Entities.SetComponentData(entity, new Vitals_HealthData { Value = new Health { Value = 1_000_000 } });
            return entity;
        }

        public void Start(int row, ushort timeline, ushort at)
        {
            Entities.GetBuffer<TimelineRequest>(Rows[row]).Add(TimelineRequest.Start(0, timeline, at));
        }

        public void Request(int row, global::Tl.RequestOp op, ushort timeline, ushort at)
        {
            Entities.GetBuffer<TimelineRequest>(Rows[row]).Add(new TimelineRequest { Slot = 0, Timeline = timeline, At = at, Op = op });
        }

        public void HotSwapLibrary(bool second)
        {
            Entities.CompleteAllTrackedJobs();
            var published = _published.GetSingleton<TimelineLibrary>();
            var next = (second ? _second : _library).Component;
            next.Generation = published.Generation + 1;
            _published.SetSingleton(next);
            _swap.Update(World.Unmanaged);
        }

        public void RequestEvery(int every)
        {
            World.Unmanaged.GetUnsafeSystemRef<PureRequestSystem>(RequestHandle).Every = every;
        }

        public int RequestHeapBytes(int sample)
        {
            Entities.CompleteAllTrackedJobs();
            var inline = TypeManager.GetTypeInfo(TypeManager.GetTypeIndex<TimelineRequest>()).BufferCapacity;
            var bytes = 0L;
            var take = Math.Min(sample, Rows.Length);
            for (var i = 0; i < take; i++)
            {
                var capacity = Entities.GetBuffer<TimelineRequest>(Rows[(long)i * Rows.Length / take], true).Capacity;
                if (capacity > inline) bytes += (long)capacity * UnsafeUtility.SizeOf<TimelineRequest>();
            }
            return (int)(bytes / Math.Max(1, take));
        }

        public void Step()
        {
            RequestHandle.Update(World.Unmanaged);
            TickHandle.Update(World.Unmanaged);
            MovementHandle.Update(World.Unmanaged);
            VitalsHandle.Update(World.Unmanaged);
        }

        public void Step(bool forward)
        {
            if (forward != _forward)
            {
                _step.SetSingleton(new TimelineStep { Forward = forward });
                _forward = forward;
            }
            Step();
        }

        public void StepEachSystemCompleted(double[] ticksPerSystem)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            TickHandle.Update(World.Unmanaged);
            Entities.CompleteAllTrackedJobs();
            var afterTick = System.Diagnostics.Stopwatch.GetTimestamp();
            MovementHandle.Update(World.Unmanaged);
            Entities.CompleteAllTrackedJobs();
            var afterMovement = System.Diagnostics.Stopwatch.GetTimestamp();
            VitalsHandle.Update(World.Unmanaged);
            Entities.CompleteAllTrackedJobs();
            var afterVitals = System.Diagnostics.Stopwatch.GetTimestamp();
            ticksPerSystem[0] = afterTick - start;
            ticksPerSystem[1] = afterMovement - afterTick;
            ticksPerSystem[2] = afterVitals - afterMovement;
        }

        public bool Precombined(string kind)
        {
            Entities.CompleteAllTrackedJobs();
            var index = _published.GetSingleton<TimelineLibrary>().Kinds;
            var id = index.KindOf(KindIndex.IdOf(kind));
            return id >= 0 && index.Values[id].Data != null;
        }

        public ulong Hash()
        {
            Entities.CompleteAllTrackedJobs();
            var h = global::Tl.TlStateHash.Begin();
            var real = stackalloc global::Tl.Playback[KindSlots.MaxSlots];
            foreach (var row in Rows)
            {
                var motion = Entities.GetComponentData<Movement_MotionData>(row).Value;
                h.Float(motion.X);
                h.Float(motion.Y);
                h.Int(Entities.GetComponentData<Movement_StaminaData>(row).Value.Value);
                h.Int(Entities.GetComponentData<Vitals_HealthData>(row).Value.Value);
                var clock = Entities.HasComponent<TimelineClock>(row) ? Entities.GetComponentData<TimelineClock>(row).Value : (ushort)0;
                var slots = Entities.GetBuffer<Playback>(row, true);
                for (var i = 0; i < slots.Length; i++)
                {
                    var slot = slots[i];
                    slot.Tick = TimelineTicks.Real(in slot, clock);
                    real[i] = *(global::Tl.Playback*)&slot;
                }
                h.Slots(real, slots.Length);
            }
            return h.Value;
        }

        public void Dispose()
        {
            Entities.CompleteAllTrackedJobs();
            if (World.IsCreated)
                World.Dispose();
            _library?.Dispose();
            _second?.Dispose();
        }
    }
}
