using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Fixtures;
using NUnit.Framework;
using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using Tl.Unity.Tests;
using TlUnityJobs;
using UnityRow = Tl.Unity.TimelineComponent;
using CoreRow = Tl.TimelineComponent;

namespace TlUnityJobs
{
    [BurstCompile]
    public partial struct TimelineIJobEntityTickJob : IJobEntity
    {
        public uint GameTick;
        public int Delta;
        public ulong HealthKey;

        public unsafe void Execute(ref UnityRow row, ref Health health)
        {
            UnmanagedTickState state = default;
            var keys = stackalloc ulong[1];
            keys[0] = HealthKey;
            var bases = stackalloc void*[1];
            bases[0] = Unsafe.AsPointer(ref health);
            Tl.Timeline.TickUnmanaged(&state, (CoreRow*)Unsafe.AsPointer(ref row), 1, bases, keys, 1, GameTick, Delta);
        }
    }
}

namespace Tl.Unity.Tests
{

internal unsafe struct TimelineIJobEntityTickJobRunner : IJobChunk
{
    internal static NativeArray<int> Diag;

    public TimelineIJobEntityTickJob Job;
    public ComponentTypeHandle<UnityRow> RowHandle;
    public ComponentTypeHandle<Health> HealthHandle;

    public unsafe void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
    {
        if (Diag.IsCreated) Diag[0]++;
        var rows = chunk.GetNativeArray(ref RowHandle);
        var healths = chunk.GetNativeArray(ref HealthHandle);
        for (var i = 0; i < chunk.Count; i++)
        {
            if (useEnabledMask && !chunk.IsComponentEnabled(ref HealthHandle, i))
            {
                if (Diag.IsCreated) Diag[2]++;
                continue;
            }
            if (Diag.IsCreated) Diag[1]++;
            var row = rows[i];
            var health = healths[i];
            Job.Execute(ref row, ref health);
            rows[i] = row;
            healths[i] = health;
        }
    }
}

public struct TimelineGateJob : IJobChunk
{
    public NativeArray<float> Observed;
    [ReadOnly] public ComponentTypeHandle<Health> HealthHandle;

    public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
    {
        var healths = chunk.GetNativeArray(ref HealthHandle);
        if (chunk.Count > 0) Observed[0] = healths[0].Value;
        while (Volatile.Read(ref Release) == 0) Thread.SpinWait(64);
    }

    internal static int Release;
}

public struct TimelineObserveTickJob : IJobChunk
{
    public NativeArray<float> Captured;
    [ReadOnly] public ComponentTypeHandle<Health> HealthHandle;

    public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
    {
        var healths = chunk.GetNativeArray(ref HealthHandle);
        if (chunk.Count > 0) Captured[0] = healths[0].Value;
    }
}

[DisableAutoCreation]
[UpdateInGroup(typeof(TimelineSystemGroup))]
public partial class TimelineObserveSystem : SystemBase
{
    public NativeArray<float> Captured;
    private EntityQuery _health;

    protected override void OnCreate() => _health = GetEntityQuery(ComponentType.ReadOnly<Health>());

    public JobHandle ScheduleGate(NativeArray<float> observed)
        => new TimelineGateJob
        {
            Observed = observed,
            HealthHandle = GetComponentTypeHandle<Health>(true),
        }.Schedule(_health, default);

    protected override void OnUpdate()
        => Dependency = new TimelineObserveTickJob
        {
            Captured = Captured,
            HealthHandle = GetComponentTypeHandle<Health>(true),
        }.Schedule(_health, Dependency);
}

[DisableAutoCreation]
[UpdateInGroup(typeof(TimelineSystemGroup))]
public unsafe partial struct TimelineIJobEntityCoordinatorSystem : ISystem
{
    public static JobHandle Gate;

    private EntityQuery _rows;
    private EntityQuery _clock;
    private EntityQuery _derived;

    public void OnCreate(ref SystemState state)
    {
        _rows = state.GetEntityQuery(ComponentType.ReadWrite<TimelineComponent>());
        _clock = state.GetEntityQuery(ComponentType.ReadOnly<TimelineClock>());
        _derived = state.GetEntityQuery(ComponentType.ReadWrite<TimelineComponent>(), ComponentType.ReadWrite<Health>());
    }

    public void OnUpdate(ref SystemState state)
    {
        state.Dependency.Complete();
        if (!_clock.TryGetSingleton<TimelineClock>(out var clock))
            throw new InvalidOperationException("TimelineClock singleton is missing.");
        if (clock.Delta == 0) return;

        TimelineEligibility.RecordExclusions(ref state, _rows);

        var gate = Gate;
        Gate = default;
        var selection = !gate.Equals(default) ? JobHandle.CombineDependencies(state.Dependency, gate) : state.Dependency;
        state.Dependency = new TimelineIJobEntityTickJobRunner
        {
            Job = new TimelineIJobEntityTickJob
            {
                GameTick = clock.GameTick,
                Delta = clock.Delta,
                HealthKey = TimelineEcs.ComponentKey<Health>(),
            },
            RowHandle = state.GetComponentTypeHandle<TimelineComponent>(),
            HealthHandle = state.GetComponentTypeHandle<Health>(),
        }.Schedule(_derived, selection);
        UnityEngine.Debug.Log($"[TLDIAG] sched chunks={_derived.CalculateChunkCount()} ents={_derived.CalculateEntityCount()} gated={!gate.Equals(default)}");
    }
}

[TestFixture]
public sealed class IJobEntityParityTests
{
    private static readonly List<TimelineAsset> Loaded = [];

    private World _world;
    private TimelineSystemGroup _managedGroup;
    private TimelineSystemGroup _jobGroup;
    private Entity _clock;
    private EntityArchetype _rowArchetype;
    private NativeList<Record> _sink;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _sink = BurstArms.Install();

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var asset in Loaded) asset.Dispose();
        Loaded.Clear();
    }

    [SetUp]
    public void SetUp()
    {
        TimelineConsumers.Reset();
        _sink.Clear();
        TimelineEligibility.Reset();
        _world = new World("TlIJobEntity");
        _managedGroup = TimelineEcs.CreateGroup(_world);
        _jobGroup = _world.CreateSystemManaged<TimelineSystemGroup>();
        _jobGroup.AddSystemToUpdateList(_world.CreateSystem<TimelineIJobEntityCoordinatorSystem>());
        _clock = _world.EntityManager.CreateEntity(typeof(TimelineClock));
        _rowArchetype = _world.EntityManager.CreateArchetype(typeof(UnityRow), typeof(Health));
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    private TimelineAsset Load(string name)
    {
        var asset = Resources.Load<TextAsset>("TlFixtures/" + name);
        Assert.NotNull(asset, "missing fixture: " + name);
        var timeline = TimelineAsset.Load(asset.bytes);
        Loaded.Add(timeline);
        return timeline;
    }

    private Entity CreateRow(TimelineAsset asset)
    {
        var entity = _world.EntityManager.CreateEntity(_rowArchetype);
        _world.EntityManager.SetComponentData(entity, new UnityRow { Reference = asset.Reference });
        _world.EntityManager.SetComponentData(entity, new Health());
        return entity;
    }

    private void ResetRow(Entity row, TimelineAsset asset)
    {
        _world.EntityManager.SetComponentData(row, new UnityRow { Reference = asset.Reference });
        _world.EntityManager.SetComponentData(row, new Health());
    }

    private void Tick(uint gameTick, int delta, bool jobArm)
    {
        _world.EntityManager.SetComponentData(_clock, new TimelineClock { GameTick = gameTick, Delta = delta });
        (jobArm ? _jobGroup : _managedGroup).Update();
    }

    private UnityRow RowOf(Entity entity) => _world.EntityManager.GetComponentData<UnityRow>(entity);

    private Health HealthOf(Entity entity) => _world.EntityManager.GetComponentData<Health>(entity);

    private List<Record> ManagedRecords()
    {
        var records = new List<Record>();
        for (var i = 0; i < TimelineConsumers.Count; i++) records.Add(TimelineConsumers.At(i));
        return records;
    }

    private List<Record> JobRecords()
    {
        var records = new List<Record>();
        for (var i = 0; i < _sink.Length; i++) records.Add(_sink[i]);
        return records;
    }

    [Test]
    public void IJobEntityArmMatchesManagedArmForwardAndBackward()
    {
        var asset = Load("fixture_a");
        var row = CreateRow(asset);

        TimelineConsumers.Reset();
        Tick(100u, 1, false);
        Tick(101u, 1, false);
        Tick(102u, 1, false);
        Tick(103u, -3, false);
        var managed = ManagedRecords();
        var managedHealth = HealthOf(row).Value;

        ResetRow(row, asset);
        TimelineConsumers.Reset();
        _sink.Clear();
        Tick(100u, 1, true);
        Tick(101u, 1, true);
        Tick(102u, 1, true);
        Tick(103u, -3, true);
        _world.EntityManager.CompleteAllTrackedJobs();
        var job = JobRecords();

        Assert.AreEqual(managed.Count, job.Count, "record count parity");
        for (var i = 0; i < managed.Count; i++)
            Assert.AreEqual(managed[i], job[i], "record " + i);
        Assert.AreEqual(4, job.Count, "two visited clip frames forward and mirrored backward");
        Assert.AreEqual(0u, RowOf(row).Position);
        Assert.AreEqual(0L, RowOf(row).Cycle);
        Assert.AreEqual(managedHealth, HealthOf(row).Value, "health column parity");
        Assert.AreEqual(330f, HealthOf(row).Value, "both arms apply clip contributions twice");
        Assert.AreEqual(0, TimelineEligibility.Count, "the derived query and the diagnostic walk agree on eligibility");
    }

    [Test]
    public void IJobEntityDerivedQueryAppliesEligibilityPolicy()
    {
        var asset = Load("fixture_a");
        var enabled = CreateRow(asset);
        var disabled = CreateRow(asset);
        _world.EntityManager.SetComponentEnabled<Health>(disabled, false);

        Tick(100u, 5, true);
        _world.EntityManager.CompleteAllTrackedJobs();

        Assert.AreEqual(2, _sink.Length, "the derived query scheduled only the eligible row");
        Assert.AreEqual(0f, HealthOf(disabled).Value, "disabled row's column untouched");
        Assert.AreEqual(0u, RowOf(disabled).Position, "disabled row made no movement");
        Assert.AreEqual(3u, RowOf(enabled).Position);
        Assert.AreEqual(1, TimelineEligibility.Count, "the host walk recorded the exclusion");
        Assert.AreEqual(disabled, TimelineEligibility.At(0).Row);
        Assert.AreEqual(TimelineExclusionReason.DisabledMandatoryColumn, TimelineEligibility.At(0).Reason);

        _world.EntityManager.RemoveComponent<Health>(disabled);
        TimelineEligibility.Reset();
        _sink.Clear();
        ResetRow(enabled, asset);
        Tick(100u, 5, true);
        _world.EntityManager.CompleteAllTrackedJobs();

        Assert.AreEqual(2, _sink.Length, "only the complete row remains scheduled");
        Assert.AreEqual(0u, RowOf(disabled).Position);
        Assert.AreEqual(1, TimelineEligibility.Count);
        Assert.AreEqual(disabled, TimelineEligibility.At(0).Row);
        Assert.AreEqual(TimelineExclusionReason.MissingMandatoryColumn, TimelineEligibility.At(0).Reason);
    }
}

[TestFixture]
public sealed class DelayedDependencyTests
{
    private static readonly List<TimelineAsset> Loaded = [];

    private World _world;
    private TimelineSystemGroup _group;
    private TimelineObserveSystem _observe;
    private Entity _clock;
    private EntityArchetype _rowArchetype;
    private NativeList<Record> _sink;
    private NativeArray<float> _observed;
    private NativeArray<float> _captured;
    private JobHandle _gate;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _sink = BurstArms.Install();

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var asset in Loaded) asset.Dispose();
        Loaded.Clear();
    }

    [SetUp]
    public void SetUp()
    {
        TimelineConsumers.Reset();
        _sink.Clear();
        TimelineEligibility.Reset();
        TimelineGateJob.Release = 0;
        TimelineIJobEntityCoordinatorSystem.Gate = default;
        _gate = default;
        _observed = new NativeArray<float>(1, Allocator.Persistent);
        _captured = new NativeArray<float>(1, Allocator.Persistent);
        _world = new World("TlDelayed");
        _group = _world.CreateSystemManaged<TimelineSystemGroup>();
        _group.AddSystemToUpdateList(_world.CreateSystem<TimelineIJobEntityCoordinatorSystem>());
        _observe = _world.CreateSystemManaged<TimelineObserveSystem>();
        _observe.Captured = _captured;
        _group.AddSystemToUpdateList(_observe);
        _clock = _world.EntityManager.CreateEntity(typeof(TimelineClock));
        _rowArchetype = _world.EntityManager.CreateArchetype(typeof(UnityRow), typeof(Health));
    }

    [TearDown]
    public void TearDown()
    {
        _world.Dispose();
        _observed.Dispose();
        _captured.Dispose();
    }

    private TimelineAsset Load(string name)
    {
        var asset = Resources.Load<TextAsset>("TlFixtures/" + name);
        Assert.NotNull(asset, "missing fixture: " + name);
        var timeline = TimelineAsset.Load(asset.bytes);
        Loaded.Add(timeline);
        return timeline;
    }

    private Health HealthOf(Entity entity) => _world.EntityManager.GetComponentData<Health>(entity);

    [Test]
    public void DelayedSelectionDependencyBlocksCommitUntilCompletion()
    {
        var asset = Load("fixture_a");
        var row = _world.EntityManager.CreateEntity(_rowArchetype);
        _world.EntityManager.SetComponentData(row, new UnityRow { Reference = asset.Reference });
        _world.EntityManager.SetComponentData(row, new Health());

        TimelineGateJob.Release = 0;
        _gate = _observe.ScheduleGate(_observed);
        TimelineIJobEntityCoordinatorSystem.Gate = _gate;

        _world.EntityManager.SetComponentData(_clock, new TimelineClock { GameTick = 100u, Delta = 5 });
        _group.Update();

        Assert.AreEqual(0, _sink.Length, "no consumer executed while the selection dependency is incomplete");
        Assert.AreEqual(0, TimelineConsumers.Count, "no managed consumer executed either: no early commit, no early overwrite");

        Volatile.Write(ref TimelineGateJob.Release, 1);
        _world.EntityManager.CompleteAllTrackedJobs();
        _gate.Complete();

        Assert.AreEqual(2, _sink.Length, "the commit lands exactly once after the dependency completes");
        Assert.AreEqual(165f, HealthOf(row).Value, "exactly-once commit: no double commit, no partial commit");
        Assert.AreEqual(3u, _world.EntityManager.GetComponentData<UnityRow>(row).Position);
        Assert.AreEqual(0f, _observed[0], "the in-flight gated read observed the pre-tick value: the coordinator did not overwrite early");
        Assert.AreEqual(165f, _captured[0], "the coordinator passed the combined dependency to the next stage, which read the committed value");
    }
}

internal struct TlDiagNopJob : IJob
{
    public void Execute() { }
}

[TestFixture]
public sealed class TlDiagClonedWorldShapes
{
    private static readonly List<TimelineAsset> Loaded = [];

    private NativeList<Record> _sink;
    private NativeArray<int> _diag;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _sink = BurstArms.Install();

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var asset in Loaded) asset.Dispose();
        Loaded.Clear();
    }

    [SetUp]
    public void SetUp()
    {
        TimelineConsumers.Reset();
        _sink.Clear();
        TimelineEligibility.Reset();
        TimelineGateJob.Release = 0;
        TimelineIJobEntityCoordinatorSystem.Gate = default;
        _diag = new NativeArray<int>(3, Allocator.Persistent);
        TimelineIJobEntityTickJobRunner.Diag = _diag;
    }

    [TearDown]
    public void TearDown()
    {
        TimelineIJobEntityTickJobRunner.Diag = default;
        _diag.Dispose();
    }

    [Test]
    public void TlDiagClonedWorldShapesMatrix()
    {
        var asset = Resources.Load<TextAsset>("TlFixtures/fixture_a");
        Assert.NotNull(asset, "missing fixture: fixture_a");
        var timeline = TimelineAsset.Load(asset.bytes);
        Loaded.Add(timeline);

        for (var variant = 0; variant < 4; variant++)
        {
            var managedGroup = (variant & 1) != 0;
            var gated = (variant & 2) != 0;
            _diag[0] = 0;
            _diag[1] = 0;
            _diag[2] = 0;
            TimelineIJobEntityCoordinatorSystem.Gate = default;
            var world = new World("TlDiag" + variant);
            try
            {
                if (managedGroup) TimelineEcs.CreateGroup(world);
                var jobGroup = world.CreateSystemManaged<TimelineSystemGroup>();
                jobGroup.AddSystemToUpdateList(world.CreateSystem<TimelineIJobEntityCoordinatorSystem>());
                var clock = world.EntityManager.CreateEntity(typeof(TimelineClock));
                var archetype = world.EntityManager.CreateArchetype(typeof(UnityRow), typeof(Health));
                var row = world.EntityManager.CreateEntity(archetype);
                world.EntityManager.SetComponentData(row, new UnityRow { Reference = timeline.Reference });
                world.EntityManager.SetComponentData(row, new Health());

                if (gated) TimelineIJobEntityCoordinatorSystem.Gate = new TlDiagNopJob().Schedule();

                world.EntityManager.SetComponentData(clock, new TimelineClock { GameTick = 100u, Delta = 1 });
                jobGroup.Update();
                world.EntityManager.CompleteAllTrackedJobs();

                UnityEngine.Debug.Log($"[TLDIAG] variant={variant} managedGroup={managedGroup} gated={gated} chunks={_diag[0]} rows={_diag[1]} skipped={_diag[2]} sink={_sink.Length} exclusions={TimelineEligibility.Count}");
            }
            finally
            {
                world.Dispose();
            }
        }
    }
}
}
