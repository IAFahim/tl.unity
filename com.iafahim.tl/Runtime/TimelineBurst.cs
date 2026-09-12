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
            Tl.Timeline.TickUnmanaged(state, table, (CoreRow*)rows.Rows, rows.Count, bases + chunk * TimelineEcs.ColumnLimit, keys, KeyCount, GameTick, Delta);
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
    private NativeArray<UnmanagedTickState> _state;
    private NativeArray<ulong> _keys;
    private int _keyCount;
    private bool _sealed;

    public void OnCreate(ref SystemState state)
    {
        TimelineEcs.Seal();
        var columns = TimelineEcs.Columns;
        var types = new ComponentType[1 + columns.Count];
        types[0] = ComponentType.ReadWrite<TimelineComponent>();
        for (var i = 0; i < columns.Count; i++) types[i + 1] = columns[i].Component;
        _rows = state.GetEntityQuery(types);
        _clock = state.GetEntityQuery(ComponentType.ReadOnly<TimelineClock>());
        _rowHandle = state.GetComponentTypeHandle<TimelineComponent>();
        for (var i = 0; i < columns.Count; i++) columns[i].Create(ref state);
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
        _rowHandle.Update(ref state);
        var columns = TimelineEcs.Columns;
        var columnCount = columns.Count;
        var chunks = _rows.ToArchetypeChunkArray(Allocator.Temp);
        try
        {
            var chunkRows = new NativeArray<TimelineChunkRows>(chunks.Length, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var bases = new NativeArray<IntPtr>(chunks.Length * TimelineEcs.ColumnLimit, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                for (var c = 0; c < chunks.Length; c++)
                {
                    var chunk = chunks[c];
                    var rows = chunk.GetNativeArray(ref _rowHandle);
                    for (var i = 0; i < columnCount; i++) bases[c * TimelineEcs.ColumnLimit + i] = (IntPtr)columns[i].Pointer(ref state, chunk);
                    chunkRows[c] = new TimelineChunkRows { Rows = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(rows), Count = rows.Length };
                }
                new TimelineUnmanagedTickJob
                {
                    Chunks = chunkRows,
                    Bases = bases,
                    State = _state,
                    Keys = _keys,
                    Table = (IntPtr)UnmanagedTable.Block,
                    KeyCount = _keyCount,
                    ChunkCount = chunks.Length,
                    GameTick = clock.GameTick,
                    Delta = clock.Delta,
                }.Run();
            }
            finally
            {
                bases.Dispose();
                chunkRows.Dispose();
            }
        }
        finally
        {
            chunks.Dispose();
        }
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_state.IsCreated) _state.Dispose();
        if (_keys.IsCreated) _keys.Dispose();
    }
}
