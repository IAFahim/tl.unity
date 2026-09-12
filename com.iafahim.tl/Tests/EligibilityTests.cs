using Fixtures;
using NUnit.Framework;
using Tl.Unity;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityRow = Tl.Unity.TimelineComponent;

namespace Tl.Unity.Tests;

public abstract class EligibilityPolicyTests
{
    protected abstract TimelineSystemGroup CreateCoordinator(World world);
    protected abstract int RecordCount { get; }
    protected abstract void ResetRecords();

    private World _world;
    private TimelineSystemGroup _group;
    private Entity _clock;
    private EntityArchetype _rowArchetype;
    private static readonly System.Collections.Generic.List<TimelineAsset> Loaded = [];

    protected EntityManager Em => _world.EntityManager;

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var asset in Loaded) asset.Dispose();
        Loaded.Clear();
    }

    [SetUp]
    public void SetUp()
    {
        ResetRecords();
        TimelineEligibility.Reset();
        _world = new World("TlEligibility");
        _group = CreateCoordinator(_world);
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

    private void Tick(uint gameTick, int delta)
    {
        _world.EntityManager.SetComponentData(_clock, new TimelineClock { GameTick = gameTick, Delta = delta });
        _group.Update();
    }

    private Health HealthOf(Entity entity) => _world.EntityManager.GetComponentData<Health>(entity);

    private UnityRow RowOf(Entity entity) => _world.EntityManager.GetComponentData<UnityRow>(entity);

    [Test]
    public void DisabledMandatoryColumnExcludesRowBeforeTick()
    {
        var asset = Load("fixture_a");
        var enabled = CreateRow(asset);
        var disabled = CreateRow(asset);
        Em.SetComponentEnabled<Health>(disabled, false);

        Tick(100u, 5);

        Assert.AreEqual(2, RecordCount, "only the eligible row's two crossed frames execute");
        Assert.AreEqual(165f, HealthOf(enabled).Value, "eligible row column executes");
        Assert.AreEqual(0f, HealthOf(disabled).Value, "disabled row's column is untouched");
        Assert.AreEqual(3u, RowOf(enabled).Position);
        Assert.AreEqual(0u, RowOf(disabled).Position, "exclusion is a total no-op: no movement either");
        Assert.AreEqual(0L, RowOf(disabled).Cycle);
        Assert.AreEqual(1, TimelineEligibility.Count, "the exclusion is recorded, not concealed");
        Assert.AreEqual(disabled, TimelineEligibility.At(0).Row);
        Assert.AreEqual(0, TimelineEligibility.At(0).Chunk, "disabled rows stay in their chunk; only structural removal moves");
        Assert.AreEqual(TimelineExclusionReason.DisabledMandatoryColumn, TimelineEligibility.At(0).Reason);
    }

    [Test]
    public void RemovedMandatoryColumnExcludesRowWithoutPartialExecution()
    {
        var asset = Load("fixture_a");
        var complete = CreateRow(asset);
        var incomplete = CreateRow(asset);
        Em.RemoveComponent<Health>(incomplete);

        Tick(100u, 5);

        Assert.AreEqual(2, RecordCount, "the complete row executes both crossed frames");
        Assert.AreEqual(165f, HealthOf(complete).Value);
        Assert.AreEqual(0u, RowOf(incomplete).Position, "the incomplete row never partially executes or commits");
        Assert.AreEqual(0L, RowOf(incomplete).Cycle);
        Assert.AreEqual(1, TimelineEligibility.Count, "removal is surfaced as a diagnostic before any effect");
        Assert.AreEqual(incomplete, TimelineEligibility.At(0).Row);
        Assert.AreEqual(TimelineExclusionReason.MissingMandatoryColumn, TimelineEligibility.At(0).Reason);
    }

    [Test]
    public void AllRowsExcludedIsObservableNoMatchNotConcealment()
    {
        var asset = Load("fixture_a");
        var first = CreateRow(asset);
        var second = CreateRow(asset);
        Em.SetComponentEnabled<Health>(first, false);
        Em.SetComponentEnabled<Health>(second, false);

        Tick(100u, 5);

        Assert.AreEqual(0, RecordCount, "a no-match iteration executes nothing");
        Assert.AreEqual(2, TimelineEligibility.Count, "each row's exclusion decision stays observable");
        Assert.AreEqual(0u, RowOf(first).Position);
        Assert.AreEqual(0u, RowOf(second).Position);

        Em.SetComponentEnabled<Health>(first, true);
        Em.SetComponentEnabled<Health>(second, true);
        Tick(100u, 5);

        Assert.AreEqual(4, RecordCount, "eligibility is re-evaluated per tick, never sticky");
        Assert.AreEqual(0, TimelineEligibility.Count);
        Assert.AreEqual(165f, HealthOf(first).Value);
        Assert.AreEqual(165f, HealthOf(second).Value);
    }

    [Test]
    public void DisabledRowInsideSharedChunkSplitsEligibleRuns()
    {
        var asset = Load("fixture_a");
        var first = CreateRow(asset);
        var second = CreateRow(asset);
        var third = CreateRow(asset);
        Em.SetComponentEnabled<Health>(second, false);

        Tick(100u, 5);

        Assert.AreEqual(4, RecordCount, "rows around the excluded row still execute: two runs of one row each");
        Assert.AreEqual(165f, HealthOf(first).Value);
        Assert.AreEqual(0f, HealthOf(second).Value);
        Assert.AreEqual(165f, HealthOf(third).Value);
        Assert.AreEqual(1, TimelineEligibility.Count);
        Assert.AreEqual(second, TimelineEligibility.At(0).Row);
    }
}

[TestFixture]
public sealed class ManagedEligibilityTests : EligibilityPolicyTests
{
    protected override TimelineSystemGroup CreateCoordinator(World world) => TimelineEcs.CreateGroup(world);
    protected override int RecordCount => TimelineConsumers.Count;
    protected override void ResetRecords() => TimelineConsumers.Reset();
}

[TestFixture]
public sealed class BurstEligibilityTests : EligibilityPolicyTests
{
    private NativeList<Record> _sink;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _sink = BurstArms.Install();

    protected override TimelineSystemGroup CreateCoordinator(World world) => TimelineBurst.CreateGroup(world);
    protected override int RecordCount => _sink.Length;
    protected override void ResetRecords()
    {
        TimelineConsumers.Reset();
        _sink.Clear();
    }
}
