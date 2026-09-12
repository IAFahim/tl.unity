using System;
using System.Collections.Generic;
using Fixtures;
using NUnit.Framework;
using Tl;
using Tl.Unity;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using UnityRow = Tl.Unity.TimelineComponent;
using CoreRow = Tl.TimelineComponent;

namespace Tl.Unity.Tests;

[BurstCompile]
unsafe class BurstArms
{
    internal struct SinkData
    {
        public NativeList<Record>* List;
        public ulong HealthKey;
    }

    internal struct SinkTag { }

    internal static readonly SharedStatic<SinkData> Sink = SharedStatic<SinkData>.GetOrCreate<SinkTag>();

    internal delegate void ExecuteDel(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row);
    internal delegate void BindDel(ulong* keys, int keyCount, byte* table);

    static FunctionPointer<ExecuteDel> _alphaExecute;
    static FunctionPointer<BindDel> _alphaBind;
    static FunctionPointer<ExecuteDel> _betaExecute;
    static FunctionPointer<ExecuteDel> _phiFirst;
    static FunctionPointer<ExecuteDel> _phiSecond;
    static FunctionPointer<ExecuteDel> _blendExecute;
    static FunctionPointer<BindDel> _noBind;

    static delegate* unmanaged[Cdecl]<byte*, uint, uint, long, FrameFlags, void**, int, void> Fn(FunctionPointer<ExecuteDel> pointer)
        => (delegate* unmanaged[Cdecl]<byte*, uint, uint, long, FrameFlags, void**, int, void>)(void*)pointer.Value;

    static delegate* unmanaged[Cdecl]<ulong*, int, byte*, void> Fn(FunctionPointer<BindDel> pointer)
        => (delegate* unmanaged[Cdecl]<ulong*, int, byte*, void>)(void*)pointer.Value;

    internal static NativeList<Record> Install()
    {
        Sink.Data.HealthKey = TimelineEcs.ComponentKey<Health>();
        if (Sink.Data.List == null)
        {
            var slot = (NativeList<Record>*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<NativeList<Record>>(), 8, Allocator.Persistent);
            *slot = new NativeList<Record>(256, Allocator.Persistent);
            Sink.Data.List = slot;
        }
        Sink.Data.List->Clear();
        _alphaExecute = BurstCompiler.CompileFunctionPointer<ExecuteDel>(AlphaExecute);
        _alphaBind = BurstCompiler.CompileFunctionPointer<BindDel>(AlphaBind);
        _betaExecute = BurstCompiler.CompileFunctionPointer<ExecuteDel>(BetaExecute);
        _phiFirst = BurstCompiler.CompileFunctionPointer<ExecuteDel>(PhiFirst);
        _phiSecond = BurstCompiler.CompileFunctionPointer<ExecuteDel>(PhiSecond);
        _blendExecute = BurstCompiler.CompileFunctionPointer<ExecuteDel>(BlendExecute);
        _noBind = BurstCompiler.CompileFunctionPointer<BindDel>(NoBind);
        PairRuntime<AlphaTrack, AlphaClip>.ConsumeUnmanaged(Fn(_alphaExecute), Fn(_alphaBind));
        PairRuntime<BetaTrack, BetaClip>.ConsumeUnmanaged(Fn(_betaExecute), Fn(_noBind));
        PairRuntime<PhiTrack, PhiClip>.ConsumeUnmanaged(Fn(_phiFirst), Fn(_noBind));
        PairRuntime<PhiTrack, PhiClip>.ConsumeUnmanaged(Fn(_phiSecond), Fn(_noBind));
        PairRuntime<BlendTrack, BlendClip>.ConsumeUnmanaged(Fn(_blendExecute), Fn(_noBind));
        return *Sink.Data.List;
    }

    [BurstCompile]
    static void AlphaExecute(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
    {
        AlphaClip scratch = default;
        var current = TickFrame.ToFrame<AlphaTrack, AlphaClip>(slot, gameTick, tick, cycle, flags, ref scratch);
        Sink.Data.List->Add(new Record('A', current.Track.Code, current.Clip.Value, current.TimelineTick, current.GameTick, current.Cycle, current.Flags));
        var health = (Health*)columns[0];
        if (health != null) health[row].Value += current.Clip.Value * current.Track.Code;
    }

    [BurstCompile]
    static void AlphaBind(ulong* keys, int keyCount, byte* table)
    {
        var health = Sink.Data.HealthKey;
        for (var i = 0; i < keyCount; i++)
            if (keys[i] == health)
            {
                table[0] = (byte)(i + 1);
                break;
            }
    }

    [BurstCompile]
    static void BetaExecute(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
    {
        BetaClip scratch = default;
        var current = TickFrame.ToFrame<BetaTrack, BetaClip>(slot, gameTick, tick, cycle, flags, ref scratch);
        Sink.Data.List->Add(new Record('X', current.Track.Code, current.Clip.Value, current.TimelineTick, current.GameTick, current.Cycle, current.Flags));
    }

    [BurstCompile]
    static void PhiFirst(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
    {
        PhiClip scratch = default;
        var current = TickFrame.ToFrame<PhiTrack, PhiClip>(slot, gameTick, tick, cycle, flags, ref scratch);
        Sink.Data.List->Add(new Record('1', current.Track.Code, current.Clip.Value, current.TimelineTick, current.GameTick, current.Cycle, current.Flags));
    }

    [BurstCompile]
    static void PhiSecond(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
    {
        PhiClip scratch = default;
        var current = TickFrame.ToFrame<PhiTrack, PhiClip>(slot, gameTick, tick, cycle, flags, ref scratch);
        Sink.Data.List->Add(new Record('2', current.Track.Code, current.Clip.Value, current.TimelineTick, current.GameTick, current.Cycle, current.Flags));
    }

    [BurstCompile]
    static void BlendExecute(byte* slot, uint gameTick, uint tick, long cycle, FrameFlags flags, void** columns, int row)
    {
        BlendClip scratch = default;
        var current = TickFrame.ToFrame<BlendTrack, BlendClip>(slot, gameTick, tick, cycle, flags, ref scratch);
        Sink.Data.List->Add(new Record('B', 0, current.Clip.Amount, current.TimelineTick, current.GameTick, current.Cycle, current.Flags));
    }

    [BurstCompile]
    static void NoBind(ulong* keys, int keyCount, byte* table)
    {
    }
}

[TestFixture]
public sealed class BurstOracleTests
{
    static readonly List<TimelineAsset> Loaded = [];

    World _world;
    TimelineSystemGroup _managedGroup;
    TimelineSystemGroup _burstGroup;
    Entity _clock;
    EntityArchetype _rowArchetype;
    NativeList<Record> _sink;

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
        _world = new World("TlBurstOracle");
        _managedGroup = TimelineEcs.CreateGroup(_world);
        _burstGroup = TimelineBurst.CreateGroup(_world);
        _clock = _world.EntityManager.CreateEntity(typeof(TimelineClock));
        _rowArchetype = _world.EntityManager.CreateArchetype(typeof(UnityRow), typeof(Health));
    }

    [TearDown]
    public void TearDown()
    {
        _world.Dispose();
        _world = default;
    }

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

    private void ResetRow(Entity row, TimelineAsset asset)
    {
        _world.EntityManager.SetComponentData(row, new UnityRow { Reference = asset.Reference });
        _world.EntityManager.SetComponentData(row, new Health());
    }

    private void Tick(uint gameTick, int delta, bool burst)
    {
        _world.EntityManager.SetComponentData(_clock, new TimelineClock { GameTick = gameTick, Delta = delta });
        (burst ? _burstGroup : _managedGroup).Update();
    }

    private UnityRow RowOf(Entity entity) => _world.EntityManager.GetComponentData<UnityRow>(entity);

    private Health HealthOf(Entity entity) => _world.EntityManager.GetComponentData<Health>(entity);

    private List<Record> ManagedRecords()
    {
        var records = new List<Record>();
        for (var i = 0; i < TimelineConsumers.Count; i++) records.Add(TimelineConsumers.At(i));
        return records;
    }

    private List<Record> BurstRecords()
    {
        var records = new List<Record>();
        for (var i = 0; i < _sink.Length; i++) records.Add(_sink[i]);
        return records;
    }

    private static void AssertSame(List<Record> expected, List<Record> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count, "record count");
        for (var i = 0; i < expected.Count; i++)
            Assert.AreEqual(expected[i], actual[i], "record " + i);
    }

    [Test]
    public void BurstArmMatchesManagedArmForwardAndBackward()
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
        var burst = BurstRecords();

        AssertSame(managed, burst);
        Assert.AreEqual(4, burst.Count, "two visited clip frames forward and mirrored backward");
        Assert.AreEqual(0u, RowOf(row).Position);
        Assert.AreEqual(0L, RowOf(row).Cycle);
        Assert.AreEqual(managedHealth, HealthOf(row).Value, "health column parity");
        Assert.AreEqual(330f, HealthOf(row).Value, "both arms apply clip contributions twice");
    }

    [Test]
    public void BurstArmMirrorsConsumerOrderAndBlends()
    {
        var blend = Load("fixture_b");
        var blendRow = CreateRow(blend);
        var mixed = Load("fixture_c");
        var mixedRow = CreateRow(mixed);

        TimelineConsumers.Reset();
        Tick(50u, 6, false);
        Tick(51u, -1, false);
        var managed = ManagedRecords();

        ResetRow(blendRow, blend);
        ResetRow(mixedRow, mixed);
        TimelineConsumers.Reset();
        _sink.Clear();
        Tick(50u, 6, true);
        Tick(51u, -1, true);
        var burst = BurstRecords();

        AssertSame(managed, burst);
        Assert.Greater(burst.Count, 8, "blend frames, mirrored consumers, forward and reverse");
        Assert.AreEqual(5u, RowOf(blendRow).Position);
        Assert.AreEqual(0u, RowOf(mixedRow).Position);
    }

    [Test]
    public void BurstArmIsNonThrowingAndPreservesPrefix()
    {
        var asset = Load("fixture_d");
        var row = CreateRow(asset);

        TimelineConsumers.Reset();
        LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Beta consumer failed"));
        Tick(30u, 2, false);
        LogAssert.NoUnexpectedReceived();
        var managed = ManagedRecords();
        Assert.AreEqual(1, managed.Count, "managed arm stops at the throwing consumer");
        Assert.AreEqual(0u, RowOf(row).Position, "managed arm leaves the position uncommitted");

        ResetRow(row, asset);
        TimelineConsumers.Reset();
        _sink.Clear();
        Tick(30u, 2, true);
        var burst = BurstRecords();

        Assert.AreEqual(3, burst.Count, "burst arm records every step without throwing");
        Assert.AreEqual(managed[0], burst[0], "prefix record matches the managed arm");
        Assert.AreEqual('X', burst[1].Kind, "failing consumer is recorded as a non-throwing marker");
        Assert.AreEqual('A', burst[2].Kind, "execution continues past the non-throwing failure");
        Assert.AreEqual(1u, RowOf(row).Position);
        Assert.AreEqual(39f, HealthOf(row).Value, "alpha contributions apply, marker contributes none");
    }

    [Test]
    public unsafe void UnmanagedSeamDispatchRemainsCorrectUnderManagedCaller()
    {
        var asset = Load("fixture_a");
        var rows = stackalloc CoreRow[1];
        rows[0] = default;
        rows[0].Reference = asset.Reference;
        UnmanagedTickState state = default;
        var keys = stackalloc ulong[4];
        keys[0] = TimelineEcs.ComponentKey<Health>();
        var health = stackalloc Health[1];
        health[0] = default;
        var bases = stackalloc void*[4];
        bases[0] = health;

        Tl.Timeline.TickUnmanaged(&state, rows, 1, bases, keys, 1, 100u, 5);
        Assert.AreEqual(3u, rows[0].Position);
        Assert.AreEqual(11f * 5 + 22f * 5, health[0].Value);

        Tl.Timeline.TickUnmanaged(&state, rows, 1, bases, keys, 1, 103, -5);
        Assert.AreEqual(0u, rows[0].Position);
        Assert.AreEqual((11f * 5 + 22f * 5) * 2, health[0].Value);
    }

    [Test]
    public void BurstAndManagedArmsReportInformalTimings()
    {
        var asset = Load("fixture_a");
        var row = CreateRow(asset);
        const int ticks = 2000;

        var managedClock = new System.Diagnostics.Stopwatch();
        var managedRecords = 0;
        managedClock.Start();
        for (var i = 0; i < ticks; i += 2)
        {
            TimelineConsumers.Reset();
            Tick((uint)(200 + i), 1, false);
            managedRecords += TimelineConsumers.Count;
            TimelineConsumers.Reset();
            Tick((uint)(200 + i + 1), -1, false);
            managedRecords += TimelineConsumers.Count;
        }
        managedClock.Stop();

        ResetRow(row, asset);
        _sink.Clear();
        var burstClock = new System.Diagnostics.Stopwatch();
        var burstRecords = 0;
        burstClock.Start();
        for (var i = 0; i < ticks; i += 2)
        {
            _sink.Clear();
            Tick((uint)(200 + i), 1, true);
            burstRecords += _sink.Length;
            _sink.Clear();
            Tick((uint)(200 + i + 1), -1, true);
            burstRecords += _sink.Length;
        }
        burstClock.Stop();

        Assert.AreEqual(managedRecords, burstRecords, "record count parity under timing loop");
        Assert.Greater(burstRecords, 0, "ping-pong plan keeps producing frames");
        UnityEngine.Debug.Log($"tl burst timings: managed={managedClock.ElapsedMilliseconds}ms burst={burstClock.ElapsedMilliseconds}ms ticks={ticks} records={burstRecords}");
    }

    [Test]
    public unsafe void BurstCoordinatorJobBodyRemainsCorrectWithoutBurst()
    {
        var asset = Load("fixture_a");
        var row = CreateRow(asset);

        TimelineConsumers.Reset();
        Tick(100u, 1, false);
        Tick(101u, 1, false);
        Tick(102u, 1, false);
        Tick(103u, -3, false);
        var managed = ManagedRecords();

        ResetRow(row, asset);
        TimelineConsumers.Reset();
        _sink.Clear();

        var em = _world.EntityManager;
        var query = em.CreateEntityQuery(ComponentType.ReadWrite<UnityRow>(), ComponentType.ReadWrite<Health>());
        var chunks = query.ToArchetypeChunkArray(Allocator.Temp);
        var rowHandle = em.GetComponentTypeHandle<UnityRow>(false);
        var healthHandle = em.GetComponentTypeHandle<Health>(false);
        try
        {
            UnmanagedTickState state = default;
            var keys = stackalloc ulong[4];
            keys[0] = TimelineEcs.ComponentKey<Health>();
            for (var step = 0; step < 4; step++)
            {
                var gameTick = step == 3 ? 103u : (uint)(100 + step);
                var delta = step == 3 ? -3 : 1;
                for (var c = 0; c < chunks.Length; c++)
                {
                    var rows = chunks[c].GetNativeArray(ref rowHandle);
                    var health = chunks[c].GetNativeArray(ref healthHandle);
                    var bases = stackalloc void*[1];
                    bases[0] = (void*)NativeArrayUnsafeUtility.GetUnsafePtr(health);
                    Tl.Timeline.TickUnmanaged(&state, (CoreRow*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(rows), rows.Length, bases, keys, 1, gameTick, delta);
                }
            }

            AssertSame(managed, BurstRecords());
            Assert.AreEqual(0u, RowOf(row).Position);
            Assert.AreEqual(330f, HealthOf(row).Value, "no-Burst dispatch applies the same column contributions");
        }
        finally
        {
            chunks.Dispose();
        }
    }
}
