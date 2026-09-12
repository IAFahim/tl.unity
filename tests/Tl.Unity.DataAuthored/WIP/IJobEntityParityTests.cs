using System;
using System.Collections.Generic;
using System.Threading;
using Fixtures;
using NUnit.Framework;
using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using UnityRow = Tl.Unity.TimelineComponent;
using CoreRow = Tl.TimelineComponent;

namespace Tl.Unity.Tests;

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

[DisableAutoCreation]
[UpdateInGroup(typeof(TimelineSystemGroup))]
public unsafe partial struct TimelineIJobEntityCoordinatorSystem : ISystem
{
    public static JobHandle Gate;

    private EntityQuery _rows;
    private EntityQuery _clock;

    public void OnCreate(ref SystemState state)
    {
        TimelineEcs.Seal();
        _rows = state.GetEntityQuery(ComponentType.ReadWrite<TimelineComponent>());
        _clock = state.GetEntityQuery(ComponentType.ReadOnly<TimelineClock>());
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
        var selection = gate.IsValid() ? JobHandle.CombineDependencies(state.Dependency, gate) : state.Dependency;
        state.Dependency = new TimelineIJobEntityTickJob
        {
            GameTick = clock.GameTick,
            Delta = clock.Delta,
            HealthKey = TimelineEcs.ComponentKey<Health>(),
        }.Schedule(selection);
    }
}

public partial struct TimelineGateJob : IJobEntity
{
    public NativeArray<float> Observed;

    public void Execute([ReadOnly] in Health health)
    {
        Observed[0] = health.Value;
        while (Volatile.Read(ref Release) == 0) Thread.SpinWait(64);
    }

    internal static int Release;
}

public partial struct TimelineObserveTickJob : IJobEntity
{
    public NativeArray<float> Captured;

    public void Execute([ReadOnly] in Health health) => Captured[0] = health.Value;
}

[DisableAutoCreation]
[UpdateInGroup(typeof(TimelineSystemGroup))]
public partial class TimelineObserveSystem : SystemBase
{
    public NativeArray<float> Captured;
    private EntityQuery _health;

    protected override void OnCreate() => _health = GetEntityQuery(ComponentType.ReadOnly<Health>());

    protected override void OnUpdate()
        => Dependency = new TimelineObserveTickJob { Captured = Captured }.Schedule(_health, Dependency);
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

    [Test]
    public void DelayedSelectionDependencyBlocksCommitUntilCompletion()
    {
        var asset = Load("fixture_a");
        var row = _world.EntityManager.CreateEntity(_rowArchetype);
        _world.EntityManager.SetComponentData(row, new UnityRow { Reference = asset.Reference });
        _world.EntityManager.SetComponentData(row, new Health());

        TimelineGateJob.Release = 0;
        TimelineIJobEntityCoordinatorSystem.Gate = new TimelineGateJob { Observed = _observed }.Schedule((JobHandle)default);

        _world.EntityManager.SetComponentData(_clock, new TimelineClock { GameTick = 100u, Delta = 5 });
        _group.Update();

        Assert.AreEqual(0, _sink.Length, "no consumer executed while the selection dependency is incomplete");
        Assert.AreEqual(0, TimelineConsumers.Count, "no managed consumer executed either: no early commit, no early overwrite");

        TimelineGateJob.Release = 1;
        _world.EntityManager.CompleteAllTrackedJobs();

        Assert.AreEqual(2, _sink.Length, "the commit lands exactly once after the dependency completes");
        Assert.AreEqual(165f, HealthOf(row).Value, "exactly-once commit: no double commit, no partial commit");
        Assert.AreEqual(3u, _world.EntityManager.GetComponentData<UnityRow>(row).Position);
        Assert.AreEqual(0f, _observed[0], "the in-flight gated read observed the pre-tick value: the coordinator did not overwrite early");
        Assert.AreEqual(165f, _captured[0], "the coordinator passed the combined dependency to the next stage, which read the committed value");
    }
}
