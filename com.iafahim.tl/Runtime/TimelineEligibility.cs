using System;
using Tl;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using CoreRow = Tl.TimelineComponent;

namespace Tl.Unity;

public enum TimelineExclusionReason : byte
{
    MissingMandatoryColumn = 1,
    DisabledMandatoryColumn = 2,
}

public readonly struct TimelineExclusion
{
    public readonly Entity Row;
    public readonly int Chunk;
    public readonly int RowInChunk;
    public readonly TimelineExclusionReason Reason;

    public TimelineExclusion(Entity row, int chunk, int rowInChunk, TimelineExclusionReason reason)
    {
        Row = row;
        Chunk = chunk;
        RowInChunk = rowInChunk;
        Reason = reason;
    }
}

public static class TimelineEligibility
{
    public const int Capacity = 256;

    private static readonly TimelineExclusion[] Exclusions = new TimelineExclusion[Capacity];
    private static int _count;

    public static int Count => _count;

    public static TimelineExclusion At(int index) => Exclusions[index];

    public static void Reset() => _count = 0;

    public static unsafe void RecordExclusions(ref SystemState state, EntityQuery rows)
    {
        var rowHandle = state.GetComponentTypeHandle<TimelineComponent>();
        var entityHandle = state.GetEntityTypeHandle();
        var runs = new NativeList<TimelineChunkRows>(Allocator.Temp);
        var bases = new NativeList<IntPtr>(Allocator.Temp);
        try
        {
            Resolve(ref state, rows, ref rowHandle, ref entityHandle, runs, bases);
        }
        finally
        {
            runs.Dispose();
            bases.Dispose();
        }
    }

    internal static void Record(Entity row, int chunk, int rowInChunk, TimelineExclusionReason reason)
    {
        if (_count == Capacity) throw new InvalidOperationException("Timeline exclusion capacity exhausted.");
        Exclusions[_count++] = new TimelineExclusion(row, chunk, rowInChunk, reason);
    }

    internal static unsafe void Resolve(
        ref SystemState state,
        EntityQuery rows,
        ref ComponentTypeHandle<TimelineComponent> rowHandle,
        ref EntityTypeHandle entityHandle,
        NativeList<TimelineChunkRows> runs,
        NativeList<IntPtr> bases)
    {
        _count = 0;
        rowHandle.Update(ref state);
        entityHandle.Update(ref state);
        var columns = TimelineEcs.Columns;
        var columnCount = columns.Count;
        var chunks = rows.ToArchetypeChunkArray(Allocator.Temp);
        try
        {
            for (var c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                var chunkRows = chunk.GetNativeArray(ref rowHandle);
                var entities = chunk.GetNativeArray(entityHandle);
                var rowCount = chunkRows.Length;
                var rowPointer = (CoreRow*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(chunkRows);

                var present = true;
                for (var i = 0; i < columnCount; i++)
                    if (columns[i].Pointer(ref state, chunk) == null)
                    {
                        present = false;
                        break;
                    }
                if (!present)
                {
                    for (var r = 0; r < rowCount; r++)
                        Record(entities[r], c, r, TimelineExclusionReason.MissingMandatoryColumn);
                    continue;
                }

                var start = -1;
                for (var r = 0; r <= rowCount; r++)
                {
                    var eligible = r < rowCount;
                    if (eligible)
                        for (var i = 0; i < columnCount; i++)
                            if (!columns[i].Eligible(ref state, chunk, r))
                            {
                                eligible = false;
                                Record(entities[r], c, r, TimelineExclusionReason.DisabledMandatoryColumn);
                                break;
                            }
                    if (eligible && start < 0) start = r;
                    else if (!eligible && start >= 0)
                    {
                        Emit(runs, bases, rowPointer + start, r - start, ref state, chunk, columns, columnCount);
                        start = -1;
                    }
                }
            }
        }
        finally
        {
            chunks.Dispose();
        }
    }

    private static unsafe void Emit(
        NativeList<TimelineChunkRows> runs,
        NativeList<IntPtr> bases,
        CoreRow* rows,
        int count,
        ref SystemState state,
        ArchetypeChunk chunk,
        System.Collections.Generic.List<TimelineEcs.ColumnEntry> columns,
        int columnCount)
    {
        runs.Add(new TimelineChunkRows { Rows = rows, Count = count });
        for (var i = 0; i < columnCount; i++) bases.Add((IntPtr)columns[i].Pointer(ref state, chunk));
    }
}
