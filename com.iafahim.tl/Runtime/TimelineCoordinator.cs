using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Tl;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using CoreRow = Tl.TimelineComponent;

namespace Tl.Unity;

public static class TimelineEcs
{
    internal const int ColumnLimit = 4;

    internal readonly struct ColumnEntry
    {
        public readonly ComponentType Component;
        public readonly ulong Key;
        public readonly unsafe delegate*<ref SystemState, void> Create;
        public readonly unsafe delegate*<ref SystemState, ArchetypeChunk, void*> Pointer;

        public unsafe ColumnEntry(ComponentType component, ulong key, delegate*<ref SystemState, void> create, delegate*<ref SystemState, ArchetypeChunk, void*> pointer)
        {
            Component = component;
            Key = key;
            Create = create;
            Pointer = pointer;
        }
    }

    internal static readonly List<ColumnEntry> Columns = [];
    private static bool _sealed;

    public static unsafe void Column<T>() where T : unmanaged, IComponentData
    {
        if (_sealed) throw new InvalidOperationException("Timeline columns are sealed after coordinator creation.");
        if (Columns.Count == ColumnLimit) throw new InvalidOperationException("At most four timeline columns.");
        foreach (var entry in Columns)
            if (entry.Component.TypeIndex == ComponentType.ReadWrite<T>().TypeIndex)
                return;
        Columns.Add(new ColumnEntry(ComponentType.ReadWrite<T>(), TypeKey<T>.Value, &ColumnAccess<T>.Create, &ColumnAccess<T>.PointerOf));
    }

    public static ulong ComponentKey<T>() => TypeKey<T>.Value;

    public static TimelineSystemGroup CreateGroup(World world)
    {
        var group = world.CreateSystemManaged<TimelineSystemGroup>();
        var coordinator = world.CreateSystem<TimelineCoordinatorSystem>();
        group.AddSystemToUpdateList(coordinator);
        return group;
    }

    internal static void Seal() => _sealed = true;

    internal static unsafe int FillKeys(ulong* keys)
    {
        var columns = Columns;
        var count = columns.Count;
        for (var i = 0; i < count; i++) keys[i] = columns[i].Key;
        return count;
    }
}

internal static class ColumnAccess<T> where T : unmanaged, IComponentData
{
    private static ComponentTypeHandle<T> _handle;

    public static void Create(ref SystemState state) => _handle = state.GetComponentTypeHandle<T>();

    public static unsafe void* PointerOf(ref SystemState state, ArchetypeChunk chunk)
    {
        _handle.Update(ref state);
        return chunk.GetComponentDataPtrRW(ref _handle);
    }
}

[DisableAutoCreation]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class TimelineSystemGroup : ComponentSystemGroup
{
}

[DisableAutoCreation]
[UpdateInGroup(typeof(TimelineSystemGroup))]
public unsafe partial struct TimelineCoordinatorSystem : ISystem
{
    private EntityQuery _rows;
    private EntityQuery _clock;
    private ComponentTypeHandle<TimelineComponent> _rowHandle;

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
    }

    public void OnUpdate(ref SystemState state)
    {
        state.Dependency.Complete();
        if (!_clock.TryGetSingleton<TimelineClock>(out var clock))
            throw new InvalidOperationException("TimelineClock singleton is missing.");
        if (clock.Delta == 0) return;

        _rowHandle.Update(ref state);
        var columns = TimelineEcs.Columns;
        var columnCount = columns.Count;
        var chunks = _rows.ToArchetypeChunkArray(Allocator.Temp);
        try
        {
            foreach (var chunk in chunks)
            {
                void** bases = stackalloc void*[TimelineEcs.ColumnLimit];
                for (var i = 0; i < columnCount; i++) bases[i] = columns[i].Pointer(ref state, chunk);
                var rows = chunk.GetNativeArray(ref _rowHandle);
                var rowPointer = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(rows);
                new TimelineQuery(new Span<CoreRow>(rowPointer, rows.Length))
                    .TickCore(&TimelineEcs.FillKeys, bases, clock.GameTick, clock.Delta);
            }
        }
        finally
        {
            chunks.Dispose();
        }
    }
}
