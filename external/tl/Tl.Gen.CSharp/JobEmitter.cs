using System.Globalization;
using System.Text;
using Tl.Gen.CSharp.Model;

namespace Tl.Gen.CSharp;

internal static class JobEmitter
{
    private const int FusedSchemaSourceBudget = 32768;
    private sealed record ScheduledOccurrence(JobDefinition Job, JobTrack? Track, int TrackStorage, int FirstStorage, int SecondStorage, uint WindowStart, uint WindowEnd, uint FactorStart, uint FactorLength);
    private sealed record ScheduledRegion(uint End, IReadOnlyList<ScheduledOccurrence> Occurrences);

    internal static string Normalize(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n');
    internal static IReadOnlyList<CompileArtifact> Emit(JobReadResult model)
    {
        var timelines = model.Timelines.OrderBy(Qualified, StringComparer.Ordinal).ToArray();
        var plans = Plans(timelines);
        var byName = Timelines(timelines);
        var queried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schema in model.Catalogs.SelectMany(static catalog => catalog.Schemas))
            if (CanFuse(schema, byName, plans))
                queried.UnionWith(schema.Assets);
        var files = timelines.Select((timeline, index) => new CompileArtifact($"TlJob{index}.g.cs", Timeline(timeline, plans[Qualified(timeline)], queried.Contains(Qualified(timeline))))).ToList();
        files.AddRange(model.Catalogs.OrderBy(static catalog => catalog.Namespace + "." + catalog.Name, StringComparer.Ordinal)
            .Select((catalog, index) => new CompileArtifact($"TlCatalog{index}.g.cs", Catalog(catalog, timelines, plans))));
        if (model.Consumers.Count != 0) files.Add(new("TlConsumerBinding.g.cs", Consumers(model.Consumers)));
        return files;
    }

    private static string Consumers(IReadOnlyList<JobConsumer> consumers)
    {
        var names = new HashSet<string>();
        var items = new List<(string Name, JobConsumer Consumer)>();
        foreach (var consumer in consumers)
        {
            var name = consumer.Job.TypeName.Split('<')[0].Split('.', ':').Last();
            while (!names.Add(name)) name += "_";
            items.Add((name, consumer));
        }
        var writer = new StringBuilder();
        void W(string text) => Line(writer, text);
        W("internal static unsafe class TlConsumerBinding");
        W("{");
        W("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        W("internal static void Install()");
        W("{");
        foreach (var (name, consumer) in items)
            W($"global::Tl.PairRuntime<{consumer.TrackTypeName}, {consumer.ClipTypeName}>.Consume(&Execute_{name}, &Bind_{name});");
        W("}");
        foreach (var (name, consumer) in items)
        {
            var job = consumer.Job;
            W($"private static void Execute_{name}(byte* __tlSlot, uint __tlGameTick, uint __tlTick, long __tlCycle, global::Tl.FrameFlags __tlFlags, void** __tlColumns, int __tlRow)");
            W("{");
            W($"{consumer.ClipTypeName} __tlClip = default; var __tlTyped = global::Tl.TickFrame.ToFrame<{consumer.TrackTypeName}, {consumer.ClipTypeName}>(__tlSlot, __tlGameTick, __tlTick, __tlCycle, __tlFlags, ref __tlClip);");
            for (var i = 0; i < job.Slots.Count; i++)
                W($"var @{job.Slots[i].Name} = ({job.Slots[i].TypeName}*)__tlColumns[{i}];");
            W($"{job.TypeName}.Execute(in __tlTyped{Arguments(job.Slots, "[__tlRow]")});");
            W("}");
            W($"private static void Bind_{name}(ulong* __tlKeys, int __tlKeyCount, byte* __tlIndices)");
            W("{");
            for (var i = 0; i < job.Slots.Count; i++)
            {
                var slot = job.Slots[i];
                W($"var __tlIdx{i} = FindKey(__tlKeys, __tlKeyCount, global::Tl.TypeKey<{slot.TypeName}>.Value);");
                W($"if (__tlIdx{i} < 0) throw new global::System.ArgumentException(\"{job.TypeName}: required column missing for registered consumer: {slot.TypeName}\");");
                W($"__tlIndices[{i}] = (byte)(__tlIdx{i} + 1);");
            }
            W("}");
        }
        W("private static int FindKey(ulong* k, int c, ulong v) { for (var i = 0; i < c; i++) if (k[i] == v) return i; return -1; }");
        W("}");
        return writer.ToString();
    }

    private static string Timeline(JobTimeline timeline, BoundOrderedTimelinePlan bound, bool queried)
    {
        var writer = Header(timeline.Namespace, timeline.Usings);
        var regions = Regions(timeline, bound);
        var operations = bound.OperationBindings;
        Line(writer, $"public readonly partial struct {timeline.Name}");
        Line(writer, "{");
        Line(writer, $"public const uint Duration = {U(timeline.Duration)};");
        Line(writer, $"public const bool Loops = {Bool(timeline.Loops)};");
        Line(writer, $"public const int TrackCount = {timeline.Tracks.Count};");
        Line(writer, $"public const int ClipCount = {timeline.Clips.Count};");
        Line(writer, $"public const int MaxStageCount = {regions.Select(static region => region.Occurrences.Count).DefaultIfEmpty().Max()};");
        var uniqueBindings = bound.Plan.UniquePayloads.Select(unique => bound.PayloadBindings[(int)unique.Id.Value]).ToArray();
        for (var index = 0; index < uniqueBindings.Length; index++)
            Line(writer, $"private static readonly {uniqueBindings[index].TypeName} __tlData{index} = {uniqueBindings[index].Expression};");
        var sizes = uniqueBindings.Select(static binding => $"global::System.Runtime.CompilerServices.Unsafe.SizeOf<{binding.TypeName}>()");
        Line(writer, $"public static int StaticDataBytes => {string.Join(" + ", sizes.DefaultIfEmpty("0"))};");
        Line(writer, "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        Line(writer, "public static bool Select(in global::Tl.TimelineState state, bool reverse, out global::Tl.TimelineState next, out uint tick, out long cycle, out global::Tl.FrameFlags flags)");
        Line(writer, "=> global::Tl.TimelineMovement.Select(in state, Duration, Loops, reverse, out next, out tick, out cycle, out flags);");
        Line(writer, "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        Line(writer, "internal static long FrameCycle(in global::Tl.TimelineState state, global::Tl.FrameFlags flags)");
        Line(writer, timeline.Loops
            ? "=> (flags & global::Tl.FrameFlags.Reverse) != 0 && state.Position == 0 ? unchecked(state.Cycle - 1) : state.Cycle;"
            : "=> 0L;");
        Line(writer, "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        Line(writer, "internal static global::Tl.TimelineState Commit(in global::Tl.TimelineState state, uint tick, global::Tl.FrameFlags flags)");
        if (timeline.Loops)
        {
            Line(writer, "=> (flags & global::Tl.FrameFlags.Reverse) != 0");
            Line(writer, "? new global::Tl.TimelineState(state.Asset, tick, FrameCycle(in state, flags))");
            Line(writer, ": (flags & global::Tl.FrameFlags.TimelineEnd) != 0");
            Line(writer, "? new global::Tl.TimelineState(state.Asset, 0u, unchecked(state.Cycle + 1))");
            Line(writer, " : new global::Tl.TimelineState(state.Asset, tick + 1u, state.Cycle);");
        }
        else
            Line(writer, "=> new global::Tl.TimelineState(state.Asset, (flags & global::Tl.FrameFlags.Reverse) != 0 ? tick : tick + 1u);");
        for (var operation = 0; operation < operations.Length; operation++)
        {
            EmitOperation(writer, regions, operations[operation], operation, false);
            EmitOperation(writer, regions, operations[operation], operation, true);
        }
        if (queried)
        {
            var slots = Slots([timeline]);
            EmitRow(writer, regions, slots, false);
            EmitRow(writer, regions, slots, true);
        }
        Line(writer, "}");
        return writer.ToString();
    }

    private static void EmitRow(
        StringBuilder writer,
        IReadOnlyList<ScheduledRegion> regions,
        IReadOnlyList<TimelineSlot> slots,
        bool reverse)
    {
        var direction = reverse ? "Reverse" : "Forward";
        Line(writer, "[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        Line(writer, $"internal static void Execute{direction}(uint __tlTick, uint __tlGameTick, long __tlCycle, global::Tl.FrameFlags __tlFlags{Parameters(slots)})");
        Line(writer, "{");
        for (var regionIndex = 0; regionIndex < regions.Count; regionIndex++)
        {
            var region = regions[regionIndex];
            Line(writer, $"{(regionIndex == 0 ? "if" : "else if")} (__tlTick < {U(region.End)})");
            Line(writer, "{");
            var occurrences = reverse ? region.Occurrences.Reverse() : region.Occurrences;
            foreach (var occurrence in occurrences)
            {
                Line(writer, "{");
                Invoke(writer, occurrence);
                Line(writer, "}");
            }
            Line(writer, "}");
        }
        Line(writer, "}");
    }

    private static void EmitOperation(
        StringBuilder writer,
        IReadOnlyList<ScheduledRegion> regions,
        JobDefinition operation,
        int operationIndex,
        bool reverse)
    {
        var direction = reverse ? "Reverse" : "Forward";
        Line(writer, "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        Line(writer, $"internal static void Execute{direction}{operationIndex}(ushort __tlStage, uint __tlTick, uint __tlGameTick, long __tlCycle, global::Tl.FrameFlags __tlFlags{Parameters(operation.Slots)})");
        Line(writer, "{");
        for (var regionIndex = 0; regionIndex < regions.Count; regionIndex++)
        {
            var region = regions[regionIndex];
            Line(writer, $"{(regionIndex == 0 ? "if" : "else if")} (__tlTick < {U(region.End)})");
            Line(writer, "{");
            var occurrences = reverse ? region.Occurrences.Reverse().ToArray() : region.Occurrences.ToArray();
            var matches = occurrences.Select((occurrence, stage) => (Occurrence: occurrence, Stage: stage))
                .Where(pair => StringComparer.Ordinal.Equals(pair.Occurrence.Job.TypeName, operation.TypeName)).ToArray();
            if (matches.Length != 0)
            {
                Line(writer, "switch (__tlStage)");
                Line(writer, "{");
                foreach (var match in matches)
                {
                    Line(writer, $"case {match.Stage}:");
                    Line(writer, "{");
                    Invoke(writer, match.Occurrence);
                    Line(writer, "break;");
                    Line(writer, "}");
                }
                Line(writer, "}");
            }
            Line(writer, "}");
        }
        Line(writer, "}");
    }

    private static string Catalog(
        JobCatalog catalog,
        IReadOnlyList<JobTimeline> timelines,
        IReadOnlyDictionary<string, BoundOrderedTimelinePlan> plans)
    {
        var byName = Timelines(timelines);
        var assets = catalog.Schemas.SelectMany(static schema => schema.Assets).Distinct(StringComparer.Ordinal)
            .OrderBy(static asset => asset, StringComparer.Ordinal).Select(asset => byName[asset]).ToArray();
        var ids = assets.Select((asset, index) => (Name: Qualified(asset), Id: index + 1))
            .ToDictionary(static pair => pair.Name, static pair => pair.Id, StringComparer.Ordinal);
        var writer = Header(catalog.Namespace, []);
        Line(writer, $"public readonly partial struct {catalog.Name}");
        Line(writer, "{");
        Line(writer, $"public const int AssetCount = {assets.Length};");
        Line(writer, "public static int StateBytes => global::System.Runtime.CompilerServices.Unsafe.SizeOf<State>();");
        Line(writer, $"public static int StaticDataBytes => {string.Join(" + ", assets.Select(asset => Qualified(asset) + ".StaticDataBytes").DefaultIfEmpty("0"))};");
        Line(writer, "public enum Asset : uint");
        Line(writer, "{");
        Line(writer, "None = 0,");
        foreach (var asset in assets)
            Line(writer, $"{asset.Name} = {ids[Qualified(asset)]},");
        Line(writer, "}");
        Line(writer, "public struct State");
        Line(writer, "{");
        Line(writer, "internal global::Tl.TimelineState Value;");
        Line(writer, "internal uint PendingTick;");
        Line(writer, "internal global::Tl.FrameFlags PendingFlags;");
        Line(writer, "internal bool Pending;");
        Line(writer, "public State(Asset asset, uint position = 0, long cycle = 0)");
        Line(writer, "{");
        Line(writer, "Value = new global::Tl.TimelineState((uint)asset, position, cycle);");
        Line(writer, "PendingTick = 0;");
        Line(writer, "PendingFlags = global::Tl.FrameFlags.None;");
        Line(writer, "Pending = false;");
        Line(writer, "}");
        Line(writer, "public Asset Timeline => (Asset)Value.Asset;");
        Line(writer, "public uint Position => Value.Position;");
        Line(writer, "public long Cycle => Value.Cycle;");
        Line(writer, "}");
        Line(writer, "public readonly struct Query");
        Line(writer, "{");
        foreach (var schema in catalog.Schemas)
        {
            var members = schema.Assets.Select(asset => byName[asset]).ToArray();
            var slots = Slots(members);
            Line(writer, $"public {schema.Name}Query {schema.Name}(global::System.Span<State> __tlStates{Columns(slots)}) => new(__tlStates{string.Concat(slots.Select(static slot => ", @" + slot.Name))});");
        }
        Line(writer, "}");
        foreach (var schema in catalog.Schemas)
            Query(writer, schema, schema.Assets.Select(asset => byName[asset]).ToArray(), ids, plans, CanFuse(schema, byName, plans));
        Line(writer, "}");
        return writer.ToString();
    }

    private static void Query(
        StringBuilder writer,
        JobSchema schema,
        IReadOnlyList<JobTimeline> assets,
        IReadOnlyDictionary<string, int> ids,
        IReadOnlyDictionary<string, BoundOrderedTimelinePlan> plans,
        bool fused)
    {
        var slots = Slots(assets);
        var operations = assets.SelectMany(asset => plans[Qualified(asset)].OperationBindings).GroupBy(static operation => operation.TypeName, StringComparer.Ordinal)
            .Select(static group => group.First()).OrderBy(static operation => operation.TypeName, StringComparer.Ordinal).ToArray();
        var maxStages = assets.Select(asset => plans[Qualified(asset)].Plan.Regions.Select(static region => (int)region.OccurrenceCount).DefaultIfEmpty().Max()).DefaultIfEmpty().Max();
        Line(writer, $"public readonly ref struct {schema.Name}Query");
        Line(writer, "{");
        Line(writer, "private readonly global::System.Span<State> __tlStates;");
        foreach (var slot in slots)
            Line(writer, $"private readonly {ColumnType(slot)} @{slot.Name};");
        Line(writer, $"internal {schema.Name}Query(global::System.Span<State> __tlStates{Columns(slots)})");
        Line(writer, "{");
        Line(writer, "this.__tlStates = __tlStates;");
        foreach (var slot in slots)
        {
            Line(writer, $"if (@{slot.Name}.Length != __tlStates.Length) throw new global::System.ArgumentException(\"Column length must equal timeline row count.\", nameof(@{slot.Name}));");
            Line(writer, $"this.@{slot.Name} = @{slot.Name};");
        }
        var columns = new[] { (Name: "__tlStates", Writable: true) }.Concat(slots.Select(static slot => (Name: "@" + slot.Name, Writable: slot.Mode != SlotMode.Input))).ToArray();
        for (var first = 0; first < columns.Length; first++)
            for (var second = first + 1; second < columns.Length; second++)
                if (columns[first].Writable || columns[second].Writable)
                    Line(writer, $"if (global::System.MemoryExtensions.Overlaps((global::System.ReadOnlySpan<byte>)global::System.Runtime.InteropServices.MemoryMarshal.AsBytes({columns[first].Name}), global::System.Runtime.InteropServices.MemoryMarshal.AsBytes({columns[second].Name}))) throw new global::System.ArgumentException(\"Writable component columns must not overlap other columns.\");");
        Line(writer, "Validate();");
        Line(writer, "}");
        Line(writer, "private void Validate()");
        Line(writer, "{");
        Line(writer, "foreach (ref readonly var __tlState in __tlStates)");
        Line(writer, "switch (__tlState.Value.Asset)");
        Line(writer, "{");
        Line(writer, "case 0:");
        foreach (var asset in assets)
            Line(writer, $"case {ids[Qualified(asset)]}:");
        Line(writer, "break;");
        Line(writer, "default: throw new global::System.ArgumentException(\"Timeline does not belong to this query schema.\");");
        Line(writer, "}");
        Line(writer, "}");
        Line(writer, "public void Tick(uint gameTick)");
        Line(writer, "{");
        Line(writer, "if (__tlStates.IsEmpty) return;");
        if (!fused)
        {
            Line(writer, "__tlTickMany(gameTick, 1);");
            Line(writer, "return;");
        }
        else
        {
            Line(writer, "if (__tlStates.Length != 1)");
            Line(writer, "{");
            Line(writer, "__tlTickMany(gameTick, 1);");
            Line(writer, "return;");
            Line(writer, "}");
            Line(writer, "ref var __tlState = ref __tlStates[0];");
            Line(writer, "switch (__tlState.Value.Asset)");
            Line(writer, "{");
            Line(writer, "case 0:");
            Line(writer, "__tlState.Pending = false;");
            Line(writer, "return;");
            foreach (var asset in assets)
            {
                Line(writer, $"case {ids[Qualified(asset)]}:");
                Line(writer, $"__tlForward{ids[Qualified(asset)]}(ref __tlState, gameTick);");
                Line(writer, "return;");
            }
            Line(writer, "default: throw new global::System.ArgumentException(\"Timeline does not belong to this query schema.\");");
            Line(writer, "}");
        }
        Line(writer, "}");
        Line(writer, "public void Tick(uint gameTick, int delta)");
        Line(writer, "{");
        Line(writer, "if (delta == 0 || __tlStates.IsEmpty) return;");
        if (fused)
        {
            Line(writer, "if (__tlStates.Length == 1)");
            Line(writer, "{");
            Line(writer, "ref var __tlState = ref __tlStates[0];");
            Line(writer, "switch (__tlState.Value.Asset)");
            Line(writer, "{");
            Line(writer, "case 0:");
            Line(writer, "__tlState.Pending = false;");
            Line(writer, "return;");
            foreach (var asset in assets)
            {
                Line(writer, $"case {ids[Qualified(asset)]}:");
                Line(writer, "{");
                EmitSingleRowTick(writer, ids[Qualified(asset)]);
                Line(writer, "return;");
                Line(writer, "}");
            }
            Line(writer, "default: throw new global::System.ArgumentException(\"Timeline does not belong to this query schema.\");");
            Line(writer, "}");
            Line(writer, "}");
        }
        Line(writer, "__tlTickMany(gameTick, delta);");
        Line(writer, "}");
        Line(writer, "[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        Line(writer, "private void __tlTickMany(uint gameTick, int delta)");
        Line(writer, "{");
        Line(writer, "Validate();");
        Line(writer, "bool __tlReverse = delta < 0;");
        Line(writer, "long __tlRemaining = __tlReverse ? -(long)delta : delta;");
        Line(writer, "while (__tlRemaining-- != 0)");
        Line(writer, "{");
        Line(writer, "bool __tlMoved = false;");
        Line(writer, "if (__tlReverse) gameTick = unchecked(gameTick - 1);");
        Line(writer, "for (int __tlRow = 0; __tlRow < __tlStates.Length; __tlRow++)");
        Line(writer, "{");
        Line(writer, "ref var __tlState = ref __tlStates[__tlRow];");
        Line(writer, "__tlState.Pending = false;");
        Line(writer, "switch (__tlState.Value.Asset)");
        Line(writer, "{");
        foreach (var asset in assets)
        {
            Line(writer, $"case {ids[Qualified(asset)]}:");
            Line(writer, "{");
            var name = Qualified(asset);
            Line(writer, $"if (!{name}.Select(in __tlState.Value, __tlReverse, out _, out __tlState.PendingTick, out _, out __tlState.PendingFlags)) break;");
            Line(writer, "__tlState.Pending = true;");
            Line(writer, "__tlMoved = true;");
            Line(writer, "break;");
            Line(writer, "}");
        }
        Line(writer, "}");
        Line(writer, "}");
        Line(writer, "if (!__tlMoved) break;");
        Line(writer, $"for (ushort __tlStage = 0; __tlStage < {maxStages}; __tlStage++)");
        Line(writer, "{");
        foreach (var operation in operations)
        {
            Line(writer, "for (int __tlRow = 0; __tlRow < __tlStates.Length; __tlRow++)");
            Line(writer, "{");
            Line(writer, "ref readonly var __tlState = ref __tlStates[__tlRow];");
            Line(writer, "if (!__tlState.Pending) continue;");
            Line(writer, "switch (__tlState.Value.Asset)");
            Line(writer, "{");
            foreach (var asset in assets.Where(asset => plans[Qualified(asset)].OperationBindings.Any(candidate => candidate.TypeName == operation.TypeName)))
            {
                var localOperation = plans[Qualified(asset)].OperationBindings.Select((candidate, index) => (candidate, index))
                    .Single(pair => pair.candidate.TypeName == operation.TypeName);
                Line(writer, $"case {ids[Qualified(asset)]}:");
                Line(writer, $"if (__tlReverse) {Qualified(asset)}.ExecuteReverse{localOperation.index}(__tlStage, __tlState.PendingTick, gameTick, {Qualified(asset)}.FrameCycle(in __tlState.Value, __tlState.PendingFlags), __tlState.PendingFlags{Arguments(operation.Slots, "[__tlRow]")});");
                Line(writer, $"else {Qualified(asset)}.ExecuteForward{localOperation.index}(__tlStage, __tlState.PendingTick, gameTick, {Qualified(asset)}.FrameCycle(in __tlState.Value, __tlState.PendingFlags), __tlState.PendingFlags{Arguments(operation.Slots, "[__tlRow]")});");
                Line(writer, "break;");
            }
            Line(writer, "}");
            Line(writer, "}");
        }
        Line(writer, "}");
        Line(writer, "for (int __tlRow = 0; __tlRow < __tlStates.Length; __tlRow++)");
        Line(writer, "{");
        Line(writer, "ref var __tlState = ref __tlStates[__tlRow];");
        Line(writer, "if (!__tlState.Pending) continue;");
        Line(writer, "switch (__tlState.Value.Asset)");
        Line(writer, "{");
        foreach (var asset in assets)
        {
            Line(writer, $"case {ids[Qualified(asset)]}:");
            Line(writer, $"__tlState.Value = {Qualified(asset)}.Commit(in __tlState.Value, __tlState.PendingTick, __tlState.PendingFlags);");
            Line(writer, "break;");
        }
        Line(writer, "}");
        Line(writer, "__tlState.Pending = false;");
        Line(writer, "}");
        Line(writer, "if (!__tlReverse) gameTick = unchecked(gameTick + 1);");
        Line(writer, "}");
        Line(writer, "}");
        if (fused)
            foreach (var asset in assets)
            {
                EmitSingleRowMove(writer, asset, ids[Qualified(asset)], Slots([asset]), false);
                EmitSingleRowMove(writer, asset, ids[Qualified(asset)], Slots([asset]), true);
            }
        Line(writer, "}");
    }

    private static bool CanFuse(
        JobSchema schema,
        IReadOnlyDictionary<string, JobTimeline> timelines,
        IReadOnlyDictionary<string, BoundOrderedTimelinePlan> plans)
    {
        var bytes = 0;
        foreach (var name in schema.Assets)
        {
            var timeline = timelines[name];
            var regions = Regions(timeline, plans[name]);
            var writer = new StringBuilder();
            var slots = Slots([timeline]);
            EmitRow(writer, regions, slots, false);
            EmitRow(writer, regions, slots, true);
            bytes += Encoding.UTF8.GetByteCount(writer.ToString());
            if (bytes > FusedSchemaSourceBudget)
                return false;
        }
        return true;
    }

    private static void EmitSingleRowTick(StringBuilder writer, int asset)
    {
        Line(writer, "if (delta == 1)");
        Line(writer, "{");
        Line(writer, $"__tlForward{asset}(ref __tlState, gameTick);");
        Line(writer, "return;");
        Line(writer, "}");
        Line(writer, "if (delta == -1)");
        Line(writer, "{");
        Line(writer, $"__tlReverse{asset}(ref __tlState, unchecked(gameTick - 1u));");
        Line(writer, "return;");
        Line(writer, "}");
        Line(writer, "bool __tlSingleReverse = delta < 0;");
        Line(writer, "long __tlSingleRemaining = __tlSingleReverse ? -(long)delta : delta;");
        Line(writer, "while (__tlSingleRemaining-- != 0)");
        Line(writer, "{");
        Line(writer, "if (__tlSingleReverse)");
        Line(writer, "{");
        Line(writer, "gameTick = unchecked(gameTick - 1u);");
        Line(writer, $"if (!__tlReverse{asset}(ref __tlState, gameTick)) break;");
        Line(writer, "}");
        Line(writer, "else");
        Line(writer, "{");
        Line(writer, $"if (!__tlForward{asset}(ref __tlState, gameTick)) break;");
        Line(writer, "gameTick = unchecked(gameTick + 1u);");
        Line(writer, "}");
        Line(writer, "}");
    }

    private static void EmitSingleRowMove(
        StringBuilder writer,
        JobTimeline asset,
        int assetIndex,
        IReadOnlyList<TimelineSlot> slots,
        bool reverse)
    {
        var direction = reverse ? "Reverse" : "Forward";
        var name = Qualified(asset);
        Line(writer, $"private bool __tl{direction}{assetIndex}(ref State __tlState, uint gameTick)");
        Line(writer, "{");
        Line(writer, $"if (!{name}.Select(in __tlState.Value, {Bool(reverse)}, out var __tlNext, out var __tlTick, out var __tlCycle, out var __tlFlags)) return false;");
        Line(writer, $"{name}.Execute{direction}(__tlTick, gameTick, __tlCycle, __tlFlags{Arguments(slots, "[0]")});");
        Line(writer, "__tlState.Value = __tlNext;");
        Line(writer, "return true;");
        Line(writer, "}");
    }

    private static IReadOnlyList<ScheduledRegion> Regions(JobTimeline timeline, BoundOrderedTimelinePlan bound)
    {
        var regions = new List<ScheduledRegion>(bound.Plan.Regions.Length);
        foreach (var region in bound.Plan.Regions)
        {
            var occurrences = new List<ScheduledOccurrence>(region.OccurrenceCount);
            for (var stage = 0; stage < region.OccurrenceCount; stage++)
            {
                var occurrence = bound.Plan.Occurrences[(int)region.OccurrenceOffset + stage];
                var job = bound.OperationBindings[occurrence.OperationIndex];
                if ((occurrence.Flags & (Compiler.OrderedOccurrenceFlags.BeforeHook | Compiler.OrderedOccurrenceFlags.AfterHook)) != 0)
                {
                    occurrences.Add(new(job, null, -1, -1, -1, 0, 0, 0, 0));
                    continue;
                }
                var track = timeline.Tracks.Single(candidate => candidate.Index == occurrence.TrackIndex);
                var firstIndex = checked((int)occurrence.FirstPayloadIndex - timeline.Tracks.Count);
                var secondIndex = occurrence.SecondPayloadIndex == Compiler.ValidatedOrderedTimelinePlan.NoPayload
                    ? -1
                    : checked((int)occurrence.SecondPayloadIndex - timeline.Tracks.Count);
                var first = timeline.Clips[firstIndex];
                var second = secondIndex < 0 ? null : timeline.Clips[secondIndex];
                occurrences.Add(new(
                    job,
                    track,
                    checked((int)bound.Plan.PayloadStorageIndices[(int)occurrence.TrackPayloadIndex]),
                    checked((int)bound.Plan.PayloadStorageIndices[(int)occurrence.FirstPayloadIndex]),
                    secondIndex < 0 ? -1 : checked((int)bound.Plan.PayloadStorageIndices[(int)occurrence.SecondPayloadIndex]),
                    second is null ? first.Start : Math.Min(first.Start, second.Start),
                    second is null ? first.End : Math.Max(first.End, second.End),
                    occurrence.FactorStart,
                    occurrence.FactorLength));
            }
            regions.Add(new(region.End, occurrences));
        }
        return regions;
    }

    private static void Invoke(StringBuilder writer, ScheduledOccurrence occurrence)
    {
        if (occurrence.Track is null)
        {
            Line(writer, "var __tlFrame = new global::Tl.TimelineFrame(__tlGameTick, __tlTick, __tlCycle, __tlFlags);");
            Line(writer, $"{occurrence.Job.TypeName}.Execute(in __tlFrame{Arguments(occurrence.Job.Slots)});");
            return;
        }
        var track = occurrence.Track;
        var clipExpression = $"__tlData{occurrence.FirstStorage}";
        if (occurrence.SecondStorage >= 0)
        {
            var factor = occurrence.FactorLength <= 1 ? "0.5f" : $"(__tlTick - {U(occurrence.FactorStart)}) / (float){U(occurrence.FactorLength - 1)}";
            Line(writer, $"__tlData{occurrence.TrackStorage}.Blend(in __tlData{occurrence.FirstStorage}, in __tlData{occurrence.SecondStorage}, {factor}, out var __tlResolved);");
            clipExpression = "__tlResolved";
        }
        Line(writer, "var __tlWorkFlags = __tlFlags;");
        Line(writer, $"if (__tlTick == {U(occurrence.WindowStart)}) __tlWorkFlags |= global::Tl.FrameFlags.ClipStart;");
        Line(writer, $"if (__tlTick == {U(occurrence.WindowEnd - 1)}) __tlWorkFlags |= global::Tl.FrameFlags.ClipEnd;");
        Line(writer, $"var __tlWork = new global::Tl.Frame<{track.TypeName}, {track.ClipTypeName}>(in __tlData{occurrence.TrackStorage}, in {clipExpression}, __tlGameTick, __tlTick, __tlCycle, {track.Index}, __tlWorkFlags);");
        Line(writer, $"{track.Job.TypeName}.Execute(in __tlWork{Arguments(track.Job.Slots)});");
    }

    internal static IReadOnlyList<TimelineSlot> Slots(IEnumerable<JobTimeline> timelines)
        => timelines.SelectMany(static timeline => timeline.Before.Concat(timeline.Tracks.Select(static track => track.Job)).Concat(timeline.After))
            .SelectMany(static job => job.Slots).GroupBy(static slot => slot.Name + "\0" + slot.TypeName, StringComparer.Ordinal)
            .Select(static group => new TimelineSlot(group.First().Name, group.First().TypeName,
                group.Any(static slot => slot.Mode != SlotMode.Input) ? SlotMode.Reference : SlotMode.Input)).ToArray();

    private static string Parameters(IEnumerable<TimelineSlot> slots)
        => string.Concat(slots.Select(static slot => $", {Mode(slot)} {slot.TypeName} @{slot.Name}"));

    private static string Arguments(IEnumerable<TimelineSlot> slots, string suffix = "")
        => string.Concat(slots.Select(slot => $", {Mode(slot)} @{slot.Name}{suffix}"));

    private static string Columns(IEnumerable<TimelineSlot> slots)
        => string.Concat(slots.Select(static slot => $", {ColumnType(slot)} @{slot.Name}"));

    private static string ColumnType(TimelineSlot slot)
        => $"global::System.{(slot.Mode == SlotMode.Input ? "ReadOnlySpan" : "Span")}<{slot.TypeName}>";

    private static string Mode(TimelineSlot slot) => slot.Mode == SlotMode.Input ? "in" : "ref";
    private static string Qualified(JobTimeline timeline) => "global::" + (timeline.Namespace.Length == 0 ? "" : timeline.Namespace + ".") + timeline.Name;

    private static Dictionary<string, BoundOrderedTimelinePlan> Plans(IReadOnlyList<JobTimeline> timelines)
    {
        var result = new Dictionary<string, BoundOrderedTimelinePlan>(timelines.Count, StringComparer.Ordinal);
        foreach (var timeline in timelines)
            result.Add(Qualified(timeline), JobTimelinePlanAdapter.Create(timeline));
        return result;
    }

    private static Dictionary<string, JobTimeline> Timelines(IReadOnlyList<JobTimeline> timelines)
    {
        var result = new Dictionary<string, JobTimeline>(timelines.Count, StringComparer.Ordinal);
        foreach (var timeline in timelines)
            result.Add(Qualified(timeline), timeline);
        return result;
    }
    private static string U(uint value) => value.ToString(CultureInfo.InvariantCulture) + "u";
    private static string Bool(bool value) => value ? "true" : "false";
    private static void Line(StringBuilder writer, string text) => writer.Append(text).Append('\n');

    private static StringBuilder Header(string nameSpace, IReadOnlyList<string> usings)
    {
        var writer = new StringBuilder();
        Line(writer, "#nullable enable");
        foreach (var directive in usings)
            Line(writer, directive);
        if (nameSpace.Length != 0)
            Line(writer, $"namespace {nameSpace};");
        return writer;
    }
}
