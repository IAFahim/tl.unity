using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using CoreRow = Tl.TimelineComponent;

namespace Tl.Unity;

public static class TimelineBurst
{
    public static TimelineSystemGroup CreateGroup(World world)
    {
        var group = world.CreateSystemManaged<TimelineSystemGroup>();
        var coordinator = world.CreateSystem<TimelineUnmanagedCoordinatorSystem>();
        group.AddSystemToUpdateList(coordinator);
        return group;
    }
}

public unsafe struct TimelineChunkRows
{
    public void* Rows;
    public int Count;
}

[BurstCompile]
internal unsafe struct TimelineUnmanagedTickJob : IJob
{
    internal NativeArray<TimelineChunkRows> Chunks;
    internal NativeArray<IntPtr> Bases;
    internal NativeArray<UnmanagedTickState> State;
    internal NativeArray<ulong> Keys;
    [NativeDisableUnsafePtrRestriction] internal IntPtr Table;
    internal int KeyCount;
    internal int ChunkCount;
    internal uint GameTick;
    internal int Delta;

    public void Execute()
    {
        var state = (UnmanagedTickState*)NativeArrayUnsafeUtility.GetUnsafePtr(State);
        var keys = (ulong*)NativeArrayUnsafeUtility.GetUnsafePtr(Keys);
        var bases = (void**)NativeArrayUnsafeUtility.GetUnsafePtr(Bases);
        var table = (byte*)Table;
        for (var chunk = 0; chunk < ChunkCount; chunk++)
        {
            var rows = Chunks[chunk];
            Tl.Timeline.TickUnmanaged(state, table, (CoreRow*)rows.Rows, rows.Count, bases + chunk * KeyCount, keys, KeyCount, GameTick, Delta);
        }
    }
}

[DisableAutoCreation]
[UpdateInGroup(typeof(TimelineSystemGroup))]
public unsafe partial struct TimelineUnmanagedCoordinatorSystem : ISystem
{
    private EntityQuery _rows;
    private EntityQuery _clock;
    private ComponentTypeHandle<TimelineComponent> _rowHandle;
    private EntityTypeHandle _entityHandle;
    private NativeArray<UnmanagedTickState> _state;
    private NativeArray<ulong> _keys;
    private int _keyCount;
    private bool _sealed;

    public void OnCreate(ref SystemState state)
    {
        TimelineEcs.Seal();
        _rows = state.GetEntityQuery(ComponentType.ReadWrite<TimelineComponent>());
        _clock = state.GetEntityQuery(ComponentType.ReadOnly<TimelineClock>());
        _rowHandle = state.GetComponentTypeHandle<TimelineComponent>();
        _entityHandle = state.GetEntityTypeHandle();
        for (var i = 0; i < TimelineEcs.Columns.Count; i++) TimelineEcs.Columns[i].Create(ref state);
        _state = new NativeArray<UnmanagedTickState>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        _keys = new NativeArray<ulong>(TimelineEcs.ColumnLimit, Allocator.Persistent, NativeArrayOptions.ClearMemory);
    }

    public void OnUpdate(ref SystemState state)
    {
        state.Dependency.Complete();
        if (!_clock.TryGetSingleton<TimelineClock>(out var clock))
            throw new InvalidOperationException("TimelineClock singleton is missing.");
        if (clock.Delta == 0) return;

        if (!_sealed)
        {
            _keyCount = TimelineEcs.FillKeys((ulong*)NativeArrayUnsafeUtility.GetUnsafePtr(_keys));
            _sealed = true;
        }
        var runs = new NativeList<TimelineChunkRows>(Allocator.Temp);
        var bases = new NativeList<IntPtr>(Allocator.Temp);
        try
        {
            TimelineEligibility.Resolve(ref state, _rows, ref _rowHandle, ref _entityHandle, runs, bases);
            var chunkRows = new NativeArray<TimelineChunkRows>(runs.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var baseArray = new NativeArray<IntPtr>(bases.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                for (var i = 0; i < runs.Length; i++) chunkRows[i] = runs[i];
                for (var i = 0; i < bases.Length; i++) baseArray[i] = bases[i];
                new TimelineUnmanagedTickJob
                {
                    Chunks = chunkRows,
                    Bases = baseArray,
                    State = _state,
                    Keys = _keys,
                    Table = (IntPtr)UnmanagedTable.Block,
                    KeyCount = _keyCount,
                    ChunkCount = runs.Length,
                    GameTick = clock.GameTick,
                    Delta = clock.Delta,
                }.Run();
            }
            finally
            {
                baseArray.Dispose();
                chunkRows.Dispose();
            }
        }
        finally
        {
            bases.Dispose();
            runs.Dispose();
        }
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_state.IsCreated) _state.Dispose();
        if (_keys.IsCreated) _keys.Dispose();
    }
}
