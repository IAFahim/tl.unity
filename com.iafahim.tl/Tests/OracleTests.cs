using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tl;
using Tl.Unity;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using UnityRow = Tl.Unity.TimelineComponent;

namespace Tl.Unity.Tests;

[TestFixture]
public sealed class OracleTests
{
    private static readonly List<TimelineAsset> Loaded = [];

    private World _world;
    private TimelineSystemGroup _group;
    private Entity _clock;
    private EntityArchetype _rowArchetype;

    [OneTimeSetUp]
    public void OneTimeSetUp() => TimelineConsumers.Reset();

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
        _world = new World("TlOracle");
        _group = TimelineEcs.CreateGroup(_world);
        _clock = _world.EntityManager.CreateEntity(typeof(TimelineClock));
        _rowArchetype = _world.EntityManager.CreateArchetype(typeof(TimelineComponent), typeof(Health));
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    private static TextAsset Fixture(string name)
    {
        var asset = Resources.Load<TextAsset>("TlFixtures/" + name);
        Assert.NotNull(asset, "missing fixture: " + name);
        return asset;
    }

    private static TimelineAsset Load(string name)
    {
        var asset = TimelineAsset.Load(Fixture(name).bytes);
        Loaded.Add(asset);
        return asset;
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

    private UnityRow RowOf(Entity entity) => _world.EntityManager.GetComponentData<UnityRow>(entity);

    private Health HealthOf(Entity entity) => _world.EntityManager.GetComponentData<Health>(entity);

    private static void AssertRecords(params Record[] expected)
    {
        Assert.AreEqual(expected.Length, TimelineConsumers.Count, "record count");
        for (var i = 0; i < expected.Length; i++)
            Assert.AreEqual(expected[i], TimelineConsumers.At(i), "record " + i);
    }

    [Test]
    public void BakedPairKeysMatchUnityRuntimeKeys()
    {
        Assert.AreEqual(
            "Tl.Unity.Tests.AlphaTrack, Tl.Runtime.Unity.Tests, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
            typeof(AlphaTrack).AssemblyQualifiedName);
        var bytes = Fixture("fixture_a").bytes;
        Assert.AreEqual(0x31424C54u, BitConverter.ToUInt32(bytes, 0));
        Assert.AreEqual(1u, BitConverter.ToUInt32(bytes, 4));
        var pairOffset = BitConverter.ToUInt32(bytes, 28);
        var pairCount = BitConverter.ToUInt32(bytes, 24);
        Assert.AreEqual(1u, pairCount);
        Assert.AreEqual(PairRuntime<AlphaTrack, AlphaClip>.Key, BitConverter.ToUInt64(bytes, (int)pairOffset));
    }

    [Test]
    public void TickMatchesMovementOracleForwardAndBackward()
    {
        var asset = Load("fixture_a");
        var row = CreateRow(asset);

        TimelineConsumers.Reset();
        Tick(100u, 5);
        AssertRecords(
            new Record('A', 5, 11f, 0u, 100u, 0L, FrameFlags.TimelineStart | FrameFlags.ClipStart | FrameFlags.ClipEnd),
            new Record('A', 5, 22f, 2u, 102u, 0L, FrameFlags.TimelineEnd | FrameFlags.CompletedAfter | FrameFlags.ClipStart | FrameFlags.ClipEnd));
        Assert.AreEqual(3u, RowOf(row).Position);
        Assert.AreEqual(0L, RowOf(row).Cycle);
        Assert.AreEqual(11f * 5 + 22f * 5, HealthOf(row).Value);

        TimelineConsumers.Reset();
        Tick(200u, int.MaxValue);
        Assert.AreEqual(0, TimelineConsumers.Count);
        Assert.AreEqual(3u, RowOf(row).Position);

        Tick(200u, 0);
        Assert.AreEqual(3u, RowOf(row).Position);

        TimelineConsumers.Reset();
        Tick(103u, -5);
        AssertRecords(
            new Record('A', 5, 22f, 2u, 102u, 0L, FrameFlags.Reverse | FrameFlags.TimelineEnd | FrameFlags.CompletedBefore | FrameFlags.ClipStart | FrameFlags.ClipEnd),
            new Record('A', 5, 11f, 0u, 100u, 0L, FrameFlags.Reverse | FrameFlags.TimelineStart | FrameFlags.ClipStart | FrameFlags.ClipEnd));
        Assert.AreEqual(0u, RowOf(row).Position);
        Assert.AreEqual(0L, RowOf(row).Cycle);
        Assert.AreEqual((11f * 5 + 22f * 5) * 2, HealthOf(row).Value);
    }

    [Test]
    public void TickResolvesBlendsOncePerVisitedFrame()
    {
        var asset = Load("fixture_b");
        var row = CreateRow(asset);

        TimelineConsumers.Reset();
        Tick(50u, 6);
        AssertRecords(
            new Record('B', 0, 0f, 0u, 50u, 0L, FrameFlags.TimelineStart | FrameFlags.ClipStart),
            new Record('B', 0, 0f, 1u, 51u, 0L, FrameFlags.None),
            new Record('B', 0, 0f, 2u, 52u, 0L, FrameFlags.None),
            new Record('B', 0, 10f, 3u, 53u, 0L, FrameFlags.None),
            new Record('B', 0, 10f, 4u, 54u, 0L, FrameFlags.None),
            new Record('B', 0, 10f, 5u, 55u, 0L, FrameFlags.TimelineEnd | FrameFlags.CompletedAfter | FrameFlags.ClipEnd));
        Assert.AreEqual(6u, RowOf(row).Position);

        var single = Load("fixture_b2");
        var singleRow = CreateRow(single);
        TimelineConsumers.Reset();
        Tick(60u, 3);
        Assert.AreEqual(3, TimelineConsumers.Count);
        Assert.AreEqual(0f + 8f * 0.5f, TimelineConsumers.At(2).Value);
        Assert.AreEqual(3u, RowOf(singleRow).Position);
    }

    [Test]
    public void TickExecutesAuthoredOrderAndMirroredConsumers()
    {
        var asset = Load("fixture_c");
        var row = CreateRow(asset);

        TimelineConsumers.Reset();
        Tick(50u, 1);
        AssertRecords(
            new Record('2', 1, 10f, 0u, 50u, 0L, FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedAfter | FrameFlags.ClipStart | FrameFlags.ClipEnd),
            new Record('1', 1, 10f, 0u, 50u, 0L, FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedAfter | FrameFlags.ClipStart | FrameFlags.ClipEnd),
            new Record('A', 4, 7f, 0u, 50u, 0L, FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedAfter | FrameFlags.ClipStart | FrameFlags.ClipEnd),
            new Record('2', 2, 20f, 0u, 50u, 0L, FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedAfter | FrameFlags.ClipStart | FrameFlags.ClipEnd),
            new Record('1', 2, 20f, 0u, 50u, 0L, FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedAfter | FrameFlags.ClipStart | FrameFlags.ClipEnd));
        Assert.AreEqual(1u, RowOf(row).Position);

        TimelineConsumers.Reset();
        Tick(51u, -1);
        AssertRecords(
            new Record('1', 2, 20f, 0u, 50u, 0L, FrameFlags.Reverse | FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedBefore | FrameFlags.ClipStart | FrameFlags.ClipEnd),
            new Record('2', 2, 20f, 0u, 50u, 0L, FrameFlags.Reverse | FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedBefore | FrameFlags.ClipStart | FrameFlags.ClipEnd),
            new Record('A', 4, 7f, 0u, 50u, 0L, FrameFlags.Reverse | FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedBefore | FrameFlags.ClipStart | FrameFlags.ClipEnd),
            new Record('1', 1, 10f, 0u, 50u, 0L, FrameFlags.Reverse | FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedBefore | FrameFlags.ClipStart | FrameFlags.ClipEnd),
            new Record('2', 1, 10f, 0u, 50u, 0L, FrameFlags.Reverse | FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedBefore | FrameFlags.ClipStart | FrameFlags.ClipEnd));
        Assert.AreEqual(0u, RowOf(row).Position);
    }

    [Test]
    public void ConsumerExceptionLeavesPrefixAndNoCommit()
    {
        var asset = Load("fixture_d");
        var row = CreateRow(asset);

        TimelineConsumers.Reset();
        LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Beta consumer failed"));
        Tick(30u, 2);
        LogAssert.NoUnexpectedReceived();
        AssertRecords(
            new Record('A', 3, 5f, 0u, 30u, 0L, FrameFlags.TimelineStart | FrameFlags.TimelineEnd | FrameFlags.CompletedAfter | FrameFlags.ClipStart | FrameFlags.ClipEnd));
        Assert.AreEqual(15f, HealthOf(row).Value);
        Assert.AreEqual(0u, RowOf(row).Position);
    }
}
