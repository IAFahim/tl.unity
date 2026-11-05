using System.Text;
using Tl.Gen.CSharp.Model;

namespace Tl.Gen.Unity
{
    internal static class UnityKindJobEmitter
    {
        sealed class Shape
        {
            public List<UnityColumn> Columns = new List<UnityColumn>();
            public List<UnityContext> Contexts = new List<UnityContext>();
            public bool[] UsedColumns = new bool[0];
            public bool[] UsedContexts = new bool[0];
        }

        static void W(StringBuilder writer, string text)
        {
            writer.Append(text).Append('\n');
        }

        static string ComponentName(UnitySystemModel model, UnityColumn column)
        {
            return model.SystemTypeName + "_" + column.Name + "Data";
        }

        static Shape ShapeOf(UnitySystemModel model, SystemAssignment assignment, IReadOnlyList<UnityKindInfo> kinds)
        {
            var shape = new Shape();
            shape.Columns.AddRange(model.Columns);
            shape.Contexts.AddRange(model.Contexts);
            shape.UsedColumns = new bool[shape.Columns.Count];
            shape.UsedContexts = new bool[shape.Contexts.Count];
            var parameters = new List<ConsumerParam>();
            foreach (var consumer in assignment.Consumers)
                foreach (var method in consumer.Methods)
                    if (method.Kind == ConsumerMethodKind.Feeder)
                        parameters.AddRange(method.Params);
            foreach (var kind in kinds)
                parameters.AddRange(kind.Decl.ApplyParams);
            foreach (var parameter in parameters)
            {
                if (parameter.Kind != ConsumerParamKind.Column && parameter.Kind != ConsumerParamKind.ColumnReadOnly && parameter.Kind != ConsumerParamKind.Context)
                    continue;
                var at = shape.Columns.FindIndex(c => c.ElementTypeName == parameter.TypeName);
                if (at >= 0 && (parameter.Kind == ConsumerParamKind.ColumnReadOnly || !shape.Columns[at].ReadOnly))
                    shape.UsedColumns[at] = true;
                else
                {
                    var ctx = shape.Contexts.FindIndex(c => c.TypeName == parameter.TypeName);
                    if (ctx >= 0)
                        shape.UsedContexts[ctx] = true;
                }
            }
            return shape;
        }

        static int ColumnOf(Shape shape, string element, bool writable)
        {
            for (var i = 0; i < shape.Columns.Count; i++)
            {
                if (!shape.UsedColumns[i] || shape.Columns[i].ElementTypeName != element)
                    continue;
                if (!writable || !shape.Columns[i].ReadOnly)
                    return i;
            }
            return -1;
        }

        static int ContextOf(Shape shape, string typeName)
        {
            for (var i = 0; i < shape.Contexts.Count; i++)
                if (shape.UsedContexts[i] && shape.Contexts[i].TypeName == typeName)
                    return i;
            return -1;
        }

        static string CallArgs(Shape shape, IReadOnlyList<ConsumerParam> parameters, string frameVar, string rowVar, string feedPrefix)
        {
            var args = new List<string>();
            var start = 0;
            if (parameters.Count > 0 && (parameters[0].Kind == ConsumerParamKind.Frame || parameters[0].Kind == ConsumerParamKind.MarkerFrame))
            {
                args.Add("in " + frameVar);
                start = 1;
            }
            for (var p = start; p < parameters.Count; p++)
            {
                var parameter = parameters[p];
                switch (parameter.Kind)
                {
                    case ConsumerParamKind.FoldFeed:
                        args.Add("in " + feedPrefix + parameter.FoldIndex);
                        break;
                    case ConsumerParamKind.Column:
                    {
                        var at = ColumnOf(shape, parameter.TypeName, true);
                        args.Add(at >= 0 ? "ref __c" + at + "[" + rowVar + "]" : parameter.Modifier + shape.Contexts[ContextOf(shape, parameter.TypeName)].Name);
                        break;
                    }
                    case ConsumerParamKind.ColumnReadOnly:
                    {
                        var at = ColumnOf(shape, parameter.TypeName, false);
                        args.Add(at >= 0 ? "in __c" + at + "[" + rowVar + "]" : parameter.Modifier + shape.Contexts[ContextOf(shape, parameter.TypeName)].Name);
                        break;
                    }
                    case ConsumerParamKind.Context:
                        args.Add(parameter.Modifier + shape.Contexts[ContextOf(shape, parameter.TypeName)].Name);
                        break;
                }
            }
            return string.Join(", ", args);
        }

        static string ColumnArgs(Shape shape, bool typed)
        {
            var args = new StringBuilder();
            for (var i = 0; i < shape.Columns.Count; i++)
                if (shape.UsedColumns[i])
                    args.Append(typed ? ", " + shape.Columns[i].ElementTypeName + "* __c" + i : ", __c" + i);
            return args.ToString();
        }

        internal static string Emit(UnitySystemModel model, SystemAssignment assignment, int assignmentIndex, IReadOnlyList<UnityKindInfo> kinds)
        {
            var ns = model.Namespace.StartsWith("global::") ? model.Namespace.Substring(8) : model.Namespace;
            var shape = ShapeOf(model, assignment, kinds);
            var writer = new StringBuilder();
            W(writer, "// <auto-generated/>");
            W(writer, "using Unity.Burst;");
            W(writer, "using Unity.Collections;");
            W(writer, "using Unity.Entities;");
            W(writer, "using Unity.Jobs;");
            W(writer, "namespace " + ns);
            W(writer, "{");
            foreach (var column in model.Columns)
                W(writer, "public struct " + ComponentName(model, column) + " : IComponentData { public " + column.ElementTypeName + " Value; }");
            W(writer, "[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]");
            W(writer, "public unsafe partial struct " + model.SystemTypeName + "ChunkJob : IJobChunk");
            W(writer, "{");
            W(writer, "[global::Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public global::Tl.LibraryView View;");
            W(writer, "[global::Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public global::Tl.Unity.KindIndex Index;");
            W(writer, "public bool Forward;");
            W(writer, "[ReadOnly] public BufferTypeHandle<global::Tl.Unity.Playback> PlaybackHandle;");
            foreach (var column in model.Columns)
                W(writer, (column.ReadOnly ? "[ReadOnly] " : "") + "public ComponentTypeHandle<" + ComponentName(model, column) + "> " + column.Name + "Handle;");
            foreach (var context in model.Contexts)
                W(writer, "public " + context.TypeName + " " + context.Name + ";");
            foreach (var kind in kinds)
            {
                W(writer, "public BufferTypeHandle<" + kind.Entry + "> " + kind.Name + "EntryHandle;");
                W(writer, "public ComponentTypeHandle<" + kind.Active + "> " + kind.Name + "ActiveHandle;");
                W(writer, "[ReadOnly] public ComponentTypeHandle<" + kind.Enabled + "> " + kind.Name + "EnabledHandle;");
            }
            W(writer, "public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in global::Unity.Burst.Intrinsics.v128 chunkEnabledMask)");
            W(writer, "{");
            W(writer, "var __playbacks = chunk.GetBufferAccessor(ref PlaybackHandle);");
            for (var i = 0; i < shape.Columns.Count; i++)
            {
                if (!shape.UsedColumns[i])
                    continue;
                var column = shape.Columns[i];
                W(writer, "var __c" + i + " = (" + column.ElementTypeName + "*)chunk.GetRequiredComponentDataPtr" + (column.ReadOnly ? "RO" : "RW") + "(ref " + column.Name + "Handle);");
            }
            for (var k = 0; k < kinds.Count; k++)
                W(writer, "Kind_" + k + "(chunk, __playbacks" + ColumnArgs(shape, false) + ");");
            W(writer, "}");
            for (var k = 0; k < kinds.Count; k++)
                EmitKind(writer, shape, assignment, assignmentIndex, kinds[k], k);
            W(writer, "}");
            W(writer, "public static partial class TimelineJobs");
            W(writer, "{");
            var signature = new StringBuilder();
            signature.Append("public static JobHandle " + model.SystemTypeName + "(ref SystemState state, global::Tl.LibraryView view, global::Tl.Unity.KindIndex index, bool forward");
            foreach (var context in model.Contexts)
                signature.Append(", " + context.TypeName + " " + context.Name);
            signature.Append(", JobHandle dependsOn)");
            W(writer, signature.ToString());
            W(writer, "{");
            W(writer, "var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<global::Tl.Unity.Playback>();");
            foreach (var column in model.Columns)
                W(writer, column.ReadOnly ? "builder = builder.WithAll<" + ComponentName(model, column) + ">();" : "builder = builder.WithAllRW<" + ComponentName(model, column) + ">();");
            foreach (var kind in kinds)
                W(writer, "builder = builder.WithAnyRW<" + kind.Entry + ">();");
            W(writer, "var query = state.GetEntityQuery(builder);");
            W(writer, "builder.Dispose();");
            W(writer, "var job = new " + model.SystemTypeName + "ChunkJob { View = view, Index = index, Forward = forward, PlaybackHandle = state.GetBufferTypeHandle<global::Tl.Unity.Playback>(true)");
            foreach (var column in model.Columns)
                W(writer, ", " + column.Name + "Handle = state.GetComponentTypeHandle<" + ComponentName(model, column) + ">(" + (column.ReadOnly ? "true" : "false") + ")");
            foreach (var context in model.Contexts)
                W(writer, ", " + context.Name + " = " + context.Name);
            foreach (var kind in kinds)
            {
                W(writer, ", " + kind.Name + "EntryHandle = state.GetBufferTypeHandle<" + kind.Entry + ">(false)");
                W(writer, ", " + kind.Name + "ActiveHandle = state.GetComponentTypeHandle<" + kind.Active + ">(false)");
                W(writer, ", " + kind.Name + "EnabledHandle = state.GetComponentTypeHandle<" + kind.Enabled + ">(true)");
            }
            W(writer, " };");
            W(writer, "return job.ScheduleParallel(query, dependsOn);");
            W(writer, "}");
            W(writer, "}");
            W(writer, "}");
            return writer.ToString().Replace("\r\n", "\n");
        }

        static void EmitKind(StringBuilder writer, Shape shape, SystemAssignment assignment, int assignmentIndex, UnityKindInfo info, int ordinal)
        {
            var kind = info.Decl;
            var feeders = new List<(int consumer, FeederDecl feeder)>();
            for (var c = 0; c < assignment.Consumers.Count; c++)
                foreach (var feeder in assignment.Consumers[c].Feeders)
                    if (feeder.KindName == kind.Name)
                        feeders.Add((c, feeder));
            feeders.Sort((a, b) =>
            {
                var by = string.CompareOrdinal(a.feeder.ConsumerMetadata, b.feeder.ConsumerMetadata);
                return by != 0 ? by : string.CompareOrdinal(a.feeder.MethodName, b.feeder.MethodName);
            });
            var valueAt = 8;
            var value = kind.ValueTypeName;
            var applyArgs = CallArgs(shape, kind.ApplyParams, "", "__row", "");
            var tail = applyArgs.Length == 0 ? "" : ", " + applyArgs;
            W(writer, "void Kind_" + ordinal + "(in ArchetypeChunk chunk, BufferAccessor<global::Tl.Unity.Playback> __playbacks" + ColumnArgs(shape, true) + ")");
            W(writer, "{");
            W(writer, "if (!chunk.Has(ref " + info.Name + "EntryHandle)) return;");
            W(writer, "var __acc = chunk.GetBufferAccessor(ref " + info.Name + "EntryHandle);");
            W(writer, "var __ema = chunk.GetEnableableBits(ref " + info.Name + "ActiveHandle);");
            W(writer, "var __eme = chunk.GetEnableableBits(ref " + info.Name + "EnabledHandle);");
            W(writer, "var __stride = sizeof(" + info.Entry + ");");
            W(writer, "var __count = chunk.Count;");
            W(writer, "for (var __kw = 0; __kw < 2; __kw++)");
            W(writer, "{");
            W(writer, "if (__kw == 1 && __count <= 64) break;");
            W(writer, "var __kb = __kw == 0 ? __ema.ULong0 & __eme.ULong0 : __ema.ULong1 & __eme.ULong1;");
            W(writer, "while (__kb != 0)");
            W(writer, "{");
            W(writer, "var __row = (__kw << 6) + (int)global::Unity.Mathematics.math.tzcnt(__kb);");
            W(writer, "__kb &= __kb - 1;");
            W(writer, "if (__row >= __count) break;");
            W(writer, "var __buf = __acc[__row];");
            W(writer, "var __len = __buf.Length;");
            W(writer, "byte* __data = (byte*)__buf.GetUnsafePtr();");
            if (!kind.Each)
            {
                W(writer, value + " __total = default;");
                W(writer, "var __has = false;");
            }
            W(writer, "var __pulses = 0;");
            W(writer, "for (var __j = 0; __j < __len; __j++)");
            W(writer, "{");
            W(writer, "var __en = (global::Tl.KindEntry*)(__data + (long)__j * __stride);");
            W(writer, value + " __v = default;");
            W(writer, "if (__en->Timeline == 0)");
            W(writer, "{");
            W(writer, "__v = *(" + value + "*)((byte*)__en + " + valueAt + ");");
            W(writer, "__pulses++;");
            W(writer, "}");
            W(writer, "else");
            W(writer, "{");
            W(writer, "var __isPulse = (__en->Flags & global::Tl.KindEntryFlags.Pulse) != 0;");
            W(writer, "if (__isPulse) __pulses++;");
            W(writer, "var __pb = (global::Tl.Playback*)__playbacks[__row].GetUnsafeReadOnlyPtr() + __en->Slot;");
            W(writer, "if (!__isPulse && (__pb->Flags & global::Tl.PlaybackFlags.Paused) != 0) continue;");
            W(writer, "var __tl = Index.Timelines + (__en->Timeline - 1);");
            W(writer, "ushort __tick = __pb->Tick;");
            W(writer, "var __backward = Forward ? (__pb->Flags & global::Tl.PlaybackFlags.Reverse) != 0 : (__pb->Flags & global::Tl.PlaybackFlags.Reverse) == 0;");
            W(writer, "var __kflags = (__backward ? global::Tl.FrameFlags.Reverse : global::Tl.FrameFlags.None)");
            W(writer, "| ((__en->Flags & global::Tl.KindEntryFlags.Synthetic) != 0 ? global::Tl.FrameFlags.Synthetic : global::Tl.FrameFlags.None)");
            W(writer, "| ((__en->Flags & global::Tl.KindEntryFlags.Interrupted) != 0 ? global::Tl.FrameFlags.Interrupted : global::Tl.FrameFlags.None);");
            W(writer, "if (__tick == 0) __kflags |= global::Tl.FrameFlags.TimelineStart;");
            W(writer, "if (__tick == (ushort)(__tl->Duration - 1)) __kflags |= global::Tl.FrameFlags.TimelineEnd;");
            W(writer, "var __ok = false;");
            if (feeders.Count > 0)
            {
                W(writer, "switch (__en->Feeder)");
                W(writer, "{");
            }
            for (var i = 0; i < feeders.Count; i++)
            {
                var (c, feeder) = feeders[i];
                var consumer = assignment.Consumers[c];
                W(writer, "case " + i + ":");
                W(writer, "{");
                if (feeder.Trigger == FeederTrigger.Marker)
                {
                    W(writer, "byte* __mptr" + c + " = Index.MarkerOf(__en->Timeline, __en->Step);");
                    W(writer, "if (__mptr" + c + " == null) break;");
                    W(writer, consumer.MarkerTypeName + " __m" + c + " = *(" + consumer.MarkerTypeName + "*)__mptr" + c + ";");
                    W(writer, "var __mf" + c + " = new global::Tl.MarkerFrame<" + consumer.MarkerTypeName + ">(in __m" + c + ", __tick, 0, __kflags);");
                    W(writer, "__v = " + consumer.ConsumerTypeName + "." + feeder.MethodName + "(" + CallArgs(shape, feeder.Method.Params, "__mf" + c, "__row", "") + ");");
                }
                else
                {
                    W(writer, "var __def = Index.StepOf(__en->Timeline, __en->Step, __tick);");
                    W(writer, "if (__def == null || __tick < __def->WindowStart || __tick >= __def->WindowEnd) break;");
                    EmitMaterialize(writer, consumer, assignmentIndex, c);
                    if (FoldOf(consumer) != null)
                        EmitFeeds(writer, consumer, c);
                    W(writer, "__v = " + consumer.ConsumerTypeName + "." + feeder.MethodName + "(" + CallArgs(shape, feeder.Method.Params, "__f" + c, "__row", "__fd" + c + "_") + ");");
                }
                W(writer, "__ok = true;");
                W(writer, "break;");
                W(writer, "}");
            }
            if (feeders.Count > 0)
                W(writer, "}");
            W(writer, "if (!__ok) continue;");
            W(writer, "}");
            if (kind.Each)
                W(writer, kind.TypeName + ".Apply(in __v" + tail + ");");
            else
            {
                W(writer, "if (__has) " + kind.TypeName + ".Combine(ref __total, in __v);");
                W(writer, "else { __total = __v; __has = true; }");
            }
            W(writer, "}");
            if (!kind.Each)
                W(writer, "if (__has) " + kind.TypeName + ".Apply(in __total" + tail + ");");
            W(writer, "if (__pulses == __len)");
            W(writer, "{");
            W(writer, "__buf.Clear();");
            W(writer, "chunk.SetComponentEnabled(ref " + info.Name + "ActiveHandle, __row, false);");
            W(writer, "}");
            W(writer, "else if (__pulses != 0)");
            W(writer, "{");
            W(writer, "var __kept = 0;");
            W(writer, "for (var __j = 0; __j < __len; __j++)");
            W(writer, "{");
            W(writer, "var __en = (global::Tl.KindEntry*)(__data + (long)__j * __stride);");
            W(writer, "if ((__en->Flags & global::Tl.KindEntryFlags.Pulse) != 0) continue;");
            W(writer, "if (__kept != __j) global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(__data + (long)__kept * __stride, __en, __stride);");
            W(writer, "__kept++;");
            W(writer, "}");
            W(writer, "__buf.ResizeUninitialized(__kept);");
            W(writer, "}");
            W(writer, "if (__buf.Capacity > " + info.Capacity + " && __buf.Length <= " + info.Capacity + ") __buf.TrimExcess();");
            W(writer, "}");
            W(writer, "}");
            W(writer, "}");
        }

        static ConsumerMethod? FoldOf(SystemConsumer consumer)
        {
            foreach (var method in consumer.Methods)
                if (method.Kind == ConsumerMethodKind.Fold)
                    return method;
            return null;
        }

        static int LaneBytes(SystemConsumer consumer)
        {
            var fold = FoldOf(consumer);
            if (fold == null)
                return 0;
            var bytes = 0;
            foreach (var size in fold.FoldSizes)
                bytes += size;
            return bytes;
        }

        static void EmitMaterialize(StringBuilder writer, SystemConsumer consumer, int assignment, int index)
        {
            W(writer, "byte* __row" + index + " = (byte*)__def->FrameRow;");
            W(writer, "byte* __first" + index + " = __row" + index + " + sizeof(" + consumer.TrackTypeName + ");");
            W(writer, "byte* __second" + index + " = __def->FactorSpan == 0 ? null : __first" + index + " + sizeof(" + consumer.ClipTypeName + ");");
            W(writer, "float __blend" + index + " = __def->FactorSpan == 0 ? 0f : (__def->FactorSpan <= 1 ? 0.5f : (__tick - __def->FactorStart) / (float)(__def->FactorSpan - 1));");
            W(writer, "ushort __len" + index + " = (ushort)(__def->WindowEnd - __def->WindowStart);");
            W(writer, "ushort __within" + index + " = (ushort)(__tick - __def->WindowStart);");
            W(writer, "sbyte __dir" + index + " = __backward ? (sbyte)-1 : (sbyte)1;");
            W(writer, UnityKindEmitter.FramesType + ".Frame_" + assignment + "_" + index + "(__row" + index + ", __first" + index + ", __second" + index + ", __blend" + index + ", __tick, __len" + index + ", __within" + index + ", (ushort)__def->TrackIndex, (ushort)__tl->Duration, __dir" + index + ", __kflags, out var __t" + index + ", out var __cc" + index + ", out var __f" + index + ");");
        }

        static void EmitFeeds(StringBuilder writer, SystemConsumer consumer, int index)
        {
            var fold = FoldOf(consumer);
            if (fold == null || LaneBytes(consumer) == 0)
                return;
            W(writer, "byte* __lane" + index + " = (byte*)__def->FoldLane + (((uint)__tick - __def->WindowStart) * 2u + (__backward ? 1u : 0u)) * (uint)" + LaneBytes(consumer) + ";");
            for (var o = 0; o < fold.Params.Count; o++)
                W(writer, fold.Params[o].TypeName + " __fd" + index + "_" + o + " = default;");
            W(writer, "if (__def->FoldLane == null)");
            W(writer, "{");
            var foldArgs = new StringBuilder("in __f" + index);
            for (var o = 0; o < fold.Params.Count; o++)
                foldArgs.Append(", " + fold.Params[o].Modifier + "__fd" + index + "_" + o);
            W(writer, consumer.ConsumerTypeName + ".Fold(" + foldArgs + ");");
            W(writer, "}");
            W(writer, "else");
            W(writer, "{");
            var offset = 0;
            for (var o = 0; o < fold.Params.Count; o++)
            {
                W(writer, "__fd" + index + "_" + o + " = *(" + fold.Params[o].TypeName + "*)(__lane" + index + " + " + offset + ");");
                offset += fold.FoldSizes[o];
            }
            W(writer, "}");
        }
    }
}
