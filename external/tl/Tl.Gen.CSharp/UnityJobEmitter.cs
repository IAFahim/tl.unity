using System.Globalization;
using System.Text;
using Tl.Gen.CSharp.Model;

namespace Tl.Gen.CSharp;

internal static class UnityJobEmitter
{
    private sealed record ScheduledOccurrence(JobDefinition Job, JobTrack? Track, int TrackStorage, int FirstStorage, int SecondStorage, uint WindowStart, uint WindowEnd, uint FactorStart, uint FactorLength);
    private sealed record ScheduledRegion(uint End, IReadOnlyList<ScheduledOccurrence> Occurrences);
    private sealed record CatalogSlot(string Name, string TypeName, string ComponentName, int Index);

    internal static IReadOnlyList<CompileArtifact> Emit(JobReadResult model)
    {
        var timelines = model.Timelines.OrderBy(Qualified, StringComparer.Ordinal).ToArray();
        var plans = new Dictionary<string, BoundOrderedTimelinePlan>(timelines.Length, StringComparer.Ordinal);
        var byName = new Dictionary<string, JobTimeline>(timelines.Length, StringComparer.Ordinal);
        foreach (var timeline in timelines)
        {
            var name = Qualified(timeline);
            plans.Add(name, JobTimelinePlanAdapter.Create(timeline));
            byName.Add(name, timeline);
        }
        Validate(model, plans, byName);
        var files = timelines.Select((timeline, index) => new CompileArtifact(
            $"TlUnityJob{index}.g.cs",
            Timeline(timeline, plans[Qualified(timeline)]))).ToList();
        files.AddRange(model.Catalogs.OrderBy(static catalog => catalog.Namespace + "." + catalog.Name, StringComparer.Ordinal)
            .Select((catalog, index) => new CompileArtifact(
                $"TlUnityCatalog{index}.g.cs",
                Catalog(catalog, byName, plans))));
        return files;
    }

    private static void Validate(
        JobReadResult model,
        IReadOnlyDictionary<string, BoundOrderedTimelinePlan> plans,
        IReadOnlyDictionary<string, JobTimeline> byName)
    {
        if (model.Catalogs.Count == 0)
            throw new InvalidOperationException("The Unity Entities backend requires an explicit timeline catalog.");
        foreach (var catalog in model.Catalogs)
        {
            var assets = catalog.Schemas.SelectMany(static schema => schema.Assets).Distinct(StringComparer.Ordinal)
                .Select(asset => byName[asset]).ToArray();
            foreach (var group in assets.SelectMany(asset => plans[Qualified(asset)].OperationBindings)
                .GroupBy(static operation => operation.TypeName, StringComparer.Ordinal))
            {
                var first = group.First();
                if (group.Skip(1).Any(operation => !operation.Slots.SequenceEqual(first.Slots)))
                    throw new InvalidOperationException($"Unity catalog '{catalog.Namespace}.{catalog.Name}' binds operation '{first.TypeName}' with inconsistent slots.");
            }
        }
    }

    private static string Timeline(JobTimeline timeline, BoundOrderedTimelinePlan bound)
    {
        var writer = Header(timeline.Namespace);
        var regions = Regions(timeline, bound);
        var operations = bound.OperationBindings;
        Line(writer, $"public readonly struct {timeline.Name}");
        Line(writer, "{");
        Line(writer, $"public const uint Duration = {U(timeline.Duration)};");
        Line(writer, $"public const bool Loops = {Bool(timeline.Loops)};");
        Line(writer, $"public const int TrackCount = {timeline.Tracks.Count};");
        Line(writer, $"public const int ClipCount = {timeline.Clips.Count};");
        Line(writer, $"public const int MaxStageCount = {regions.Select(static region => region.Occurrences.Count).DefaultIfEmpty().Max()};");
        var uniqueBindings = bound.Plan.UniquePayloads.Select(unique => bound.PayloadBindings[(int)unique.Id.Value]).ToArray();
        for (var index = 0; index < uniqueBindings.Length; index++)
            Line(writer, $"private static readonly {uniqueBindings[index].TypeName} __tlData{index} = {uniqueBindings[index].Expression};");
        var sizes = uniqueBindings.Select(static binding => $"global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<{binding.TypeName}>()");
        Line(writer, $"public static int StaticDataBytes => {string.Join(" + ", sizes.DefaultIfEmpty("0"))};");
        Line(writer, "internal static bool Select(in global::Tl.TimelineState state, bool reverse, out global::Tl.TimelineState next, out uint tick, out long cycle, out global::Tl.FrameFlags flags)");
        Line(writer, "{");
        Line(writer, "return global::Tl.TimelineMovement.Select(in state, Duration, Loops, reverse, out next, out tick, out cycle, out flags);");
        Line(writer, "}");
        Line(writer, "internal static long FrameCycle(in global::Tl.TimelineState state, global::Tl.FrameFlags flags)");
        Line(writer, "{");
        Line(writer, timeline.Loops
            ? "return (flags & global::Tl.FrameFlags.Reverse) != 0 && state.Position == 0 ? unchecked(state.Cycle - 1) : state.Cycle;"
            : "return 0L;");
        Line(writer, "}");
        Line(writer, "internal static global::Tl.TimelineState Commit(in global::Tl.TimelineState state, uint tick, global::Tl.FrameFlags flags)");
        Line(writer, "{");
        if (timeline.Loops)
        {
            Line(writer, "if ((flags & global::Tl.FrameFlags.Reverse) != 0) return new global::Tl.TimelineState(state.Asset, tick, FrameCycle(in state, flags));");
            Line(writer, "if ((flags & global::Tl.FrameFlags.TimelineEnd) != 0) return new global::Tl.TimelineState(state.Asset, 0u, unchecked(state.Cycle + 1));");
            Line(writer, "return new global::Tl.TimelineState(state.Asset, tick + 1u, state.Cycle);");
        }
        else
            Line(writer, "return new global::Tl.TimelineState(state.Asset, (flags & global::Tl.FrameFlags.Reverse) != 0 ? tick : tick + 1u);");
        Line(writer, "}");
        for (var operation = 0; operation < operations.Length; operation++)
        {
            EmitOperation(writer, regions, operations[operation], operation, false);
            EmitOperation(writer, regions, operations[operation], operation, true);
        }
        Line(writer, "}");
        Footer(writer, timeline.Namespace);
        return writer.ToString();
    }

    private static void EmitOperation(
        StringBuilder writer,
        IReadOnlyList<ScheduledRegion> regions,
        JobDefinition operation,
        int operationIndex,
        bool reverse)
    {
        var direction = reverse ? "Reverse" : "Forward";
        Line(writer, $"internal static void Execute{direction}{operationIndex}(ushort stage, uint tick, uint gameTick, long cycle, global::Tl.FrameFlags flags{Parameters(operation.Slots)})");
        Line(writer, "{");
        for (var regionIndex = 0; regionIndex < regions.Count; regionIndex++)
        {
            var region = regions[regionIndex];
            Line(writer, $"{(regionIndex == 0 ? "if" : "else if")} (tick < {U(region.End)})");
            Line(writer, "{");
            var occurrences = reverse ? region.Occurrences.Reverse().ToArray() : region.Occurrences.ToArray();
            var matches = occurrences.Select((occurrence, stage) => (Occurrence: occurrence, Stage: stage))
                .Where(pair => StringComparer.Ordinal.Equals(pair.Occurrence.Job.TypeName, operation.TypeName)).ToArray();
            if (matches.Length != 0)
            {
                Line(writer, "switch (stage)");
                Line(writer, "{");
                foreach (var match in matches)
                {
                    Line(writer, $"case {match.Stage.ToString(CultureInfo.InvariantCulture)}:");
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
        IReadOnlyDictionary<string, JobTimeline> byName,
        IReadOnlyDictionary<string, BoundOrderedTimelinePlan> plans)
    {
        var assets = catalog.Schemas.SelectMany(static schema => schema.Assets).Distinct(StringComparer.Ordinal)
            .OrderBy(static asset => asset, StringComparer.Ordinal).Select(asset => byName[asset]).ToArray();
        var ids = assets.Select((asset, index) => (Name: Qualified(asset), Id: index + 1))
            .ToDictionary(static pair => pair.Name, static pair => pair.Id, StringComparer.Ordinal);
        var operations = assets.SelectMany(asset => plans[Qualified(asset)].OperationBindings)
            .GroupBy(static operation => operation.TypeName, StringComparer.Ordinal)
            .Select(static group => group.First()).OrderBy(static operation => operation.TypeName, StringComparer.Ordinal).ToArray();
        var maxStages = assets.Select(asset => plans[Qualified(asset)].Plan.Regions
                .Select(static region => (int)region.OccurrenceCount).DefaultIfEmpty().Max())
            .DefaultIfEmpty().Max();
        var slots = CatalogSlots(catalog, byName);
        var slotIds = slots.ToDictionary(slot => SlotKey(slot.Name, slot.TypeName), static slot => slot, StringComparer.Ordinal);
        var writer = Header(catalog.Namespace);
        Line(writer, $"public readonly partial struct {catalog.Name}");
        Line(writer, "{");
        Line(writer, $"public const int AssetCount = {assets.Length.ToString(CultureInfo.InvariantCulture)};");
        Line(writer, "public enum Asset : uint");
        Line(writer, "{");
        Line(writer, "None = 0,");
        foreach (var asset in assets)
            Line(writer, $"{asset.Name} = {ids[Qualified(asset)].ToString(CultureInfo.InvariantCulture)},");
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
        Line(writer, "PendingTick = 0u;");
        Line(writer, "PendingFlags = global::Tl.FrameFlags.None;");
        Line(writer, "Pending = false;");
        Line(writer, "}");
        Line(writer, "public Asset Timeline { get { return (Asset)Value.Asset; } }");
        Line(writer, "public uint Position { get { return Value.Position; } }");
        Line(writer, "public long Cycle { get { return Value.Cycle; } }");
        Line(writer, "}");
        Line(writer, "public static int StateBytes => global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<State>();");
        Line(writer, $"public static int StaticDataBytes => {string.Join(" + ", assets.Select(asset => Qualified(asset) + ".StaticDataBytes").DefaultIfEmpty("0"))};");
        Line(writer, "public struct TimelineComponent : global::Unity.Entities.IComponentData");
        Line(writer, "{");
        Line(writer, "public State Value;");
        Line(writer, "}");
        foreach (var schema in catalog.Schemas)
            Line(writer, $"public struct {schema.Name} : global::Unity.Entities.IComponentData, global::Unity.Entities.IEnableableComponent {{ }}");
        foreach (var slot in slots)
        {
            Line(writer, $"public struct {slot.ComponentName} : global::Unity.Entities.IComponentData");
            Line(writer, "{");
            Line(writer, $"public {slot.TypeName} Value;");
            Line(writer, $"public {slot.ComponentName}({slot.TypeName} value) {{ Value = value; }}");
            Line(writer, "}");
        }
        EmitScheduler(writer, catalog, byName, operations, maxStages, slots, slotIds);
        Line(writer, "}");
        EmitJobs(writer, catalog, assets, ids, byName, plans, operations, slotIds);
        Footer(writer, catalog.Namespace);
        return writer.ToString();
    }

    private static void EmitScheduler(
        StringBuilder writer,
        JobCatalog catalog,
        IReadOnlyDictionary<string, JobTimeline> byName,
        IReadOnlyList<JobDefinition> operations,
        int maxStages,
        IReadOnlyList<CatalogSlot> slots,
        IReadOnlyDictionary<string, CatalogSlot> slotIds)
    {
        Line(writer, "public struct Scheduler");
        Line(writer, "{");
        Line(writer, "private global::Unity.Entities.EntityQuery __tlTimelineQuery;");
        Line(writer, $"private global::Unity.Entities.ComponentTypeHandle<{catalog.Name}.TimelineComponent> __tlTimelineHandle;");
        for (var schema = 0; schema < catalog.Schemas.Count; schema++)
            Line(writer, $"private global::Unity.Entities.ComponentTypeHandle<{catalog.Name}.{catalog.Schemas[schema].Name}> __tlSchema{schema}Handle;");
        foreach (var slot in slots)
            Line(writer, $"private global::Unity.Entities.ComponentTypeHandle<{catalog.Name}.{slot.ComponentName}> __tlSlot{slot.Index}Handle;");
        for (var operation = 0; operation < operations.Count; operation++)
            Line(writer, $"private __Tl{catalog.Name}Operation{operation}Job.InternalCompilerQueryAndHandleData __tlOperation{operation}Data;");
        Line(writer, $"private __Tl{catalog.Name}CommitJob.InternalCompilerQueryAndHandleData __tlCommitData;");
        Line(writer, "public void OnCreate(ref global::Unity.Entities.SystemState state)");
        Line(writer, "{");
        Line(writer, $"__tlTimelineQuery = state.GetEntityQuery(global::Unity.Entities.ComponentType.ReadWrite<{catalog.Name}.TimelineComponent>());");
        Line(writer, $"__tlTimelineHandle = state.GetComponentTypeHandle<{catalog.Name}.TimelineComponent>(false);");
        for (var schema = 0; schema < catalog.Schemas.Count; schema++)
            Line(writer, $"__tlSchema{schema}Handle = state.GetComponentTypeHandle<{catalog.Name}.{catalog.Schemas[schema].Name}>(true);");
        foreach (var slot in slots)
            Line(writer, $"__tlSlot{slot.Index}Handle = state.GetComponentTypeHandle<{catalog.Name}.{slot.ComponentName}>(true);");
        for (var operation = 0; operation < operations.Count; operation++)
            Line(writer, $"__tlOperation{operation}Data.Init(ref state, true);");
        Line(writer, "__tlCommitData.Init(ref state, true);");
        Line(writer, "}");
        Line(writer, "public void Tick(ref global::Unity.Entities.SystemState state, uint gameTick, int delta = 1)");
        Line(writer, "{");
        Line(writer, "if (delta == 0) return;");
        Line(writer, "bool reverse = delta < 0;");
        Line(writer, "long remaining = reverse ? -(long)delta : delta;");
        Line(writer, "__tlTimelineHandle.Update(ref state);");
        for (var schema = 0; schema < catalog.Schemas.Count; schema++)
            Line(writer, $"__tlSchema{schema}Handle.Update(ref state);");
        foreach (var slot in slots)
            Line(writer, $"__tlSlot{slot.Index}Handle.Update(ref state);");
        for (var operation = 0; operation < operations.Count; operation++)
            Line(writer, $"__tlOperation{operation}Data.__TypeHandle.Update(ref state);");
        Line(writer, "__tlCommitData.__TypeHandle.Update(ref state);");
        Line(writer, "var dependency = state.Dependency;");
        Line(writer, "while (remaining-- != 0)");
        Line(writer, "{");
        Line(writer, "if (reverse) gameTick = unchecked(gameTick - 1u);");
        Line(writer, $"var clearJob = new __Tl{catalog.Name}ClearJob {{ TimelineHandle = __tlTimelineHandle }};");
        Line(writer, "dependency = clearJob.ScheduleParallel(__tlTimelineQuery, dependency);");
        for (var schema = 0; schema < catalog.Schemas.Count; schema++)
        {
            var schemaSlots = JobEmitter.Slots(catalog.Schemas[schema].Assets.Select(asset => byName[asset]));
            var values = new List<string>
            {
                "TimelineHandle = __tlTimelineHandle",
                $"SchemaHandle = __tlSchema{schema}Handle",
                "Reverse = reverse",
            };
            values.AddRange(schemaSlots.Select(slot =>
            {
                var binding = slotIds[SlotKey(slot.Name, slot.TypeName)];
                return $"Slot{binding.Index}Handle = __tlSlot{binding.Index}Handle";
            }));
            Line(writer, $"var select{schema}Job = new __Tl{catalog.Name}Select{schema}Job {{ {string.Join(", ", values)} }};");
            Line(writer, $"dependency = select{schema}Job.ScheduleParallel(__tlTimelineQuery, dependency);");
        }
        if (maxStages != 0)
        {
            Line(writer, $"for (ushort stage = 0; stage < {maxStages.ToString(CultureInfo.InvariantCulture)}; stage++)");
            Line(writer, "{");
            for (var operation = 0; operation < operations.Count; operation++)
            {
                Line(writer, $"var operation{operation}Job = new __Tl{catalog.Name}Operation{operation}Job {{ Stage = stage, GameTick = gameTick, Reverse = reverse }};");
                Line(writer, $"dependency = __tlOperation{operation}Data.ScheduleParallel(ref operation{operation}Job, __tlOperation{operation}Data.DefaultQuery, dependency);");
            }
            Line(writer, "}");
        }
        Line(writer, $"var commitJob = new __Tl{catalog.Name}CommitJob();");
        Line(writer, "dependency = __tlCommitData.ScheduleParallel(ref commitJob, __tlCommitData.DefaultQuery, dependency);");
        Line(writer, "if (!reverse) gameTick = unchecked(gameTick + 1u);");
        Line(writer, "}");
        Line(writer, "state.Dependency = dependency;");
        Line(writer, "}");
        Line(writer, "}");
    }

    private static void EmitJobs(
        StringBuilder writer,
        JobCatalog catalog,
        IReadOnlyList<JobTimeline> assets,
        IReadOnlyDictionary<string, int> ids,
        IReadOnlyDictionary<string, JobTimeline> byName,
        IReadOnlyDictionary<string, BoundOrderedTimelinePlan> plans,
        IReadOnlyList<JobDefinition> operations,
        IReadOnlyDictionary<string, CatalogSlot> slotIds)
    {
        Line(writer, "[global::Unity.Burst.BurstCompile(CompileSynchronously = true)]");
        Line(writer, $"internal struct __Tl{catalog.Name}ClearJob : global::Unity.Entities.IJobChunk");
        Line(writer, "{");
        Line(writer, $"public global::Unity.Entities.ComponentTypeHandle<{catalog.Name}.TimelineComponent> TimelineHandle;");
        EmitChunkSignature(writer);
        Line(writer, "{");
        Line(writer, "var timelines = chunk.GetNativeArray(ref TimelineHandle);");
        Line(writer, "for (int index = 0; index < chunk.Count; index++)");
        Line(writer, "{");
        Line(writer, "var timeline = timelines[index];");
        Line(writer, "timeline.Value.Pending = false;");
        Line(writer, "timelines[index] = timeline;");
        Line(writer, "}");
        Line(writer, "}");
        Line(writer, "}");

        for (var schema = 0; schema < catalog.Schemas.Count; schema++)
        {
            var definition = catalog.Schemas[schema];
            var slots = JobEmitter.Slots(definition.Assets.Select(asset => byName[asset]));
            Line(writer, "[global::Unity.Burst.BurstCompile(CompileSynchronously = true)]");
            Line(writer, $"internal struct __Tl{catalog.Name}Select{schema}Job : global::Unity.Entities.IJobChunk");
            Line(writer, "{");
            Line(writer, $"public global::Unity.Entities.ComponentTypeHandle<{catalog.Name}.TimelineComponent> TimelineHandle;");
            Line(writer, $"[global::Unity.Collections.ReadOnly] public global::Unity.Entities.ComponentTypeHandle<{catalog.Name}.{definition.Name}> SchemaHandle;");
            foreach (var slot in slots)
            {
                var binding = slotIds[SlotKey(slot.Name, slot.TypeName)];
                Line(writer, $"[global::Unity.Collections.ReadOnly] public global::Unity.Entities.ComponentTypeHandle<{catalog.Name}.{binding.ComponentName}> Slot{binding.Index}Handle;");
            }
            Line(writer, "public bool Reverse;");
            EmitChunkSignature(writer);
            Line(writer, "{");
            var checks = new[] { "chunk.Has(ref SchemaHandle)" }.Concat(slots.Select(slot => $"chunk.Has(ref Slot{slotIds[SlotKey(slot.Name, slot.TypeName)].Index}Handle)"));
            Line(writer, $"if (!({string.Join(" && ", checks)})) return;");
            Line(writer, "var timelines = chunk.GetNativeArray(ref TimelineHandle);");
            Line(writer, "for (int index = 0; index < chunk.Count; index++)");
            Line(writer, "{");
            Line(writer, "if (!chunk.IsComponentEnabled(ref SchemaHandle, index)) continue;");
            Line(writer, "var timeline = timelines[index];");
            Line(writer, "switch (timeline.Value.Value.Asset)");
            Line(writer, "{");
            foreach (var assetName in definition.Assets.Distinct(StringComparer.Ordinal))
            {
                var asset = byName[assetName];
                Line(writer, $"case {ids[Qualified(asset)].ToString(CultureInfo.InvariantCulture)}u:");
                Line(writer, $"if ({Qualified(asset)}.Select(in timeline.Value.Value, Reverse, out _, out timeline.Value.PendingTick, out _, out timeline.Value.PendingFlags))");
                Line(writer, "{");
                Line(writer, "timeline.Value.Pending = true;");
                Line(writer, "timelines[index] = timeline;");
                Line(writer, "}");
                Line(writer, "break;");
            }
            Line(writer, "}");
            Line(writer, "}");
            Line(writer, "}");
            Line(writer, "}");
        }

        for (var operation = 0; operation < operations.Count; operation++)
        {
            var definition = operations[operation];
            Line(writer, "[global::Unity.Burst.BurstCompile(CompileSynchronously = true)]");
            Line(writer, "[WithOptions(global::Unity.Entities.EntityQueryOptions.IgnoreComponentEnabledState)]");
            Line(writer, $"internal partial struct __Tl{catalog.Name}Operation{operation}Job : IJobEntity");
            Line(writer, "{");
            Line(writer, "public ushort Stage;");
            Line(writer, "public uint GameTick;");
            Line(writer, "public bool Reverse;");
            Line(writer, $"private void Execute(in {catalog.Name}.TimelineComponent timeline{WrapperParameters(catalog.Name, definition.Slots, slotIds)})");
            Line(writer, "{");
            Line(writer, "if (!timeline.Value.Pending) return;");
            Line(writer, "switch (timeline.Value.Value.Asset)");
            Line(writer, "{");
            foreach (var asset in assets.Where(asset => plans[Qualified(asset)].OperationBindings.Any(candidate => candidate.TypeName == definition.TypeName)))
            {
                var local = plans[Qualified(asset)].OperationBindings.Select((candidate, index) => (candidate, index))
                    .Single(pair => pair.candidate.TypeName == definition.TypeName);
                Line(writer, $"case {ids[Qualified(asset)].ToString(CultureInfo.InvariantCulture)}u:");
                Line(writer, $"if (Reverse) {Qualified(asset)}.ExecuteReverse{local.index}(Stage, timeline.Value.PendingTick, GameTick, {Qualified(asset)}.FrameCycle(in timeline.Value.Value, timeline.Value.PendingFlags), timeline.Value.PendingFlags{WrapperArguments(definition.Slots)});");
                Line(writer, $"else {Qualified(asset)}.ExecuteForward{local.index}(Stage, timeline.Value.PendingTick, GameTick, {Qualified(asset)}.FrameCycle(in timeline.Value.Value, timeline.Value.PendingFlags), timeline.Value.PendingFlags{WrapperArguments(definition.Slots)});");
                Line(writer, "break;");
            }
            Line(writer, "}");
            Line(writer, "}");
            Line(writer, "}");
        }

        Line(writer, "[global::Unity.Burst.BurstCompile(CompileSynchronously = true)]");
        Line(writer, $"internal partial struct __Tl{catalog.Name}CommitJob : IJobEntity");
        Line(writer, "{");
        Line(writer, $"private void Execute(ref {catalog.Name}.TimelineComponent timeline)");
        Line(writer, "{");
        Line(writer, "if (timeline.Value.Pending)");
        Line(writer, "{");
        Line(writer, "switch (timeline.Value.Value.Asset)");
        Line(writer, "{");
        foreach (var asset in assets)
        {
            Line(writer, $"case {ids[Qualified(asset)].ToString(CultureInfo.InvariantCulture)}u:");
            Line(writer, $"timeline.Value.Value = {Qualified(asset)}.Commit(in timeline.Value.Value, timeline.Value.PendingTick, timeline.Value.PendingFlags);");
            Line(writer, "break;");
        }
        Line(writer, "}");
        Line(writer, "}");
        Line(writer, "timeline.Value.Pending = false;");
        Line(writer, "}");
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
                    occurrences.Add(new ScheduledOccurrence(job, null, -1, -1, -1, 0, 0, 0, 0));
                    continue;
                }
                var track = timeline.Tracks.Single(candidate => candidate.Index == occurrence.TrackIndex);
                var firstIndex = checked((int)occurrence.FirstPayloadIndex - timeline.Tracks.Count);
                var secondIndex = occurrence.SecondPayloadIndex == Compiler.ValidatedOrderedTimelinePlan.NoPayload
                    ? -1
                    : checked((int)occurrence.SecondPayloadIndex - timeline.Tracks.Count);
                var first = timeline.Clips[firstIndex];
                var second = secondIndex < 0 ? null : timeline.Clips[secondIndex];
                occurrences.Add(new ScheduledOccurrence(
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
            regions.Add(new ScheduledRegion(region.End, occurrences));
        }
        return regions;
    }

    private static void Invoke(StringBuilder writer, ScheduledOccurrence occurrence)
    {
        if (occurrence.Track is null)
        {
            Line(writer, "var frame = new global::Tl.TimelineFrame(gameTick, tick, cycle, flags);");
            Line(writer, $"{occurrence.Job.TypeName}.Execute(in frame{Arguments(occurrence.Job.Slots)});");
            return;
        }
        var track = occurrence.Track;
        var clipExpression = $"__tlData{occurrence.FirstStorage}";
        if (occurrence.SecondStorage >= 0)
        {
            var factor = occurrence.FactorLength <= 1 ? "0.5f" : $"(tick - {U(occurrence.FactorStart)}) / (float){U(occurrence.FactorLength - 1)}";
            Line(writer, $"__tlData{occurrence.TrackStorage}.Blend(in __tlData{occurrence.FirstStorage}, in __tlData{occurrence.SecondStorage}, {factor}, out var resolved);");
            clipExpression = "resolved";
        }
        Line(writer, "var workFlags = flags;");
        Line(writer, $"if (tick == {U(occurrence.WindowStart)}) workFlags |= global::Tl.FrameFlags.ClipStart;");
        Line(writer, $"if (tick == {U(occurrence.WindowEnd - 1)}) workFlags |= global::Tl.FrameFlags.ClipEnd;");
        Line(writer, $"var frame = new global::Tl.Frame<{track.TypeName}, {track.ClipTypeName}>(in __tlData{occurrence.TrackStorage}, in {clipExpression}, gameTick, tick, cycle, {track.Index.ToString(CultureInfo.InvariantCulture)}, workFlags);");
        Line(writer, $"{track.Job.TypeName}.Execute(in frame{Arguments(track.Job.Slots)});");
    }

    private static void EmitChunkSignature(StringBuilder writer)
        => Line(writer, "public void Execute(in global::Unity.Entities.ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in global::Unity.Burst.Intrinsics.v128 chunkEnabledMask)");

    private static IReadOnlyList<CatalogSlot> CatalogSlots(
        JobCatalog catalog,
        IReadOnlyDictionary<string, JobTimeline> byName)
        => catalog.Schemas.SelectMany(schema => JobEmitter.Slots(schema.Assets.Select(asset => byName[asset])))
            .GroupBy(static slot => SlotKey(slot.Name, slot.TypeName), StringComparer.Ordinal)
            .Select(static group => new TimelineSlot(
                group.First().Name,
                group.First().TypeName,
                group.Any(static slot => slot.Mode != SlotMode.Input) ? SlotMode.Reference : SlotMode.Input))
            .OrderBy(static slot => slot.Name, StringComparer.Ordinal)
            .ThenBy(static slot => slot.TypeName, StringComparer.Ordinal)
            .Select((slot, index) => new CatalogSlot(slot.Name, slot.TypeName, "Role" + index.ToString(CultureInfo.InvariantCulture) + Pascal(slot.Name), index))
            .ToArray();

    private static string WrapperParameters(
        string catalogName,
        IEnumerable<TimelineSlot> slots,
        IReadOnlyDictionary<string, CatalogSlot> bindings)
        => string.Concat(slots.Select(slot =>
        {
            var binding = bindings[SlotKey(slot.Name, slot.TypeName)];
            return $", {(slot.Mode == SlotMode.Input ? "in" : "ref")} {catalogName}.{binding.ComponentName} @{slot.Name}";
        }));

    private static string WrapperArguments(IEnumerable<TimelineSlot> slots)
        => string.Concat(slots.Select(slot => $", {(slot.Mode == SlotMode.Input ? "in" : "ref")} @{slot.Name}.Value"));

    private static string SlotKey(string name, string typeName) => name + "\0" + typeName;

    private static string Pascal(string value)
        => char.ToUpperInvariant(value[0]) + value.Substring(1);

    private static string Parameters(IEnumerable<TimelineSlot> slots)
        => string.Concat(slots.Select(static slot => $", {(slot.Mode == SlotMode.Input ? "in" : "ref")} {slot.TypeName} @{slot.Name}"));

    private static string Arguments(IEnumerable<TimelineSlot> slots)
        => string.Concat(slots.Select(static slot => $", {(slot.Mode == SlotMode.Input ? "in" : "ref")} @{slot.Name}"));

    private static string Qualified(JobTimeline timeline)
        => "global::" + (timeline.Namespace.Length == 0 ? "" : timeline.Namespace + ".") + timeline.Name;

    private static string U(uint value) => value.ToString(CultureInfo.InvariantCulture) + "u";
    private static string Bool(bool value) => value ? "true" : "false";
    private static void Line(StringBuilder writer, string value) => writer.Append(value).Append('\n');

    private static StringBuilder Header(string nameSpace)
    {
        var writer = new StringBuilder();
        if (nameSpace.Length != 0)
        {
            Line(writer, $"namespace {nameSpace}");
            Line(writer, "{");
        }
        Line(writer, "using Unity.Entities;");
        return writer;
    }

    private static void Footer(StringBuilder writer, string nameSpace)
    {
        if (nameSpace.Length != 0)
            Line(writer, "}");
    }
}
