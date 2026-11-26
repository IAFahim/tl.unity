using System.Text;
using Tl.Gen.CSharp.Analysis;
using Tl.Gen.CSharp.Model;

namespace Tl.Gen.Unity
{
    internal static partial class UnityKindJobEmitter
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
            return column.Direct ? column.ElementTypeName : model.SystemTypeName + "_" + column.Name + "Data";
        }

        internal static IReadOnlyList<string> ColumnsOf(UnitySystemModel model)
        {
            return ColumnsOf(model, true);
        }

        internal static IReadOnlyList<string> OwnedColumnsOf(UnitySystemModel model)
        {
            return ColumnsOf(model, false);
        }

        static IReadOnlyList<string> ColumnsOf(UnitySystemModel model, bool direct)
        {
            var ns = model.Namespace.StartsWith("global::") ? model.Namespace.Substring(8) : model.Namespace;
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var column in model.Columns)
            {
                if (column.Direct)
                {
                    if (direct)
                        names.Add(column.ElementTypeName);
                    continue;
                }
                names.Add("global::" + ns + "." + ComponentName(model, column));
            }
            return new List<string>(names);
        }

        static List<ConsumerParam> KindParams(SystemAssignment assignment, KindDecl kind)
        {
            var parameters = new List<ConsumerParam>(kind.ApplyParams);
            if (kind.IdleParams is not null)
                parameters.AddRange(kind.IdleParams);
            foreach (var consumer in assignment.Consumers)
                foreach (var feeder in consumer.Feeders)
                    if (feeder.KindName == kind.Id)
                        parameters.AddRange(feeder.Method.Params);
            return parameters;
        }

        static List<int> KindColumnIndices(UnitySystemModel model, SystemAssignment assignment, KindDecl kind)
        {
            var indices = new List<int>();
            foreach (var parameter in KindParams(assignment, kind))
            {
                if (parameter.Kind != ConsumerParamKind.Column && parameter.Kind != ConsumerParamKind.ColumnReadOnly)
                    continue;
                for (var i = 0; i < model.Columns.Count; i++)
                    if (model.Columns[i].ElementTypeName == parameter.TypeName && (parameter.Kind == ConsumerParamKind.ColumnReadOnly || !model.Columns[i].ReadOnly))
                    {
                        if (!indices.Contains(i))
                            indices.Add(i);
                        break;
                    }
            }
            indices.Sort();
            return indices;
        }

        internal static IReadOnlyList<string> KindColumnsOf(UnitySystemModel model, SystemAssignment assignment, KindDecl kind)
        {
            var ns = model.Namespace.StartsWith("global::") ? model.Namespace.Substring(8) : model.Namespace;
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var i in KindColumnIndices(model, assignment, kind))
            {
                var column = model.Columns[i];
                names.Add(column.Direct ? column.ElementTypeName : "global::" + ns + "." + ComponentName(model, column));
            }
            return new List<string>(names);
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
            {
                parameters.AddRange(kind.Decl.ApplyParams);
                if (kind.Decl.IdleParams is not null)
                    parameters.AddRange(kind.Decl.IdleParams);
            }
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

        static List<int> ApplyColumnIndices(Shape shape, IReadOnlyList<ConsumerParam> parameters)
        {
            var indices = new List<int>();
            foreach (var parameter in parameters)
            {
                if (parameter.Kind != ConsumerParamKind.Column && parameter.Kind != ConsumerParamKind.ColumnReadOnly)
                    continue;
                var at = ColumnOf(shape, parameter.TypeName, parameter.Kind == ConsumerParamKind.Column);
                if (at >= 0 && !indices.Contains(at))
                    indices.Add(at);
            }
            return indices;
        }

        static List<int> ApplyContextIndices(Shape shape, IReadOnlyList<ConsumerParam> parameters)
        {
            var indices = new List<int>();
            foreach (var parameter in parameters)
            {
                if (parameter.Kind == ConsumerParamKind.Context)
                {
                    var at = ContextOf(shape, parameter.TypeName);
                    if (at >= 0 && !indices.Contains(at))
                        indices.Add(at);
                }
                else if (parameter.Kind == ConsumerParamKind.Column || parameter.Kind == ConsumerParamKind.ColumnReadOnly)
                {
                    if (ColumnOf(shape, parameter.TypeName, parameter.Kind == ConsumerParamKind.Column) < 0)
                    {
                        var at = ContextOf(shape, parameter.TypeName);
                        if (at >= 0 && !indices.Contains(at))
                            indices.Add(at);
                    }
                }
            }
            return indices;
        }

        static string ColumnArgs(Shape shape, bool typed)
        {
            var args = new StringBuilder();
            for (var i = 0; i < shape.Columns.Count; i++)
                if (shape.UsedColumns[i])
                    args.Append(typed ? ", " + shape.Columns[i].ElementTypeName + "* __c" + i : ", __c" + i);
            return args.ToString();
        }

        static bool BindsContext(Shape shape, IReadOnlyList<ConsumerParam> ps)
        {
            foreach (var p in ps)
                if (p.Kind == ConsumerParamKind.Context || ((p.Kind == ConsumerParamKind.Column || p.Kind == ConsumerParamKind.ColumnReadOnly) && ColumnOf(shape, p.TypeName, p.Kind == ConsumerParamKind.Column) < 0 && ContextOf(shape, p.TypeName) >= 0))
                    return true;
            return false;
        }

        internal static bool IsTlFrame(ConsumerMethod method)
            => method.Params.Count > 0
                && method.Params[0].Kind == ConsumerParamKind.Frame
                && method.Params[0].TypeName.StartsWith("global::Tl.TlFrame<", StringComparison.Ordinal);

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
                if (!column.Direct)
                    W(writer, "public struct " + ComponentName(model, column) + " : IComponentData { public " + column.ElementTypeName + " Value; }");
            W(writer, "[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]");
            W(writer, "public unsafe partial struct " + model.SystemTypeName + "ChunkJob : IJobChunk");
            W(writer, "{");
            W(writer, "[global::Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public global::Tl.LibraryView View;");
            W(writer, "[global::Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public global::Tl.Unity.KindIndex Index;");
            W(writer, "public bool Forward;");
            W(writer, "[ReadOnly] public BufferTypeHandle<global::Tl.Unity.Playback> PlaybackHandle;");
            W(writer, "[ReadOnly] public ComponentTypeHandle<global::Tl.Unity.TimelineClock> ClockHandle;");
            W(writer, "[ReadOnly] public ComponentTypeHandle<global::Tl.Unity.TimelinePlaying> PlayingHandle;");
            W(writer, "public global::Unity.Burst.Intrinsics.v128 Skip;");
            foreach (var column in model.Columns)
                W(writer, (column.ReadOnly ? "[ReadOnly] " : "") + "public ComponentTypeHandle<" + ComponentName(model, column) + "> " + column.Name + "Handle;");
            foreach (var context in model.Contexts)
                W(writer, "public " + context.TypeName + " " + context.Name + ";");
            var eventConsumers = EventConsumers(assignment);
            var events = eventConsumers.Count == 0 ? new List<UnityContext>() : new List<UnityContext>(model.Events);
            foreach (var evented in events)
                W(writer, "public BufferTypeHandle<global::Tl.Unity.TimelineEvent<" + evented.TypeName + ">> " + evented.Name + "EventHandle;");
            for (var k = 0; k < kinds.Count; k++)
            {
                var kind = kinds[k];
                W(writer, "public BufferTypeHandle<" + kind.Entry + "> " + kind.Name + "EntryHandle;");
                W(writer, "public ComponentTypeHandle<" + kind.Active + "> " + kind.Name + "ActiveHandle;");
                W(writer, "[ReadOnly] public ComponentTypeHandle<" + kind.Enabled + "> " + kind.Name + "EnabledHandle;");
                W(writer, "public int KindSlot_" + k + ";");
            }
            var values = new List<int>();
            for (var k = 0; k < kinds.Count && values.Count < 10; k++)
                if (kinds[k].Decl.Precombined)
                    values.Add(k);
            var runs = new List<List<int>>();
            var curRun = new List<int>();
            for (var k = 0; k < kinds.Count; k++)
            {
                var kind = kinds[k].Decl;
                var ctx = BindsContext(shape, kind.ApplyParams);
                if (!ctx)
                    foreach (var f in FeedersOf(assignment, kind))
                        if (BindsContext(shape, f.feeder.Method.Params)) { ctx = true; break; }
                if (values.Contains(k) && kind.Group.Length == 0 && kind.IdleParams is null && !ctx)
                    curRun.Add(k);
                else
                {
                    if (curRun.Count >= 2) runs.Add(curRun);
                    curRun = new List<int>();
                }
            }
            if (curRun.Count >= 2) runs.Add(curRun);
            var fusedKinds = new HashSet<int>();
            foreach (var run in runs)
                foreach (var fk in run)
                    fusedKinds.Add(fk);
            for (var k = 0; k < kinds.Count; k++)
                if (fusedKinds.Contains(k))
                    W(writer, "public bool NoEnt_" + k + ";");
            var pulseFast = new List<int>();
            for (var k = 0; k < kinds.Count; k++)
                if (kinds[k].Decl.PrecombinedPulses && kinds[k].Decl.IdleParams is null)
                    pulseFast.Add(k);
            W(writer, "public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in global::Unity.Burst.Intrinsics.v128 chunkEnabledMask)");
            W(writer, "{");
            W(writer, "var __count = chunk.Count;");
            var hasNonSpanless = false;
            for (var k = 0; k < kinds.Count; k++)
                if (kinds[k].Decl.Accepts != 1) { hasNonSpanless = true; break; }
            if (hasNonSpanless)
                W(writer, "var __epl = chunk.GetEnableableBits(ref PlayingHandle);");
            var live = new StringBuilder();
            for (var k = 0; k < kinds.Count; k++)
            {
                var name = kinds[k].Name;
                W(writer, "var __l" + k + " = false;");
                if (fusedKinds.Contains(k))
                    W(writer, "var __has" + k + " = chunk.Has(ref " + name + "EntryHandle);");
                if (kinds[k].Decl.Accepts == 1 || fusedKinds.Contains(k))
                {
                    W(writer, "var __a" + k + " = default(global::Unity.Burst.Intrinsics.v128);");
                    W(writer, "var __e" + k + " = default(global::Unity.Burst.Intrinsics.v128);");
                }
                if (pulseFast.Contains(k))
                {
                    W(writer, "var __kid" + k + " = KindSlot_" + k + " != 0 ? KindSlot_" + k + " - 2 : Index.KindOf(" + KindId(kinds[k].Key) + ");");
                    W(writer, "if (__kid" + k + " >= 0 && Index.NeedsBufferScan(__kid" + k + ") && " + (fusedKinds.Contains(k) ? "__has" + k : "chunk.Has(ref " + name + "EntryHandle)") + ")");
                    W(writer, "{");
                    W(writer, "__a" + k + " = chunk.GetEnableableBits(ref " + name + "ActiveHandle);");
                    W(writer, "__e" + k + " = chunk.GetEnableableBits(ref " + name + "EnabledHandle);");
                    W(writer, "var __m0 = __count >= 64 ? ~0ul : (1ul << __count) - 1;");
                    W(writer, "var __m1 = __count <= 64 ? 0ul : (__count >= 128 ? ~0ul : (1ul << (__count - 64)) - 1);");
                    W(writer, "if (((__a" + k + ".ULong0 & ~__e" + k + ".ULong0 & __m0) | (__a" + k + ".ULong1 & ~__e" + k + ".ULong1 & __m1)) != 0) global::System.Threading.Interlocked.Increment(ref Index.HeldSeen[__kid" + k + "]);");
                    W(writer, "__l" + k + " = ((__a" + k + ".ULong0 & __e" + k + ".ULong0 & __m0) | (__a" + k + ".ULong1 & __e" + k + ".ULong1 & __m1)) != 0;");
                    W(writer, "}");
                }
                else
                {
                    W(writer, "if (" + (fusedKinds.Contains(k) ? "__has" + k : "chunk.Has(ref " + name + "EntryHandle)") + ")");
                    W(writer, "{");
                    if (kinds[k].Decl.IdleParams is not null)
                        W(writer, "__l" + k + " = true;");
                    else if (kinds[k].Decl.Accepts == 1)
                    {
                        W(writer, "__a" + k + " = chunk.GetEnableableBits(ref " + name + "ActiveHandle);");
                        W(writer, "__e" + k + " = chunk.GetEnableableBits(ref " + name + "EnabledHandle);");
                        W(writer, "__l" + k + " = ((__a" + k + ".ULong0 & __e" + k + ".ULong0) | (__a" + k + ".ULong1 & __e" + k + ".ULong1)) != 0;");
                    }
                    else
                    {
                        W(writer, (fusedKinds.Contains(k) ? "" : "var ") + "__a" + k + " = chunk.GetEnableableBits(ref " + name + "ActiveHandle);");
                        W(writer, (fusedKinds.Contains(k) ? "" : "var ") + "__e" + k + " = chunk.GetEnableableBits(ref " + name + "EnabledHandle);");
                        W(writer, "__l" + k + " = (((__a" + k + ".ULong0 | __epl.ULong0) & __e" + k + ".ULong0) | ((__a" + k + ".ULong1 | __epl.ULong1) & __e" + k + ".ULong1)) != 0;");
                    }
                    W(writer, "}");
                }
                live.Append(k == 0 ? "" : " && ").Append("!__l" + k);
            }
            if (events.Count != 0)
            {
                var any = new StringBuilder();
                for (var e = 0; e < events.Count; e++)
                    any.Append(e == 0 ? "" : " || ").Append("chunk.Has(ref " + events[e].Name + "EventHandle)");
                W(writer, "var __x = " + any + ";");
                live.Append(kinds.Count == 0 ? "!__x" : " && !__x");
            }
            if (live.Length != 0)
                W(writer, "if (" + live + ") return;");
            var pbUsers = new List<string>();
            if (events.Count != 0) pbUsers.Add("__x");
            for (var k = 0; k < kinds.Count; k++)
                if (kinds[k].Decl.Accepts != 1)
                    pbUsers.Add("__l" + k);
            if (pbUsers.Count != 0)
                W(writer, "var __playbacks = (" + string.Join(" || ", pbUsers) + ") ? chunk.GetBufferAccessor(ref PlaybackHandle) : default;");
            if (events.Count != 0)
                W(writer, "if (__x) Events(chunk, __playbacks);");
            var colKinds = new List<int>[shape.Columns.Count];
            for (var i = 0; i < shape.Columns.Count; i++) colKinds[i] = new List<int>();
            for (var k = 0; k < kinds.Count; k++)
                foreach (var ci in KindColumnIndices(model, assignment, kinds[k].Decl))
                    if (!colKinds[ci].Contains(k))
                        colKinds[ci].Add(k);
            for (var i = 0; i < shape.Columns.Count; i++)
            {
                if (!shape.UsedColumns[i])
                    continue;
                var column = shape.Columns[i];
                var needTerms = new List<string>();
                foreach (var k in colKinds[i]) needTerms.Add("__l" + k);
                var needExpr = needTerms.Count == 0 ? "false" : string.Join(" || ", needTerms);
                W(writer, "var __c" + i + " = (" + needExpr + ") ? (" + column.ElementTypeName + "*)chunk.GetComponentDataPtr" + (column.ReadOnly ? "RO" : "RW") + "(ref " + column.Name + "Handle) : null;");
            }
            foreach (var k in pulseFast)
            {
                var reqCols = new List<string>();
                foreach (var i in KindColumnIndices(model, assignment, kinds[k].Decl))
                    if (shape.UsedColumns[i])
                        reqCols.Add("__c" + i + " == null");
                if (reqCols.Count != 0)
                    W(writer, "if (__l" + k + " && __kid" + k + " >= 0 && (" + string.Join(" || ", reqCols) + ")) global::System.Threading.Interlocked.Increment(ref Index.HeldSeen[__kid" + k + "]);");
            }
            var rAt = 0;
            for (var k = 0; k < kinds.Count; k++)
            {
                var cur = rAt < runs.Count ? runs[rAt] : null;
                if (cur != null && k == cur[0])
                {
                    var fName = runs.Count == 1 ? "Fused" : "Fused_" + rAt;
                    var allHas = new List<string>();
                    var anyLive = new List<string>();
                    var extraArgs = new List<string> { "__epl" };
                    var allCols = new SortedSet<int>();
                    foreach (var fk in cur)
                    {
                        allHas.Add("__has" + fk);
                        anyLive.Add("__l" + fk);
                        extraArgs.Add("__a" + fk);
                        extraArgs.Add("__e" + fk);
                        foreach (var ci in KindColumnIndices(model, assignment, kinds[fk].Decl))
                            if (shape.UsedColumns[ci])
                                allCols.Add(ci);
                    }
                    var cond = "(" + string.Join(" || ", anyLive) + ") && " + string.Join(" && ", allHas);
                    foreach (var ci in allCols)
                        cond += " && __c" + ci + " != null";
                    W(writer, "if (!(" + cond + " && " + fName + "(chunk, __playbacks, " + string.Join(", ", extraArgs) + ColumnArgs(shape, false) + ")))");
                    W(writer, "{");
                }
                var guard = new StringBuilder("__l" + k);
                foreach (var i in KindColumnIndices(model, assignment, kinds[k].Decl))
                    if (shape.UsedColumns[i])
                        guard.Append(" && __c" + i + " != null");
                var kindArgs = kinds[k].Decl.Accepts == 1 ? ColumnArgs(shape, false) : ", __playbacks" + ColumnArgs(shape, false);
                if (kindArgs.StartsWith(", ", StringComparison.Ordinal)) kindArgs = kindArgs.Substring(2);
                W(writer, "if (" + guard + ") Kind_" + k + "(chunk" + (kindArgs.Length == 0 ? "" : ", " + kindArgs) + ");");
                if (cur != null && k == cur[cur.Count - 1])
                {
                    W(writer, "}");
                    rAt++;
                }
            }
            W(writer, "}");
            for (var k = 0; k < kinds.Count; k++)
                EmitKind(writer, shape, assignment, assignmentIndex, kinds[k], k, values.Contains(k));
            for (var k = 0; k < kinds.Count; k++)
                if (values.Contains(k))
                    EmitSpanValues(writer, shape, assignment, assignmentIndex, kinds[k], k);
                else if (kinds[k].Decl.PrecombinedPulses)
                    EmitPulseValues(writer, shape, assignment, assignmentIndex, kinds[k], k);
            if (values.Count > 0)
            {
                W(writer, "public struct KindFill : global::Tl.IKindFill, global::Tl.Unity.IKindDenseFill");
                W(writer, "{");
                W(writer, "[global::Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public global::Tl.Unity.KindIndex Index;");
                W(writer, "public void Add(ref global::Tl.KindBuild __b)");
                W(writer, "{");
                foreach (var k in values)
                {
                    var info = kinds[k];
                    W(writer, "var __kid" + k + " = Index.KindOf(" + KindId(info.Key) + ");");
                    W(writer, "if (__kid" + k + " >= 0) { var __s" + k + " = default(KindSpans_" + k + "); __b.Add<" + info.Decl.ValueTypeName + ", KindSpans_" + k + ">(__kid" + k + ", 0, Index.Routes, ref __s" + k + "); }");
                }
                W(writer, "}");
                W(writer, "public void Add(ref global::Tl.Unity.KindDenseBuild __b)");
                W(writer, "{");
                foreach (var k in values)
                {
                    var info = kinds[k];
                    W(writer, "var __kid" + k + " = Index.KindOf(" + KindId(info.Key) + ");");
                    W(writer, "if (__kid" + k + " >= 0) { var __s" + k + " = default(KindSpans_" + k + "); " + info.Decl.ValueTypeName + " __id" + k + "; " + Identity(info.Decl, "__id" + k) + "__b.Add<" + info.Decl.ValueTypeName + ", KindSpans_" + k + ">(__kid" + k + ", 0, Index.Routes, ref __s" + k + ", __id" + k + "); }");
                }
                W(writer, "}");
                W(writer, "}");
            }
            for (var r = 0; r < runs.Count; r++)
                EmitFused(writer, shape, assignment, assignmentIndex, kinds, runs[r], runs.Count == 1 ? "Fused" : "Fused_" + r);
            if (events.Count != 0)
                EmitEvents(writer, events, eventConsumers, assignmentIndex);
            W(writer, "[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            W(writer, "global::Unity.Burst.Intrinsics.v128 __Keep(global::Unity.Burst.Intrinsics.v128 __bits) => new global::Unity.Burst.Intrinsics.v128(__bits.ULong0 & ~Skip.ULong0, __bits.ULong1 & ~Skip.ULong1);");
            W(writer, "}");
            foreach (var k in pulseFast)
            {
                EmitPulseJob(writer, model, shape, kinds[k], k);
                EmitFinishJob(writer, model, shape, kinds[k], k);
            }
            EmitMixed(writer, model);
            EmitEntry(writer, model, shape, events, kinds, values, fusedKinds, pulseFast);
            W(writer, "}");
            return System.Text.RegularExpressions.Regex.Replace(writer.ToString(), @"\bchunk\.GetEnableableBits\(ref (\w+)\)", "__Keep(chunk.GetEnableableBits(ref $1))").Replace("\r\n", "\n");
        }

        static void EmitPulseJob(StringBuilder writer, UnitySystemModel model, Shape shape, UnityKindInfo info, int ordinal)
        {
            var kind = info.Decl;
            var value = kind.ValueTypeName;
            var applyCols = ApplyColumnIndices(shape, kind.ApplyParams);
            var applyCtxs = ApplyContextIndices(shape, kind.ApplyParams);
            var applyArgs = CallArgs(shape, kind.ApplyParams, "", "__row", "");
            var tail = applyArgs.Length == 0 ? "" : ", " + applyArgs;
            W(writer, "[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]");
            W(writer, "public unsafe struct " + model.SystemTypeName + "PulseJob_" + ordinal + " : IJobParallelFor");
            W(writer, "{");
            W(writer, "[global::Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public global::Tl.Unity.KindIndex Index;");
            W(writer, "public int Kind;");
            W(writer, "public int OrderVersion;");
            W(writer, "[ReadOnly] [global::Unity.Collections.LowLevel.Unsafe.NativeDisableContainerSafetyRestriction] public ComponentTypeHandle<" + info.Enabled + "> EnabledHandle;");
            foreach (var ci in applyCols)
            {
                var col = shape.Columns[ci];
                W(writer, (col.ReadOnly ? "[ReadOnly] " : "") + "[global::Unity.Collections.LowLevel.Unsafe.NativeDisableContainerSafetyRestriction] public ComponentTypeHandle<" + ComponentName(model, col) + "> " + col.Name + "Handle;");
            }
            foreach (var xi in applyCtxs)
            {
                var ctx = shape.Contexts[xi];
                W(writer, "public " + ctx.TypeName + " " + ctx.Name + ";");
            }
            W(writer, "public void Execute(int threadIndex)");
            W(writer, "{");
            W(writer, "if (Kind < 0 || Index.TickFastPulses == null || (*Index.TickFastPulses >> Kind & 1) == 0) return;");
            W(writer, "if (Index.TickOrderVersion == null || *Index.TickOrderVersion != OrderVersion) return;");
            W(writer, "var lane = Index.PulseLanes + (Kind * Index.ThreadCount + threadIndex);");
            W(writer, "var n = lane->Count;");
            W(writer, "if (n == 0) return;");
            W(writer, "var items = lane->Items;");
            W(writer, "var pv = Index.PulseValuesTable + Kind;");
            W(writer, "var starts = pv->Start;");
            W(writer, "var vals = (" + value + "*)pv->Data;");
            W(writer, "var lastChunk = default(ArchetypeChunk);");
            W(writer, "var lastValid = false;");
            W(writer, "var lastEn = default(global::Unity.Burst.Intrinsics.v128);");
            foreach (var ci in applyCols)
                W(writer, shape.Columns[ci].ElementTypeName + "* __c" + ci + " = null;");
            W(writer, "var kept = 0;");
            W(writer, "var i = 0;");
            W(writer, "while (i < n)");
            W(writer, "{");
            W(writer, "var e = items[i].Entity;");
            W(writer, "var end = i + 1;");
            W(writer, "while (end < n && items[end].Entity == e) end++;");
            W(writer, "var chunk = items[i].Chunk;");
            W(writer, "var __row = items[i].Row;");
            W(writer, "if (!lastValid || !chunk.Equals(lastChunk))");
            W(writer, "{");
            W(writer, "lastChunk = chunk;");
            W(writer, "lastValid = true;");
            W(writer, "lastEn = lastChunk.GetEnableableBits(ref EnabledHandle);");
            foreach (var ci in applyCols)
            {
                var col = shape.Columns[ci];
                W(writer, "__c" + ci + " = (" + col.ElementTypeName + "*)lastChunk.GetComponentDataPtr" + (col.ReadOnly ? "RO" : "RW") + "(ref " + col.Name + "Handle);");
            }
            W(writer, "}");
            W(writer, "var enBit = (((__row < 64 ? lastEn.ULong0 : lastEn.ULong1) >> (__row & 63)) & 1) != 0;");
            W(writer, "if (!enBit)");
            W(writer, "{");
            W(writer, "for (var j = i; j < end; j++) items[kept++] = items[j];");
            W(writer, "i = end;");
            W(writer, "continue;");
            W(writer, "}");
            if (applyCols.Count != 0)
            {
                var checks = new List<string>();
                foreach (var ci in applyCols) checks.Add("__c" + ci + " != null");
                W(writer, "if (" + string.Join(" && ", checks) + ")");
                W(writer, "{");
            }
            if (kind.Each)
            {
                W(writer, "for (var j = i; j < end; j++)");
                W(writer, "{");
                W(writer, "var c = items[j].Cell;");
                W(writer, "for (var p = starts[c]; p < starts[c + 1]; p++) " + kind.TypeName + ".Apply(in vals[p]" + tail + ");");
                W(writer, "}");
            }
            else
            {
                W(writer, value + " __total = default;");
                W(writer, "var __has = false;");
                W(writer, "for (var j = i; j < end; j++)");
                W(writer, "{");
                W(writer, "var c = items[j].Cell;");
                W(writer, "for (var p = starts[c]; p < starts[c + 1]; p++)");
                W(writer, "{");
                W(writer, "if (__has) " + kind.TypeName + ".Combine(ref __total, in vals[p]);");
                W(writer, "else { __total = vals[p]; __has = true; }");
                W(writer, "}");
                W(writer, "}");
                W(writer, "if (__has) " + kind.TypeName + ".Apply(in __total" + tail + ");");
            }
            if (applyCols.Count != 0)
            {
                W(writer, "}");
                W(writer, "else");
                W(writer, "{");
                W(writer, "for (var j = i; j < end; j++) items[kept++] = items[j];");
                W(writer, "}");
            }
            W(writer, "i = end;");
            W(writer, "}");
            W(writer, "lane->Count = kept;");
            W(writer, "if (kept > 0) global::System.Threading.Interlocked.Increment(ref Index.SpillCount[Kind]);");
            W(writer, "}");
            W(writer, "}");
        }

        static void EmitFinishJob(StringBuilder writer, UnitySystemModel model, Shape shape, UnityKindInfo info, int ordinal)
        {
            var kind = info.Decl;
            var value = kind.ValueTypeName;
            var applyCols = ApplyColumnIndices(shape, kind.ApplyParams);
            var applyCtxs = ApplyContextIndices(shape, kind.ApplyParams);
            var applyArgs = CallArgs(shape, kind.ApplyParams, "", "__row", "");
            var tail = applyArgs.Length == 0 ? "" : ", " + applyArgs;
            W(writer, "[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]");
            W(writer, "public unsafe struct " + model.SystemTypeName + "FinishJob_" + ordinal + " : IJob");
            W(writer, "{");
            W(writer, "[global::Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public global::Tl.Unity.KindIndex Index;");
            W(writer, "public int Kind;");
            W(writer, "public int OrderVersion;");
            W(writer, "[ReadOnly] public EntityStorageInfoLookup Storage;");
            W(writer, "public BufferTypeHandle<" + info.Entry + "> EntryHandle;");
            W(writer, "public ComponentTypeHandle<" + info.Active + "> ActiveHandle;");
            W(writer, "[ReadOnly] public ComponentTypeHandle<" + info.Enabled + "> EnabledHandle;");
            foreach (var ci in applyCols)
            {
                var col = shape.Columns[ci];
                W(writer, (col.ReadOnly ? "[ReadOnly] " : "") + "public ComponentTypeHandle<" + ComponentName(model, col) + "> " + col.Name + "Handle;");
            }
            foreach (var xi in applyCtxs)
            {
                var ctx = shape.Contexts[xi];
                W(writer, "public " + ctx.TypeName + " " + ctx.Name + ";");
            }
            W(writer, "public void Execute()");
            W(writer, "{");
            W(writer, "if (Kind < 0 || Index.HeldBufferKinds == null) return;");
            W(writer, "var fast = Index.TickFastPulses != null && (*Index.TickFastPulses >> Kind & 1) != 0;");
            W(writer, "var direct = Index.TickOrderVersion != null && *Index.TickOrderVersion == OrderVersion;");
            W(writer, "var spilled = false;");
            W(writer, "if (fast && !direct)");
            W(writer, "{");
            W(writer, "var pv = Index.PulseValuesTable + Kind;");
            W(writer, "var starts = pv->Start;");
            W(writer, "var vbytes = pv->Stride;");
            W(writer, "var stride = sizeof(" + info.Entry + ");");
            W(writer, "var vals = (" + value + "*)pv->Data;");
            W(writer, "var lastChunk = default(ArchetypeChunk);");
            W(writer, "var lastValid = false;");
            W(writer, "var lastEn = default(global::Unity.Burst.Intrinsics.v128);");
            foreach (var ci in applyCols)
                W(writer, shape.Columns[ci].ElementTypeName + "* __c" + ci + " = null;");
            W(writer, "for (var th = 0; th < Index.ThreadCount; th++)");
            W(writer, "{");
            W(writer, "var lane = Index.PulseLanes + (Kind * Index.ThreadCount + th);");
            W(writer, "var n = lane->Count;");
            W(writer, "if (n == 0) continue;");
            W(writer, "var items = lane->Items;");
            W(writer, "var i = 0;");
            W(writer, "while (i < n)");
            W(writer, "{");
            W(writer, "var e = items[i].Entity;");
            W(writer, "var end = i + 1;");
            W(writer, "while (end < n && items[end].Entity == e) end++;");
            W(writer, "if (!Storage.Exists(e)) { i = end; continue; }");
            W(writer, "var info = Storage[e];");
            W(writer, "var chunk = info.Chunk;");
            W(writer, "var __row = info.IndexInChunk;");
            W(writer, "if (!lastValid || !chunk.Equals(lastChunk))");
            W(writer, "{");
            W(writer, "lastChunk = chunk;");
            W(writer, "lastValid = true;");
            W(writer, "lastEn = lastChunk.GetEnableableBits(ref EnabledHandle);");
            foreach (var ci in applyCols)
            {
                var col = shape.Columns[ci];
                W(writer, "__c" + ci + " = (" + col.ElementTypeName + "*)lastChunk.GetComponentDataPtr" + (col.ReadOnly ? "RO" : "RW") + "(ref " + col.Name + "Handle);");
            }
            W(writer, "}");
            var checks2 = new List<string> { "(((__row < 64 ? lastEn.ULong0 : lastEn.ULong1) >> (__row & 63)) & 1) != 0" };
            foreach (var ci in applyCols) checks2.Add("__c" + ci + " != null");
            W(writer, "if (" + string.Join(" && ", checks2) + ")");
            W(writer, "{");
            if (kind.Each)
            {
                W(writer, "for (var j = i; j < end; j++)");
                W(writer, "{");
                W(writer, "var c = items[j].Cell;");
                W(writer, "for (var p = starts[c]; p < starts[c + 1]; p++) " + kind.TypeName + ".Apply(in vals[p]" + tail + ");");
                W(writer, "}");
            }
            else
            {
                W(writer, value + " __total = default;");
                W(writer, "var __has = false;");
                W(writer, "for (var j = i; j < end; j++)");
                W(writer, "{");
                W(writer, "var c = items[j].Cell;");
                W(writer, "for (var p = starts[c]; p < starts[c + 1]; p++)");
                W(writer, "{");
                W(writer, "if (__has) " + kind.TypeName + ".Combine(ref __total, in vals[p]);");
                W(writer, "else { __total = vals[p]; __has = true; }");
                W(writer, "}");
                W(writer, "}");
                W(writer, "if (__has) " + kind.TypeName + ".Apply(in __total" + tail + ");");
            }
            W(writer, "}");
            W(writer, "else if (chunk.Has(ref EntryHandle))");
            W(writer, "{");
            W(writer, "var added = 0;");
            W(writer, "for (var j = i; j < end; j++) { var c = items[j].Cell; var cnt = starts[c + 1] - starts[c]; if (cnt > 0) added += cnt; }");
            W(writer, "if (added > 0)");
            W(writer, "{");
            W(writer, "var acc = chunk.GetBufferAccessor(ref EntryHandle);");
            W(writer, "var buf = acc[__row];");
            W(writer, "var len = buf.Length;");
            W(writer, "buf.ResizeUninitialized(len + added);");
            W(writer, "var dstBase = (byte*)buf.GetUnsafePtr() + (long)len * stride;");
            W(writer, "var at = 0;");
            W(writer, "for (var j = i; j < end; j++)");
            W(writer, "{");
            W(writer, "var c = items[j].Cell;");
            W(writer, "var s = starts[c];");
            W(writer, "var cnt = starts[c + 1] - s;");
            W(writer, "if (cnt <= 0) continue;");
            W(writer, "var src = pv->Data + (long)s * vbytes;");
            W(writer, "for (var p = 0; p < cnt; p++)");
            W(writer, "{");
            W(writer, "var dst = dstBase + (long)at * stride;");
            W(writer, "global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(dst, stride);");
            W(writer, "((global::Tl.KindEntry*)dst)->Flags = global::Tl.KindEntryFlags.Pulse;");
            W(writer, "global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(dst + 8, src + (long)p * vbytes, vbytes);");
            W(writer, "at++;");
            W(writer, "}");
            W(writer, "}");
            W(writer, "if (len == 0) chunk.SetComponentEnabled(ref ActiveHandle, __row, true);");
            W(writer, "spilled = true;");
            W(writer, "}");
            W(writer, "}");
            W(writer, "i = end;");
            W(writer, "}");
            W(writer, "lane->Count = 0;");
            W(writer, "}");
            W(writer, "Index.SpillCount[Kind] = 0;");
            W(writer, "}");
            W(writer, "else if (Index.SpillCount[Kind] > 0)");
            W(writer, "{");
            W(writer, "var pv = Index.PulseValuesTable + Kind;");
            W(writer, "var starts = pv->Start;");
            W(writer, "var vbytes = pv->Stride;");
            W(writer, "var stride = sizeof(" + info.Entry + ");");
            W(writer, "for (var th = 0; th < Index.ThreadCount; th++)");
            W(writer, "{");
            W(writer, "var lane = Index.PulseLanes + (Kind * Index.ThreadCount + th);");
            W(writer, "var n = lane->Count;");
            W(writer, "if (n == 0) continue;");
            W(writer, "var items = lane->Items;");
            W(writer, "for (var i = 0; i < n; i++)");
            W(writer, "{");
            W(writer, "var e = items[i].Entity;");
            W(writer, "if (!Storage.Exists(e)) continue;");
            W(writer, "var info = Storage[e];");
            W(writer, "if (!info.Chunk.Has(ref EntryHandle)) continue;");
            W(writer, "var c = items[i].Cell;");
            W(writer, "var s = starts[c];");
            W(writer, "var cnt = starts[c + 1] - s;");
            W(writer, "if (cnt <= 0) continue;");
            W(writer, "var acc = info.Chunk.GetBufferAccessor(ref EntryHandle);");
            W(writer, "var buf = acc[info.IndexInChunk];");
            W(writer, "var len = buf.Length;");
            W(writer, "buf.ResizeUninitialized(len + cnt);");
            W(writer, "var dstBase = (byte*)buf.GetUnsafePtr() + (long)len * stride;");
            W(writer, "var src = pv->Data + (long)s * vbytes;");
            W(writer, "for (var p = 0; p < cnt; p++)");
            W(writer, "{");
            W(writer, "var dst = dstBase + (long)p * stride;");
            W(writer, "global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(dst, stride);");
            W(writer, "((global::Tl.KindEntry*)dst)->Flags = global::Tl.KindEntryFlags.Pulse;");
            W(writer, "global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(dst + 8, src + (long)p * vbytes, vbytes);");
            W(writer, "}");
            W(writer, "if (len == 0) info.Chunk.SetComponentEnabled(ref ActiveHandle, info.IndexInChunk, true);");
            W(writer, "spilled = true;");
            W(writer, "}");
            W(writer, "lane->Count = 0;");
            W(writer, "}");
            W(writer, "Index.SpillCount[Kind] = 0;");
            W(writer, "}");
            W(writer, "if (spilled) Index.HeldBufferKinds[Kind] = 1;");
            W(writer, "else if (Index.NeedsBufferScan(Kind)) Index.HeldBufferKinds[Kind] = (byte)(Index.HeldSeen[Kind] != 0 ? 1 : 0);");
            W(writer, "Index.HeldSeen[Kind] = 0;");
            W(writer, "Index.SlowCount[Kind] = 0;");
            W(writer, "}");
            W(writer, "}");
        }

        static void EmitMixed(StringBuilder writer, UnitySystemModel model)
        {
            W(writer, "[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]");
            W(writer, "public unsafe struct " + model.SystemTypeName + "MixedJob : global::Unity.Jobs.IJobParallelForDefer");
            W(writer, "{");
            W(writer, "[global::Unity.Collections.LowLevel.Unsafe.NativeDisableContainerSafetyRestriction] public " + model.SystemTypeName + "ChunkJob Job;");
            W(writer, "[global::Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction] public global::Tl.Unity.FusedMixed* Items;");
            W(writer, "public void Execute(int index)");
            W(writer, "{");
            W(writer, "var job = Job;");
            W(writer, "job.Skip = Items[index].Dense;");
            W(writer, "job.Execute(Items[index].Chunk, index, false, default);");
            W(writer, "}");
            W(writer, "}");
        }

        static void EmitEntry(StringBuilder writer, UnitySystemModel model, Shape shape, List<UnityContext> events, IReadOnlyList<UnityKindInfo> kinds, List<int> values, HashSet<int> fusedKinds, List<int> pulseFast)
        {
            var handles = new List<(string field, string type, string getter, bool readOnly)>();
            handles.Add(("PlaybackHandle", "BufferTypeHandle<global::Tl.Unity.Playback>", "GetBufferTypeHandle<global::Tl.Unity.Playback>", true));
            handles.Add(("ClockHandle", "ComponentTypeHandle<global::Tl.Unity.TimelineClock>", "GetComponentTypeHandle<global::Tl.Unity.TimelineClock>", true));
            handles.Add(("PlayingHandle", "ComponentTypeHandle<global::Tl.Unity.TimelinePlaying>", "GetComponentTypeHandle<global::Tl.Unity.TimelinePlaying>", true));
            foreach (var column in model.Columns)
                handles.Add((column.Name + "Handle", "ComponentTypeHandle<" + ComponentName(model, column) + ">", "GetComponentTypeHandle<" + ComponentName(model, column) + ">", column.ReadOnly));
            foreach (var evented in events)
                handles.Add((evented.Name + "EventHandle", "BufferTypeHandle<global::Tl.Unity.TimelineEvent<" + evented.TypeName + ">>", "GetBufferTypeHandle<global::Tl.Unity.TimelineEvent<" + evented.TypeName + ">>", false));
            foreach (var kind in kinds)
            {
                handles.Add((kind.Name + "EntryHandle", "BufferTypeHandle<" + kind.Entry + ">", "GetBufferTypeHandle<" + kind.Entry + ">", false));
                handles.Add((kind.Name + "ActiveHandle", "ComponentTypeHandle<" + kind.Active + ">", "GetComponentTypeHandle<" + kind.Active + ">", false));
                handles.Add((kind.Name + "EnabledHandle", "ComponentTypeHandle<" + kind.Enabled + ">", "GetComponentTypeHandle<" + kind.Enabled + ">", true));
            }
            var name = model.SystemTypeName;
            W(writer, (model.Namespace == TlGenUnityGenerator.AutoNamespace ? "internal" : "public") + " static partial class TimelineJobs");
            W(writer, "{");
            W(writer, "public struct " + name);
            W(writer, "{");
            W(writer, "EntityQuery _query;");
            if (pulseFast.Count > 0)
                W(writer, "EntityStorageInfoLookup _Storage;");
            foreach (var handle in handles)
                W(writer, handle.type + " _" + handle.field + ";");
            W(writer, "public static " + name + " Create(ref SystemState state)");
            W(writer, "{");
            W(writer, "var jobs = default(" + name + ");");
            if (pulseFast.Count > 0)
                W(writer, "jobs._Storage = state.GetEntityStorageInfoLookup();");
            if (kinds.Count == 1 && kinds[0].Decl.Accepts == 1 && kinds[0].Decl.IdleParams is null && events.Count == 0 && !pulseFast.Contains(0))
            {
                W(writer, "var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<global::Tl.Unity.Playback>().WithAllRW<" + kinds[0].Entry + ">().WithAllRW<" + kinds[0].Active + ">();");
                W(writer, "jobs._query = builder.Build(ref state);");
                W(writer, "builder.Dispose();");
            }
            else
            {
                W(writer, "var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<global::Tl.Unity.Playback>();");
                W(writer, "var any = new NativeList<ComponentType>(" + (kinds.Count + events.Count) + ", Allocator.Temp);");
                foreach (var kind in kinds)
                    W(writer, "any.Add(ComponentType.ReadWrite<" + kind.Entry + ">());");
                foreach (var evented in events)
                    W(writer, "any.Add(ComponentType.ReadWrite<global::Tl.Unity.TimelineEvent<" + evented.TypeName + ">>());");
                W(writer, "builder.WithAny(ref any);");
                W(writer, "jobs._query = builder.Build(ref state);");
                W(writer, "any.Dispose();");
                W(writer, "builder.Dispose();");
            }
            foreach (var handle in handles)
                W(writer, "jobs._" + handle.field + " = state." + handle.getter + "(" + (handle.readOnly ? "true" : "false") + ");");
            W(writer, "return jobs;");
            W(writer, "}");
            var contextParams = new StringBuilder();
            var contextArgs = new StringBuilder();
            foreach (var context in model.Contexts)
            {
                contextParams.Append(", " + context.TypeName + " " + context.Name);
                contextArgs.Append(", " + context.Name);
            }
            W(writer, "public JobHandle Schedule(ref SystemState state, global::Tl.LibraryView view, global::Tl.Unity.KindIndex index, bool forward" + contextParams + ", JobHandle dependsOn)");
            W(writer, "{");
            W(writer, "var job = Prepare(ref state, view, index, forward" + contextArgs + ", dependsOn);");
            W(writer, "return Pulses(ref state, index" + contextArgs + ", job.ScheduleParallel(_query, dependsOn));");
            W(writer, "}");
            W(writer, "public " + name + "ChunkJob Prepare(ref SystemState state, global::Tl.LibraryView view, global::Tl.Unity.KindIndex index, bool forward" + contextParams + ", JobHandle dependsOn)");
            W(writer, "{");
            foreach (var handle in handles)
                W(writer, "_" + handle.field + ".Update(ref state);");
            if (pulseFast.Count > 0)
                W(writer, "_Storage.Update(ref state);");
            for (var k = 0; k < kinds.Count; k++)
                W(writer, "var __id" + k + " = index.KindOf(" + KindId(kinds[k].Key) + ");");
            foreach (var k in values)
                W(writer, "if (index.NeedsSpanValues(__id" + k + ")) { dependsOn.Complete(); var __kfill = new " + name + "ChunkJob.KindFill { Index = index }; index.SpanValues(__id" + k + ", ref __kfill); }");
            for (var k = 0; k < kinds.Count; k++)
                if (kinds[k].Decl.PrecombinedPulses)
                    W(writer, "if (index.NeedsPulseValues(__id" + k + ")) { dependsOn.Complete(); var __kp" + k + " = default(" + name + "ChunkJob.KindPulses_" + k + "); index.PulseValues<" + kinds[k].Decl.ValueTypeName + ", " + name + "ChunkJob.KindPulses_" + k + ">(__id" + k + ", ref __kp" + k + "); }");
            W(writer, "var job = new " + name + "ChunkJob { View = view, Index = index, Forward = forward");
            for (var k = 0; k < kinds.Count; k++)
                W(writer, ", KindSlot_" + k + " = __id" + k + " + 2");
            for (var k = 0; k < kinds.Count; k++)
                if (fusedKinds.Contains(k))
                    W(writer, ", NoEnt_" + k + (kinds[k].Decl.Accepts == 0 ? " = __id" + k + " >= 0 && !index.HasForeign(__id" + k + ")" : " = false"));
            foreach (var handle in handles)
                W(writer, ", " + handle.field + " = _" + handle.field);
            foreach (var context in model.Contexts)
                W(writer, ", " + context.Name + " = " + context.Name);
            W(writer, " };");
            W(writer, "return job;");
            W(writer, "}");
            W(writer, "public JobHandle Pulses(ref SystemState state, global::Tl.Unity.KindIndex index" + contextParams + ", JobHandle __dep)");
            W(writer, "{");
            if (pulseFast.Count == 0)
                W(writer, "return __dep;");
            else
            {
                foreach (var k in pulseFast)
                    W(writer, "var __id" + k + " = index.KindOf(" + KindId(kinds[k].Key) + ");");
                W(writer, "var __orderVer = state.EntityManager.GetComponentOrderVersion<global::Tl.Unity.Playback>();");
                foreach (var k in pulseFast)
                {
                    var info = kinds[k];
                    var pArgs = new List<string>
                    {
                        "Index = index",
                        "Kind = __id" + k,
                        "OrderVersion = __orderVer",
                        "EnabledHandle = _" + info.Name + "EnabledHandle"
                    };
                    var fArgs = new List<string>
                    {
                        "Index = index",
                        "Kind = __id" + k,
                        "OrderVersion = __orderVer",
                        "Storage = _Storage",
                        "EntryHandle = _" + info.Name + "EntryHandle",
                        "ActiveHandle = _" + info.Name + "ActiveHandle",
                        "EnabledHandle = _" + info.Name + "EnabledHandle"
                    };
                    foreach (var ci in ApplyColumnIndices(shape, info.Decl.ApplyParams))
                    {
                        var col = shape.Columns[ci];
                        var item = col.Name + "Handle = _" + col.Name + "Handle";
                        pArgs.Add(item);
                        fArgs.Add(item);
                    }
                    foreach (var xi in ApplyContextIndices(shape, info.Decl.ApplyParams))
                    {
                        var ctx = shape.Contexts[xi];
                        var item = ctx.Name + " = " + ctx.Name;
                        pArgs.Add(item);
                        fArgs.Add(item);
                    }
                    W(writer, "__dep = new " + name + "PulseJob_" + k + " { " + string.Join(", ", pArgs) + " }.Schedule(index.ThreadCount, 1, __dep);");
                    W(writer, "__dep = new " + name + "FinishJob_" + k + " { " + string.Join(", ", fArgs) + " }.Schedule(__dep);");
                }
                W(writer, "return __dep;");
            }
            W(writer, "}");
            W(writer, "}");
            W(writer, "}");
        }

        static int KindId(string name)
        {
            var hash = 2166136261u;
            foreach (var c in name)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return unchecked((int)hash);
        }

        static void EmitSlotWalk(StringBuilder writer, string kindIdVar)
        {
            W(writer, "var __spans = (Index.SpanKinds >> " + kindIdVar + " & 1) != 0;");
            W(writer, "for (var __q = 0; __spans && __q < __cnt + __smp; __q++)");
            W(writer, "{");
            W(writer, "var __i = __q < __smp ? __cnt + __q : __q - __smp;");
            W(writer, "var __pb = __run + __i;");
            W(writer, "if (__pb->Timeline == 0 || !global::Tl.KindScore.Listed(__pb->Flags)) continue;");
            W(writer, "if ((uint)(__pb->Timeline - 1) >= (uint)Index.Score.TimelineCount || (Index.Score.Masks[__pb->Timeline - 1].KindMask >> " + kindIdVar + " & 1) == 0) continue;");
            W(writer, "ushort __tick = __i < __cnt ? global::Tl.Unity.TimelineTicks.Real(__pb, __clock) : __pb->Tick;");
            W(writer, "var __cell = Index.Score.CellOf(__pb->Timeline, __tick, " + kindIdVar + ");");
            W(writer, "if (__cell < 0 || Index.Score.Start[__cell] == Index.Score.Start[__cell + 1]) continue;");
        }

        static void EmitSpanHead(StringBuilder writer)
        {
            W(writer, "if ((__pb->Flags & global::Tl.PlaybackFlags.Paused) != 0) continue;");
            W(writer, "var __tl = Index.Timelines + (__pb->Timeline - 1);");
            W(writer, "var __backward = Forward ? (__pb->Flags & global::Tl.PlaybackFlags.Reverse) != 0 : (__pb->Flags & global::Tl.PlaybackFlags.Reverse) == 0;");
            W(writer, "for (var __f = Index.Score.Start[__cell]; __f < Index.Score.Start[__cell + 1]; __f++)");
            W(writer, "{");
            W(writer, "var __sp = Index.Score.Spans + __f;");
            W(writer, "var __r = Index.Routes[__sp->Feeder];");
        }

        static void EmitSpanFrame(StringBuilder writer)
        {
            W(writer, "var __def = __sp->Step;");
            W(writer, "if (__def == null) continue;");
            W(writer, "var __kflags = (__backward ? global::Tl.FrameFlags.Reverse : global::Tl.FrameFlags.None) | (global::Tl.KindScore.Synthetic(__pb->Flags, __tick, __def, __backward) ? global::Tl.FrameFlags.Synthetic : global::Tl.FrameFlags.None);");
            W(writer, "if (__tick == 0) __kflags |= global::Tl.FrameFlags.TimelineStart;");
            W(writer, "if (__tick == (ushort)(__tl->Duration - 1)) __kflags |= global::Tl.FrameFlags.TimelineEnd;");
        }

        static void EmitCases(StringBuilder writer, Shape shape, SystemAssignment assignment, int assignmentIndex, List<(int consumer, FeederDecl feeder)> feeders, string selector, bool spans, string skip = "continue", bool preEval = false)
        {
            var any = false;
            for (var i = 0; i < feeders.Count; i++)
            {
                var (c, feeder) = feeders[i];
                if (spans && feeder.Trigger != FeederTrigger.Span) continue;
                if (preEval && feeder.Trigger == FeederTrigger.Span) continue;
                if (!any) { W(writer, "switch (" + selector + ")"); W(writer, "{"); any = true; }
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
                    if (!spans && !preEval)
                    {
                        W(writer, "var __def = Index.StepInWindow(__en->Timeline, __en->Step, __tick);");
                        W(writer, "if (__def == null) break;");
                    }
                    EmitMaterialize(writer, consumer, assignmentIndex, c);
                    if (FoldOf(consumer) != null)
                        EmitFeeds(writer, consumer, c);
                    var frameVar = "__f" + c;
                    if (IsTlFrame(feeder.Method))
                    {
                        frameVar = "__tf" + c;
                        W(writer, "var " + frameVar + " = new global::Tl.TlFrame<" + consumer.ClipTypeName + ">(in __f" + c + ");");
                    }
                    W(writer, "__v = " + consumer.ConsumerTypeName + "." + feeder.MethodName + "(" + CallArgs(shape, feeder.Method.Params, frameVar, "__row", "__fd" + c + "_") + ");");
                }
                W(writer, "__ok = true;");
                W(writer, "break;");
                W(writer, "}");
            }
            if (any)
                W(writer, "}");
            W(writer, "if (!__ok) " + skip + ";");
        }

        static List<(int consumer, FeederDecl feeder)> FeedersOf(SystemAssignment assignment, KindDecl kind)
        {
            var feeders = new List<(int consumer, FeederDecl feeder)>();
            for (var c = 0; c < assignment.Consumers.Count; c++)
                foreach (var feeder in assignment.Consumers[c].Feeders)
                    if (feeder.KindName == kind.Id)
                        feeders.Add((c, feeder));
            feeders.Sort((a, b) =>
            {
                var by = string.CompareOrdinal(a.feeder.ConsumerMetadata, b.feeder.ConsumerMetadata);
                return by != 0 ? by : string.CompareOrdinal(a.feeder.MethodName, b.feeder.MethodName);
            });
            return feeders;
        }

        static void EmitSpanValues(StringBuilder writer, Shape shape, SystemAssignment assignment, int assignmentIndex, UnityKindInfo info, int ordinal)
        {
            var kind = info.Decl;
            var value = kind.ValueTypeName;
            var law = kind.Monoid != 0;
            W(writer, "public struct KindSpans_" + ordinal + " : global::Tl.IKindSpanValues<" + value + ">");
            W(writer, "{");
            if (law)
            {
                EmitLaw(writer, kind);
                W(writer, "public bool Evaluate(int __r, global::Tl.LibraryStep* __def, global::Tl.LibraryTimeline* __tl, ushort __tick, bool __backward, out " + value + " __v)");
                W(writer, "{");
                W(writer, "if (!__Evaluate(__r, __def, __tl, __tick, __backward, out __v)) return false;");
                W(writer, "__Law(in __v);");
                W(writer, "return true;");
                W(writer, "}");
            }
            W(writer, (law ? "bool __Evaluate" : "public bool Evaluate") + "(int __r, global::Tl.LibraryStep* __def, global::Tl.LibraryTimeline* __tl, ushort __tick, bool __backward, out " + value + " __v)");
            W(writer, "{");
            W(writer, "__v = default;");
            W(writer, "var __kflags = __backward ? global::Tl.FrameFlags.Reverse : global::Tl.FrameFlags.None;");
            W(writer, "if (__tick == 0) __kflags |= global::Tl.FrameFlags.TimelineStart;");
            W(writer, "if (__tick == (ushort)(__tl->Duration - 1)) __kflags |= global::Tl.FrameFlags.TimelineEnd;");
            W(writer, "var __ok = false;");
            EmitCases(writer, shape, assignment, assignmentIndex, FeedersOf(assignment, kind), "__r", true, "return false");
            W(writer, "return true;");
            W(writer, "}");
            W(writer, law
                ? "public void Combine(ref " + value + " total, in " + value + " next) { var __a = total; " + LaneOp(kind, "__a", "next") + kind.TypeName + ".Combine(ref total, in next); __Same(__a, total); }"
                : "public void Combine(ref " + value + " total, in " + value + " next) => " + kind.TypeName + ".Combine(ref total, in next);");
            W(writer, "}");
        }

        static void EmitLaw(StringBuilder writer, KindDecl kind)
        {
            var t = kind.ValueTypeName;
            W(writer, t + " __last; bool __seen;");
            W(writer, "void __Law(in " + t + " __v)");
            W(writer, "{");
            W(writer, t + " __a, __b;");
            if (kind.Monoid != 5)
                W(writer, Identity(kind, "__a") + "__b = __a; " + LaneOp(kind, "__a", "__v") + kind.TypeName + ".Combine(ref __b, in __v); __Same(__a, __b);");
            W(writer, "__a = __v; __b = __v; " + LaneOp(kind, "__a", "__v") + kind.TypeName + ".Combine(ref __b, in __v); __Same(__a, __b);");
            W(writer, "if (__seen) { __a = __last; __b = __last; " + LaneOp(kind, "__a", "__v") + kind.TypeName + ".Combine(ref __b, in __v); __Same(__a, __b); }");
            W(writer, "__last = __v; __seen = true;");
            W(writer, "}");
            var tests = new List<string>();
            for (var f = kind.Monoid == 5 ? 1 : 0; f < kind.LaneFields.Count; f++)
            {
                var field = kind.LaneFields[f];
                var a = LanePath("__a", field.Path);
                var b = LanePath("__b", field.Path);
                tests.Add(field.Type == "float" ? "global::Unity.Mathematics.math.asint(" + a + ") != global::Unity.Mathematics.math.asint(" + b + ")"
                    : field.Type == "double" ? "global::Unity.Mathematics.math.aslong(" + a + ") != global::Unity.Mathematics.math.aslong(" + b + ")"
                    : a + " != " + b);
            }
            var monoid = kind.Monoid switch { 1 => "Sum", 2 => "Max", 3 => "Min", 4 => "Or", 5 => "Pick", _ => "Custom" };
            W(writer, "static void __Same(" + t + " __a, " + t + " __b)");
            W(writer, "{");
            if (tests.Count > 0)
                W(writer, "if (" + string.Join(" || ", tests) + ") throw new global::System.InvalidOperationException(\"Tl: kind '" + kind.Id + "' declares Combine = " + monoid + ", but " + kind.TypeName.Replace("global::", "") + ".Combine gives a different result on values from the library; fix Combine or the declaration.\");");
            W(writer, "}");
        }

        static void EmitPulseValues(StringBuilder writer, Shape shape, SystemAssignment assignment, int assignmentIndex, UnityKindInfo info, int ordinal)
        {
            var kind = info.Decl;
            var value = kind.ValueTypeName;
            W(writer, "public struct KindPulses_" + ordinal + " : global::Tl.IKindSpanValues<" + value + ">");
            W(writer, "{");
            W(writer, "public bool Evaluate(int __r, global::Tl.LibraryStep* __def, global::Tl.LibraryTimeline* __tl, ushort __tick, bool __backward, out " + value + " __v)");
            W(writer, "{");
            W(writer, "__v = default;");
            W(writer, "if (__tick < __def->WindowStart || __tick >= __def->WindowEnd) return false;");
            W(writer, "var __kflags = __backward ? global::Tl.FrameFlags.Reverse : global::Tl.FrameFlags.None;");
            W(writer, "if (__tick == 0) __kflags |= global::Tl.FrameFlags.TimelineStart;");
            W(writer, "if (__tick == (ushort)(__tl->Duration - 1)) __kflags |= global::Tl.FrameFlags.TimelineEnd;");
            W(writer, "var __ok = false;");
            EmitCases(writer, shape, assignment, assignmentIndex, FeedersOf(assignment, kind), "__r", false, "return false", preEval: true);
            W(writer, "return true;");
            W(writer, "}");
            W(writer, "public void Combine(ref " + value + " total, in " + value + " next) { }");
            W(writer, "}");
        }

        static void EmitValueWalk(StringBuilder writer, KindDecl kind)
        {
            W(writer, "for (var __q = 0; __q < __cnt + __smp; __q++)");
            W(writer, "{");
            W(writer, "var __i = __q < __smp ? __cnt + __q : __q - __smp;");
            W(writer, "var __pb = __run + __i;");
            W(writer, "if (__pb->Timeline == 0 || !global::Tl.KindScore.Listed(__pb->Flags)) continue;");
            W(writer, "ushort __tick = __i < __cnt ? global::Tl.Unity.TimelineTicks.Real(__pb, __clock) : __pb->Tick;");
            W(writer, "var __vr = __kv->RowOf(__pb->Timeline, __tick);");
            W(writer, "if (__vr == null) continue;");
            W(writer, "if (__i < __cnt) { __listed = true; if (__sj != 0 || __pb->Rate != 0) continue; }");
            W(writer, "else if (__pb->Phase != __sj) continue;");
            W(writer, "if ((__pb->Flags & global::Tl.PlaybackFlags.Paused) != 0) continue;");
            W(writer, "var __bk = Forward ? (__pb->Flags & global::Tl.PlaybackFlags.Reverse) != 0 : (__pb->Flags & global::Tl.PlaybackFlags.Reverse) == 0;");
            W(writer, "if ((*(uint*)__vr >> __kv->Shift & (__bk ? 4u : 2u)) == 0) continue;");
            W(writer, "ref readonly var __sv = ref *(" + kind.ValueTypeName + "*)(__vr + (__bk ? __kv->Back : __kv->Front));");
            W(writer, "if (__has) " + kind.TypeName + ".Combine(ref __total, in __sv);");
            W(writer, "else { __total = __sv; __has = true; }");
            W(writer, "}");
        }

        static void EmitRowHead(StringBuilder writer)
        {
            W(writer, "var __pbuf = __playbacks[__row];");
            W(writer, "var __run = (global::Tl.Playback*)__pbuf.GetUnsafeReadOnlyPtr();");
            W(writer, "var __cnt = __pbuf.Length;");
            W(writer, "var __smp = __copies == null ? 0 : (int)__copies[__row].Copies;");
            W(writer, "if (__cnt + __smp > __pbuf.Capacity) __smp = 0;");
            W(writer, "var __clock = __clocks == null ? (ushort)0 : __clocks[__row];");
        }

        static void EmitEntryWalk(StringBuilder writer, Shape shape, SystemAssignment assignment, int assignmentIndex, KindDecl kind, List<(int consumer, FeederDecl feeder)> feeders, string strideVar, string tail, bool lazyRow = false)
        {
            var value = kind.ValueTypeName;
            W(writer, "for (var __j = 0; __j < __len; __j++)");
            W(writer, "{");
            W(writer, "var __en = (global::Tl.KindEntry*)(__data + (long)__j * " + strideVar + ");");
            W(writer, "if (__sampled && !global::Tl.KindRules.Due(__en, __sj, __run, __cnt)) continue;");
            W(writer, value + " __v = default;");
            W(writer, "if (__en->Timeline == 0)");
            W(writer, "__v = *(" + value + "*)((byte*)__en + 8);");
            W(writer, "else if (__en->Owner != 0)");
            W(writer, "{");
            W(writer, "if ((__en->Flags & global::Tl.KindEntryFlags.Ready) == 0) continue;");
            W(writer, "__v = *(" + value + "*)((byte*)__en + 8);");
            W(writer, "__en->Flags &= ~global::Tl.KindEntryFlags.Ready;");
            W(writer, "}");
            W(writer, "else");
            W(writer, "{");
            if (lazyRow)
                W(writer, "if (!__rowInit) { if (!__pbReady) { __playbacks = chunk.GetBufferAccessor(ref PlaybackHandle); __clocks = (ushort*)chunk.GetComponentDataPtrRO(ref ClockHandle); __pbReady = true; } var __pbuf = __playbacks[__row]; __run = (global::Tl.Playback*)__pbuf.GetUnsafeReadOnlyPtr(); __cnt = __pbuf.Length; __clock = (__clocks == null ? (ushort)0 : __clocks[__row]); __rowInit = true; }");
            W(writer, "var __pb = __run + __en->Slot;");
            W(writer, "var __tl = Index.Timelines + (__en->Timeline - 1);");
            W(writer, "ushort __tick = __en->Slot < __cnt ? global::Tl.Unity.TimelineTicks.Real(__pb, __clock) : __pb->Tick;");
            W(writer, "var __backward = Forward ? (__pb->Flags & global::Tl.PlaybackFlags.Reverse) != 0 : (__pb->Flags & global::Tl.PlaybackFlags.Reverse) == 0;");
            W(writer, "var __kflags = (__backward ? global::Tl.FrameFlags.Reverse : global::Tl.FrameFlags.None)");
            W(writer, "| ((__en->Flags & global::Tl.KindEntryFlags.Synthetic) != 0 ? global::Tl.FrameFlags.Synthetic : global::Tl.FrameFlags.None)");
            W(writer, "| ((__en->Flags & global::Tl.KindEntryFlags.Interrupted) != 0 ? global::Tl.FrameFlags.Interrupted : global::Tl.FrameFlags.None);");
            W(writer, "if (__tick == 0) __kflags |= global::Tl.FrameFlags.TimelineStart;");
            W(writer, "if (__tick == (ushort)(__tl->Duration - 1)) __kflags |= global::Tl.FrameFlags.TimelineEnd;");
            W(writer, "var __ok = false;");
            EmitCases(writer, shape, assignment, assignmentIndex, feeders, "__en->Feeder", false);
            W(writer, "}");
            if (kind.Each)
                W(writer, kind.TypeName + ".Apply(in __v" + tail + ");");
            else
            {
                W(writer, "if (__has) " + kind.TypeName + ".Combine(ref __total, in __v);");
                W(writer, "else { __total = __v; __has = true; }");
            }
            W(writer, "}");
        }

        static void EmitFused(StringBuilder writer, Shape shape, SystemAssignment assignment, int assignmentIndex, IReadOnlyList<UnityKindInfo> kinds, List<int> fused, string name)
        {
            var f0 = fused[0];
            var bitParams = new StringBuilder(", global::Unity.Burst.Intrinsics.v128 __epl");
            foreach (var i in fused)
                bitParams.Append(", global::Unity.Burst.Intrinsics.v128 __ema" + i + ", global::Unity.Burst.Intrinsics.v128 __eme" + i);
            W(writer, "bool " + name + "(in ArchetypeChunk chunk, BufferAccessor<global::Tl.Unity.Playback> __playbacks" + bitParams + ColumnArgs(shape, true) + ")");
            W(writer, "{");
            foreach (var i in fused)
            {
                W(writer, "var __kid" + i + " = KindSlot_" + i + " != 0 ? KindSlot_" + i + " - 2 : Index.KindOf(" + KindId(kinds[i].Key) + ");");
                W(writer, "if (__kid" + i + " < 0) return false;");
                W(writer, "var __kv" + i + " = Index.Values + __kid" + i + ";");
                W(writer, "if (__kv" + i + "->Data == null || __kv" + i + "->Data != __kv" + f0 + "->Data) return false;");
            }
            W(writer, "var __vrow = __kv" + f0 + "->Row; var __vdata = __kv" + f0 + "->Data; var __vstride = (nuint)__kv" + f0 + "->Stride; var __vtc = (uint)__kv" + f0 + "->TimelineCount;");
            foreach (var i in fused)
            {
                W(writer, "var __vsh" + i + " = __kv" + i + "->Shift; var __vlm" + i + " = 1u << __vsh" + i + "; var __vm" + i + " = 1u << (__vsh" + i + " + (Forward ? 1 : 2)); var __vfr" + i + " = __kv" + i + "->Front; var __vbk" + i + " = __kv" + i + "->Back; var __vf" + i + " = Forward ? __vfr" + i + " : __vbk" + i + ";");
                W(writer, "var __noEnt" + i + " = NoEnt_" + i + ";");
            }
            W(writer, "var __anySampled = Index.SampledCount == null || *Index.SampledCount != 0;");
            W(writer, "var __clocks = (ushort*)chunk.GetComponentDataPtrRO(ref ClockHandle);");
            W(writer, "var __count = chunk.Count;");
            W(writer, "var __m0 = __count >= 64 ? ~0ul : (1ul << __count) - 1;");
            W(writer, "var __m1 = __count <= 64 ? 0ul : (__count >= 128 ? ~0ul : (1ul << (__count - 64)) - 1);");
            var fastCond = new List<string> { "!__anySampled", "__clocks == null" };
            foreach (var i in fused)
            {
                fastCond.Add("__noEnt" + i);
                fastCond.Add("(((__ema" + i + ".ULong0 | __epl.ULong0) & __eme" + i + ".ULong0 & __m0) == __m0)");
                fastCond.Add("(((__ema" + i + ".ULong1 | __epl.ULong1) & __eme" + i + ".ULong1 & __m1) == __m1)");
            }
            W(writer, "if (" + string.Join(" && ", fastCond) + ")");
            W(writer, "{");
            foreach (var i in fused)
                W(writer, "ulong __n" + i + "_0 = 0, __n" + i + "_1 = 0;");
            W(writer, "for (var __row = 0; __row < __count; __row++)");
            W(writer, "{");
            W(writer, "var __pbuf = __playbacks[__row];");
            W(writer, "var __run = (global::Tl.Playback*)__pbuf.GetUnsafeReadOnlyPtr();");
            W(writer, "var __cnt = __pbuf.Length;");
            foreach (var i in fused)
                W(writer, kinds[i].Decl.ValueTypeName + " __ft" + i + " = default; var __fh" + i + " = false; var __fl" + i + " = false;");
            W(writer, "for (var __i = 0; __i < __cnt; __i++)");
            W(writer, "{");
            W(writer, "var __pb = __run + __i; var __tid = __pb->Timeline; if (__tid == 0) continue;");
            W(writer, "if (((byte)__pb->Flags | __pb->Rate) == 0)");
            W(writer, "{");
            W(writer, "var __t0 = (uint)(__tid - 1); if (__t0 >= __vtc) continue; var __vr0 = __vrow[__t0]; if (__vr0 < 0) continue;");
            W(writer, "var __at = __vdata + (nuint)(__vr0 + __pb->Tick) * __vstride; var __m = *(uint*)__at;");
            foreach (var i in fused)
            {
                var kind = kinds[i].Decl;
                W(writer, "if ((__m & __vlm" + i + ") != 0) { __fl" + i + " = true; if ((__m & __vm" + i + ") != 0) { ref readonly var __sv = ref *(" + kind.ValueTypeName + "*)(__at + __vf" + i + "); if (__fh" + i + ") " + kind.TypeName + ".Combine(ref __ft" + i + ", in __sv); else { __ft" + i + " = __sv; __fh" + i + " = true; } } }");
            }
            W(writer, "}");
            W(writer, "else if (global::Tl.KindScore.Listed(__pb->Flags))");
            W(writer, "{");
            W(writer, "ushort __tick = global::Tl.Unity.TimelineTicks.Real(__pb, 0);");
            W(writer, "var __hd = __kv" + f0 + "->HeadOf(__tid, __tick); if (__hd == null) continue; var __m = *__hd;");
            W(writer, "var __emit = __pb->Rate == 0 && (__pb->Flags & global::Tl.PlaybackFlags.Paused) == 0;");
            W(writer, "var __bk = Forward ? (__pb->Flags & global::Tl.PlaybackFlags.Reverse) != 0 : (__pb->Flags & global::Tl.PlaybackFlags.Reverse) == 0;");
            W(writer, "var __d = __bk ? 2 : 1;");
            foreach (var i in fused)
            {
                var kind = kinds[i].Decl;
                W(writer, "if ((__m & __vlm" + i + ") != 0) { __fl" + i + " = true; if (__emit && (__m >> (__vsh" + i + " + __d) & 1) != 0) { ref readonly var __sv = ref *(" + kind.ValueTypeName + "*)((byte*)__hd + (__bk ? __vbk" + i + " : __vfr" + i + ")); if (__fh" + i + ") " + kind.TypeName + ".Combine(ref __ft" + i + ", in __sv); else { __ft" + i + " = __sv; __fh" + i + " = true; } } }");
            }
            W(writer, "}");
            W(writer, "}");
            foreach (var i in fused)
            {
                var info = kinds[i];
                var kind = info.Decl;
                var applyArgs = CallArgs(shape, kind.ApplyParams, "", "__row", "");
                var tail = applyArgs.Length == 0 ? "" : ", " + applyArgs;
                W(writer, "if (__fh" + i + ") " + kind.TypeName + ".Apply(in __ft" + i + tail + ");");
                W(writer, "if (__fl" + i + ") { if (__row < 64) __n" + i + "_0 |= 1ul << __row; else __n" + i + "_1 |= 1ul << (__row - 64); }");
            }
            W(writer, "}");
            foreach (var i in fused)
            {
                var info = kinds[i];
                W(writer, "var __d" + i + "_0 = (__n" + i + "_0 ^ __ema" + i + ".ULong0) & __m0;");
                W(writer, "var __d" + i + "_1 = (__n" + i + "_1 ^ __ema" + i + ".ULong1) & __m1;");
                W(writer, "if ((__d" + i + "_0 | __d" + i + "_1) != 0)");
                W(writer, "{");
                W(writer, "var __act" + i + " = chunk.GetEnabledMask(ref " + info.Name + "ActiveHandle);");
                W(writer, "while (__d" + i + "_0 != 0) { var __b = (int)global::Unity.Mathematics.math.tzcnt(__d" + i + "_0); __d" + i + "_0 &= __d" + i + "_0 - 1; __act" + i + "[__b] = (__n" + i + "_0 >> __b & 1) != 0; }");
                W(writer, "while (__d" + i + "_1 != 0) { var __b = (int)global::Unity.Mathematics.math.tzcnt(__d" + i + "_1); __d" + i + "_1 &= __d" + i + "_1 - 1; __act" + i + "[64 + __b] = (__n" + i + "_1 >> __b & 1) != 0; }");
                W(writer, "}");
            }
            W(writer, "return true;");
            W(writer, "}");
            W(writer, "var __copies = __anySampled ? (global::Tl.Unity.TimelinePlaying*)chunk.GetComponentDataPtrRO(ref PlayingHandle) : null;");
            foreach (var i in fused)
            {
                var info = kinds[i];
                W(writer, "var __stride" + i + " = sizeof(" + info.Entry + ");");
                W(writer, "var __acc" + i + " = default(BufferAccessor<" + info.Entry + ">); var __accReady" + i + " = false;");
                W(writer, "var __active" + i + " = default(EnabledMask); var __actReady" + i + " = false;");
            }
            W(writer, "for (var __kw = 0; __kw < 2; __kw++)");
            W(writer, "{");
            W(writer, "if (__kw == 1 && __count <= 64) break;");
            W(writer, "var __eplw = __kw == 0 ? __epl.ULong0 : __epl.ULong1;");
            foreach (var i in fused)
            {
                W(writer, "var __ema" + i + "w = __kw == 0 ? __ema" + i + ".ULong0 : __ema" + i + ".ULong1;");
                W(writer, "var __eme" + i + "w = __kw == 0 ? __eme" + i + ".ULong0 : __eme" + i + ".ULong1;");
                W(writer, "var __kb" + i + " = (__ema" + i + "w | __eplw) & __eme" + i + "w;");
            }
            W(writer, "var __kba = " + string.Join(" | ", fused.ConvertAll(static i => "__kb" + i)) + ";");
            W(writer, "while (__kba != 0)");
            W(writer, "{");
            W(writer, "var __bit = (int)global::Unity.Mathematics.math.tzcnt(__kba);");
            W(writer, "__kba &= __kba - 1;");
            W(writer, "var __row = (__kw << 6) + __bit;");
            W(writer, "if (__row >= __count) break;");
            EmitRowHead(writer);
            W(writer, "var __sampled = __smp != 0;");
            foreach (var i in fused)
                W(writer, kinds[i].Decl.ValueTypeName + " __ft" + i + " = default; var __fh" + i + " = false; var __fl" + i + " = false;");
            W(writer, "if (!__sampled)");
            W(writer, "{");
            W(writer, "if (__clocks == null)");
            W(writer, "{");
            W(writer, "for (var __i = 0; __i < __cnt; __i++)");
            W(writer, "{");
            W(writer, "var __pb = __run + __i; var __tid = __pb->Timeline; if (__tid == 0) continue;");
            W(writer, "if (((byte)__pb->Flags | __pb->Rate) == 0)");
            W(writer, "{");
            W(writer, "var __t0 = (uint)(__tid - 1); if (__t0 >= __vtc) continue; var __vr0 = __vrow[__t0]; if (__vr0 < 0) continue;");
            W(writer, "var __at = __vdata + (nuint)(__vr0 + __pb->Tick) * __vstride; var __m = *(uint*)__at;");
            foreach (var i in fused)
            {
                var kind = kinds[i].Decl;
                W(writer, "if ((__m & __vlm" + i + ") != 0) { __fl" + i + " = true; if ((__m & __vm" + i + ") != 0) { ref readonly var __sv = ref *(" + kind.ValueTypeName + "*)(__at + __vf" + i + "); if (__fh" + i + ") " + kind.TypeName + ".Combine(ref __ft" + i + ", in __sv); else { __ft" + i + " = __sv; __fh" + i + " = true; } } }");
            }
            W(writer, "}");
            W(writer, "else if (global::Tl.KindScore.Listed(__pb->Flags))");
            W(writer, "{");
            W(writer, "ushort __tick = global::Tl.Unity.TimelineTicks.Real(__pb, 0);");
            W(writer, "var __hd = __kv" + f0 + "->HeadOf(__tid, __tick); if (__hd == null) continue; var __m = *__hd;");
            W(writer, "var __emit = __pb->Rate == 0 && (__pb->Flags & global::Tl.PlaybackFlags.Paused) == 0;");
            W(writer, "var __bk = Forward ? (__pb->Flags & global::Tl.PlaybackFlags.Reverse) != 0 : (__pb->Flags & global::Tl.PlaybackFlags.Reverse) == 0;");
            W(writer, "var __d = __bk ? 2 : 1;");
            foreach (var i in fused)
            {
                var kind = kinds[i].Decl;
                W(writer, "if ((__m & __vlm" + i + ") != 0) { __fl" + i + " = true; if (__emit && (__m >> (__vsh" + i + " + __d) & 1) != 0) { ref readonly var __sv = ref *(" + kind.ValueTypeName + "*)((byte*)__hd + (__bk ? __vbk" + i + " : __vfr" + i + ")); if (__fh" + i + ") " + kind.TypeName + ".Combine(ref __ft" + i + ", in __sv); else { __ft" + i + " = __sv; __fh" + i + " = true; } } }");
            }
            W(writer, "}");
            W(writer, "}");
            W(writer, "}");
            W(writer, "else");
            W(writer, "{");
            W(writer, "for (var __i = 0; __i < __cnt; __i++)");
            W(writer, "{");
            W(writer, "var __pb = __run + __i; var __tid = __pb->Timeline; if (__tid == 0) continue;");
            W(writer, "if ((((byte)__pb->Flags | __pb->Rate) | __clock) == 0)");
            W(writer, "{");
            W(writer, "var __t0 = (uint)(__tid - 1); if (__t0 >= __vtc) continue; var __vr0 = __vrow[__t0]; if (__vr0 < 0) continue;");
            W(writer, "var __at = __vdata + (nuint)(__vr0 + __pb->Tick) * __vstride; var __m = *(uint*)__at;");
            foreach (var i in fused)
            {
                var kind = kinds[i].Decl;
                W(writer, "if ((__m & __vlm" + i + ") != 0) { __fl" + i + " = true; if ((__m & __vm" + i + ") != 0) { ref readonly var __sv = ref *(" + kind.ValueTypeName + "*)(__at + __vf" + i + "); if (__fh" + i + ") " + kind.TypeName + ".Combine(ref __ft" + i + ", in __sv); else { __ft" + i + " = __sv; __fh" + i + " = true; } } }");
            }
            W(writer, "}");
            W(writer, "else if (global::Tl.KindScore.Listed(__pb->Flags))");
            W(writer, "{");
            W(writer, "ushort __tick = global::Tl.Unity.TimelineTicks.Real(__pb, __clock);");
            W(writer, "var __hd = __kv" + f0 + "->HeadOf(__tid, __tick); if (__hd == null) continue; var __m = *__hd;");
            W(writer, "var __emit = __pb->Rate == 0 && (__pb->Flags & global::Tl.PlaybackFlags.Paused) == 0;");
            W(writer, "var __bk = Forward ? (__pb->Flags & global::Tl.PlaybackFlags.Reverse) != 0 : (__pb->Flags & global::Tl.PlaybackFlags.Reverse) == 0;");
            W(writer, "var __d = __bk ? 2 : 1;");
            foreach (var i in fused)
            {
                var kind = kinds[i].Decl;
                W(writer, "if ((__m & __vlm" + i + ") != 0) { __fl" + i + " = true; if (__emit && (__m >> (__vsh" + i + " + __d) & 1) != 0) { ref readonly var __sv = ref *(" + kind.ValueTypeName + "*)((byte*)__hd + (__bk ? __vbk" + i + " : __vfr" + i + ")); if (__fh" + i + ") " + kind.TypeName + ".Combine(ref __ft" + i + ", in __sv); else { __ft" + i + " = __sv; __fh" + i + " = true; } } }");
            }
            W(writer, "}");
            W(writer, "}");
            W(writer, "}");
            W(writer, "}");
            foreach (var i in fused)
            {
                var info = kinds[i];
                var kind = info.Decl;
                var value = kind.ValueTypeName;
                var applyArgs = CallArgs(shape, kind.ApplyParams, "", "__row", "");
                var tail = applyArgs.Length == 0 ? "" : ", " + applyArgs;
                var feeders = FeedersOf(assignment, kind);
                W(writer, "if ((__kb" + i + " >> __bit & 1) != 0)");
                W(writer, "{");
                W(writer, "if (!__sampled && __noEnt" + i + ")");
                W(writer, "{");
                W(writer, "if (__fh" + i + ") " + kind.TypeName + ".Apply(in __ft" + i + tail + ");");
                W(writer, "if (__fl" + i + " != ((__ema" + i + "w >> __bit & 1) != 0)) { if (!__actReady" + i + ") { __active" + i + " = chunk.GetEnabledMask(ref " + info.Name + "ActiveHandle); __actReady" + i + " = true; } __active" + i + "[__row] = __fl" + i + "; }");
                W(writer, "}");
                W(writer, "else");
                W(writer, "{");
                W(writer, "var __kv = __kv" + i + ";");
                W(writer, "var __listed = false;");
                W(writer, "var __len = 0;");
                W(writer, "byte* __data = null;");
                W(writer, "var __buf = default(DynamicBuffer<" + info.Entry + ">);");
                W(writer, "if (!__noEnt" + i + ")");
                W(writer, "{");
                W(writer, "if (!__accReady" + i + ") { __acc" + i + " = chunk.GetBufferAccessor(ref " + info.Name + "EntryHandle); __accReady" + i + " = true; }");
                W(writer, "__buf = __acc" + i + "[__row]; __len = __buf.Length; __data = (byte*)__buf.GetUnsafePtr();");
                W(writer, "}");
                W(writer, "var __sk = __sampled ? global::Tl.KindRules.Depth(__run + __cnt, __smp) : 1;");
                W(writer, "for (var __sj = 0; __sj < __sk; __sj++)");
                W(writer, "{");
                W(writer, value + " __total = __ft" + i + ";");
                W(writer, "var __has = __fh" + i + ";");
                W(writer, "if (__sampled)");
                W(writer, "{");
                EmitValueWalk(writer, kind);
                W(writer, "}");
                W(writer, "else __listed |= __fl" + i + ";");
                W(writer, "if (__len != 0)");
                W(writer, "{");
                EmitEntryWalk(writer, shape, assignment, assignmentIndex, kind, feeders, "__stride" + i, tail);
                W(writer, "}");
                W(writer, "if (__has) " + kind.TypeName + ".Apply(in __total" + tail + ");");
                W(writer, "}");
                W(writer, "if (__len != 0)");
                W(writer, "{");
                W(writer, "var __kept = 0;");
                W(writer, "for (var __j = 0; __j < __len; __j++)");
                W(writer, "{");
                W(writer, "var __en = (global::Tl.KindEntry*)(__data + (long)__j * __stride" + i + ");");
                W(writer, "if ((__en->Flags & global::Tl.KindEntryFlags.Pulse) != 0 || __en->Timeline == 0) continue;");
                W(writer, "if (__kept != __j) global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(__data + (long)__kept * __stride" + i + ", __en, __stride" + i + ");");
                W(writer, "__kept++;");
                W(writer, "}");
                W(writer, "__buf.ResizeUninitialized(__kept);");
                W(writer, "__listed |= __kept != 0;");
                W(writer, "}");
                W(writer, "if (!__noEnt" + i + " && __buf.Capacity > " + info.Capacity + " && __buf.Length <= " + info.Capacity + ") __buf.TrimExcess();");
                W(writer, "if (__listed != ((__ema" + i + "w >> __bit & 1) != 0)) { if (!__actReady" + i + ") { __active" + i + " = chunk.GetEnabledMask(ref " + info.Name + "ActiveHandle); __actReady" + i + " = true; } __active" + i + "[__row] = __listed; }");
                W(writer, "}");
                W(writer, "}");
            }
            W(writer, "}");
            W(writer, "}");
            W(writer, "return true;");
            W(writer, "}");
        }

        static bool Lanes(KindDecl kind) => kind.Monoid is > 0 and < 5;

        static string LanePath(string root, string path) => path.Length == 0 ? root : root + "." + path;

        static string Identity(KindDecl kind, string target)
        {
            var sb = new StringBuilder(target + " = default; ");
            if (Lanes(kind))
                foreach (var f in kind.LaneFields)
                    sb.Append(LanePath(target, f.Path) + " = " + (kind.Monoid switch { 1 => f.Type == "float" ? "-0f" : f.Type == "double" ? "-0d" : "0", 2 => f.Type + ".MinValue", 3 => f.Type + ".MaxValue", _ => f.Type == "bool" ? "false" : "0" }) + "; ");
            return sb.ToString();
        }

        static string LaneOp(KindDecl kind, string total, string next)
        {
            if (kind.Monoid == 5) return "if (" + next + "." + kind.LaneFields[0].Path + " >= " + total + "." + kind.LaneFields[0].Path + ") " + total + " = " + next + "; ";
            var sb = new StringBuilder();
            foreach (var f in kind.LaneFields)
            {
                var t = LanePath(total, f.Path);
                var n = LanePath(next, f.Path);
                sb.Append(kind.Monoid switch { 1 => t + " += " + n + "; ", 2 => t + " = global::System.Math.Max(" + t + ", " + n + "); ", 3 => t + " = global::System.Math.Min(" + t + ", " + n + "); ", _ => t + " |= " + n + "; " });
            }
            return sb.ToString();
        }


        static void EmitKind(StringBuilder writer, Shape shape, SystemAssignment assignment, int assignmentIndex, UnityKindInfo info, int ordinal, bool useValues)
        {
            var kind = info.Decl;
            var feeders = FeedersOf(assignment, kind);
            var spanless = kind.Accepts == 1;
            var value = kind.ValueTypeName;
            var applyArgs = CallArgs(shape, kind.ApplyParams, "", "__row", "");
            var tail = applyArgs.Length == 0 ? "" : ", " + applyArgs;
            W(writer, spanless
                ? "void Kind_" + ordinal + "(in ArchetypeChunk chunk" + ColumnArgs(shape, true) + ")"
                : "void Kind_" + ordinal + "(in ArchetypeChunk chunk, BufferAccessor<global::Tl.Unity.Playback> __playbacks" + ColumnArgs(shape, true) + ")");
            W(writer, "{");
            if (spanless)
            {
                W(writer, "var __pbReady = false;");
                W(writer, "var __playbacks = default(BufferAccessor<global::Tl.Unity.Playback>);");
                W(writer, "ushort* __clocks = null;");
                }
            else
            {
                W(writer, "var __clocks = (ushort*)chunk.GetComponentDataPtrRO(ref ClockHandle);");
                }
            W(writer, "if (!chunk.Has(ref " + info.Name + "EntryHandle)) return;");
            W(writer, "var __kid = KindSlot_" + ordinal + " != 0 ? KindSlot_" + ordinal + " - 2 : Index.KindOf(" + KindId(info.Key) + ");");
            W(writer, "if (__kid < 0) return;");
            if (useValues)
                W(writer, "var __kv = Index.Values + __kid;");
            W(writer, "var __copies = (global::Tl.Unity.TimelinePlaying*)chunk.GetComponentDataPtrRO(ref PlayingHandle);");
            if (!spanless) W(writer, "var __epl = chunk.GetEnableableBits(ref PlayingHandle);");
            W(writer, "var __acc = chunk.GetBufferAccessor(ref " + info.Name + "EntryHandle);");
            W(writer, "var __ema = chunk.GetEnableableBits(ref " + info.Name + "ActiveHandle);");
            W(writer, "var __eme = chunk.GetEnableableBits(ref " + info.Name + "EnabledHandle);");
            W(writer, "var __stride = sizeof(" + info.Entry + ");");
            W(writer, "var __count = chunk.Count;");
            W(writer, "var __active = chunk.GetEnabledMask(ref " + info.Name + "ActiveHandle);");
            W(writer, "for (var __kw = 0; __kw < 2; __kw++)");
            W(writer, "{");
            W(writer, "if (__kw == 1 && __count <= 64) break;");
            W(writer, spanless ? "var __kb = __kw == 0 ? __ema.ULong0 & __eme.ULong0 : __ema.ULong1 & __eme.ULong1;" : "var __kb = __kw == 0 ? (__ema.ULong0 | __epl.ULong0) & __eme.ULong0 : (__ema.ULong1 | __epl.ULong1) & __eme.ULong1;");
            W(writer, "while (__kb != 0)");
            W(writer, "{");
            W(writer, "var __row = (__kw << 6) + (int)global::Unity.Mathematics.math.tzcnt(__kb);");
            W(writer, "__kb &= __kb - 1;");
            W(writer, "if (__row >= __count) break;");
            W(writer, "var __buf = __acc[__row];");
            W(writer, "var __len = __buf.Length;");
            W(writer, "byte* __data = (byte*)__buf.GetUnsafePtr();");
            if (spanless)
            {
                W(writer, "global::Tl.Playback* __run = null; var __cnt = 0; ushort __clock = 0; var __rowInit = false;");
                W(writer, "var __smp = __copies == null ? 0 : (int)__copies[__row].Copies;");
                W(writer, "var __sampled = __smp != 0;");
                W(writer, "if (__sampled)");
                W(writer, "{");
                W(writer, "if (!__pbReady) { __playbacks = chunk.GetBufferAccessor(ref PlaybackHandle); __clocks = (ushort*)chunk.GetComponentDataPtrRO(ref ClockHandle); __pbReady = true; }");
                W(writer, "var __pbuf = __playbacks[__row]; __run = (global::Tl.Playback*)__pbuf.GetUnsafeReadOnlyPtr(); __cnt = __pbuf.Length; if (__cnt + __smp > __pbuf.Capacity) __smp = 0; __sampled = __smp != 0; __clock = (__clocks == null ? (ushort)0 : __clocks[__row]); __rowInit = true;");
                W(writer, "}");
                W(writer, "var __sk = __sampled ? global::Tl.KindRules.Depth(__run + __cnt, __smp) : 1;");
            }
            else
            {
                EmitRowHead(writer);
                W(writer, "var __listed = false;");
                W(writer, "var __sampled = __smp != 0;");
                W(writer, "var __sk = __sampled ? global::Tl.KindRules.Depth(__run + __cnt, __smp) : 1;");
            }
            W(writer, "for (var __sj = 0; __sj < __sk; __sj++)");
            W(writer, "{");
            if (!kind.Each)
            {
                W(writer, value + " __total = default;");
                W(writer, "var __has = false;");
            }
            if (useValues)
            {
                W(writer, "if (__kv->Data != null)");
                W(writer, "{");
                EmitValueWalk(writer, kind);
                W(writer, "}");
                W(writer, "else");
                W(writer, "{");
            }
            if (!spanless)
            {
                EmitSlotWalk(writer, "__kid");
                W(writer, "if (__i < __cnt) { __listed = true; if (__sj != 0 || __pb->Rate != 0) continue; }");
                W(writer, "else if (__pb->Phase != __sj) continue;");
                EmitSpanHead(writer);
                W(writer, "if ((__r >> 8) != 0) continue;");
                EmitSpanFrame(writer);
                W(writer, value + " __v = default;");
                W(writer, "var __ok = false;");
                EmitCases(writer, shape, assignment, assignmentIndex, feeders, "__r & 0xff", true);
                if (kind.Each) W(writer, kind.TypeName + ".Apply(in __v" + tail + ");");
                else
                {
                    W(writer, "if (__has) " + kind.TypeName + ".Combine(ref __total, in __v);");
                    W(writer, "else { __total = __v; __has = true; }");
                }
                W(writer, "}");
                W(writer, "}");
            }
            if (useValues)
                W(writer, "}");
            EmitEntryWalk(writer, shape, assignment, assignmentIndex, kind, feeders, "__stride", tail, spanless);
            if (!kind.Each)
                W(writer, "if (__has) " + kind.TypeName + ".Apply(in __total" + tail + ");");
            W(writer, "}");
            if (spanless)
            {
                W(writer, "if (__len != 0) __buf.ResizeUninitialized(0);");
                W(writer, "__active[__row] = false;");
                W(writer, "if (__buf.Capacity > " + info.Capacity + ") __buf.TrimExcess();");
            }
            else
            {
                W(writer, "if (__len != 0)");
                W(writer, "{");
                W(writer, "var __kept = 0;");
                W(writer, "for (var __j = 0; __j < __len; __j++)");
                W(writer, "{");
                W(writer, "var __en = (global::Tl.KindEntry*)(__data + (long)__j * __stride);");
                W(writer, "if ((__en->Flags & global::Tl.KindEntryFlags.Pulse) != 0 || __en->Timeline == 0) continue;");
                W(writer, "if (__kept != __j) global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(__data + (long)__kept * __stride, __en, __stride);");
                W(writer, "__kept++;");
                W(writer, "}");
                W(writer, "__buf.ResizeUninitialized(__kept);");
                W(writer, "__listed |= __kept != 0;");
                W(writer, "}");
                W(writer, "__active[__row] = __listed;");
                W(writer, "if (__buf.Capacity > " + info.Capacity + " && __buf.Length <= " + info.Capacity + ") __buf.TrimExcess();");
            }
            W(writer, "}");
            W(writer, "}");
            if (kind.IdleParams is not null)
                EmitIdle(writer, shape, kind, info);
            W(writer, "}");
        }

        static void EmitIdle(StringBuilder writer, Shape shape, KindDecl kind, UnityKindInfo info)
        {
            var idleArgs = CallArgs(shape, kind.IdleParams!, "", "__row", "");
            W(writer, "var __ia = chunk.GetEnableableBits(ref " + info.Name + "ActiveHandle);");
            W(writer, "var __ie = chunk.GetEnableableBits(ref " + info.Name + "EnabledHandle);");
            W(writer, "for (var __iw = 0; __iw < 2; __iw++)");
            W(writer, "{");
            W(writer, "if (__iw == 1 && __count <= 64) break;");
            W(writer, "var __ib = __iw == 0 ? ~__ia.ULong0 & __ie.ULong0 : ~__ia.ULong1 & __ie.ULong1;");
            W(writer, "while (__ib != 0)");
            W(writer, "{");
            W(writer, "var __row = (__iw << 6) + (int)global::Unity.Mathematics.math.tzcnt(__ib);");
            W(writer, "__ib &= __ib - 1;");
            W(writer, "if (__row >= __count) break;");
            W(writer, kind.TypeName + ".Idle(" + idleArgs + ");");
            W(writer, "}");
            W(writer, "}");
        }

        static List<(int index, SystemConsumer consumer)> EventConsumers(SystemAssignment assignment)
        {
            var list = new List<(int, SystemConsumer)>();
            for (var c = 0; c < assignment.Consumers.Count; c++)
            {
                var consumer = assignment.Consumers[c];
                if (consumer.IsMarker)
                    continue;
                foreach (var method in consumer.Methods)
                    if (UnityKindPlan.IsEventMethod(method))
                    {
                        list.Add((c, consumer));
                        break;
                    }
            }
            return list;
        }

        static ConsumerMethod? EventMethod(SystemConsumer consumer, ConsumerMethodKind kind)
        {
            foreach (var method in consumer.Methods)
                if (method.Kind == kind && UnityKindPlan.IsEventMethod(method))
                    return method;
            return null;
        }

        static string EventArgs(IReadOnlyList<UnityContext> events, ConsumerMethod method, int index, SystemConsumer consumer)
        {
            var args = new StringBuilder(IsTlFrame(method) ? "new global::Tl.TlFrame<" + consumer.ClipTypeName + ">(in __f" + index + ")" : "in __f" + index);
            for (var p = 1; p < method.Params.Count; p++)
            {
                var element = UnityKindPlan.EventElement(method.Params[p].TypeName);
                var at = -1;
                for (var e = 0; e < events.Count; e++)
                    if (events[e].TypeName == element)
                        at = e;
                args.Append(", ref __eb" + at);
            }
            return args.ToString();
        }

        static void EmitEventSteps(StringBuilder writer, IReadOnlyList<UnityContext> events, List<(int index, SystemConsumer consumer)> consumers, int assignment, bool closing)
        {
            W(writer, "for (var __i = 0; __i < __stage->StepCount; __i++)");
            W(writer, "{");
            W(writer, "var __def = __stage->Steps + __i;");
            W(writer, "if (__tick < __def->WindowStart || __tick >= __def->WindowEnd) continue;");
            foreach (var (index, consumer) in consumers)
            {
                var active = EventMethod(consumer, ConsumerMethodKind.ExecuteActive);
                var start = EventMethod(consumer, ConsumerMethodKind.OnClipStart);
                var end = EventMethod(consumer, ConsumerMethodKind.OnClipEnd);
                if (closing && end == null)
                    continue;
                W(writer, "if (__def->PairKey == " + consumer.PairKey + "UL)");
                W(writer, "{");
                EmitMaterialize(writer, consumer, assignment, index);
                if (closing)
                    W(writer, consumer.ConsumerTypeName + ".OnClipEnd(" + EventArgs(events, end!, index, consumer) + ");");
                else
                {
                    if (active != null)
                        W(writer, consumer.ConsumerTypeName + ".ExecuteActive(" + EventArgs(events, active, index, consumer) + ");");
                    if (start != null)
                        W(writer, "if (__tick == __def->WindowStart && (__pb->Flags & global::Tl.PlaybackFlags.ReenterPending) == 0) " + consumer.ConsumerTypeName + ".OnClipStart(" + EventArgs(events, start, index, consumer) + ");");
                    if (end != null)
                        W(writer, "if (__tick == __def->WindowEnd - 1 && (__pb->Flags & global::Tl.PlaybackFlags.ReenterPending) == 0) " + consumer.ConsumerTypeName + ".OnClipEnd(" + EventArgs(events, end, index, consumer) + ");");
                }
                W(writer, "}");
            }
            W(writer, "}");
        }

        static void EmitSlotHeader(StringBuilder writer, IReadOnlyList<UnityContext> events, List<(int index, SystemConsumer consumer)> consumers, bool closing)
        {
            ulong pairs = 0;
            foreach (var (_, consumer) in consumers)
                pairs |= 1ul << (int)(consumer.PairKey >> 58);
            if (!closing) W(writer, "var __pq = 0;");
            W(writer, "for (var __s = 0; __s < __n; __s++)");
            W(writer, "{");
            if (closing)
            {
                W(writer, "var __pb = __pbs + __s;");
                W(writer, "if (__pb->Timeline == 0 || (__pb->Flags & global::Tl.PlaybackFlags.Stopping) == 0) continue;");
            }
            else
            {
                W(writer, "var __ps = __pbs + __s;");
                W(writer, "if (__ps->Timeline == 0) continue;");
                W(writer, "var __pcopy = *__ps;");
                W(writer, "global::Tl.Unity.TimelineTicks.Decode(&__pcopy, (__clocks == null ? (ushort)0 : __clocks[__row]));");
                W(writer, "var __pb = &__pcopy; var __pk = 1; global::Tl.Playback* __pc = null;");
                W(writer, "if (__pcopy.Rate != 0 && (__pk = global::Tl.KindRules.CopiesOf(__pcs, __smp, ref __pq, in __pcopy, out __pc)) == 0 && global::Tl.PlaybackRules.Crossed(in __pcopy) != 0) __pk = 1;");
                W(writer, "for (var __j = 0; __j < __pk; __j++)");
                W(writer, "{");
                W(writer, "if (__pc != null) __pb = __pc + __j;");
                W(writer, "if ((__pb->Flags & (global::Tl.PlaybackFlags.Ended | global::Tl.PlaybackFlags.Paused | global::Tl.PlaybackFlags.Stopping)) != 0) continue;");
            }
            W(writer, "if (Index.LacksPairs(__pb->Timeline, " + pairs + "UL)) continue;");
            W(writer, "var __tl = global::Tl.LibraryViewReader.GetTimeline(in View, new global::Tl.TimelineId(__pb->Timeline));");
            W(writer, "if (__tl == null || __tl->Duration == 0) continue;");
            if (closing)
            {
                W(writer, "var __rt = global::Tl.Unity.TimelineTicks.Real(__pb, (__clocks == null ? (ushort)0 : __clocks[__row]));");
                W(writer, "var __tick = __rt < __tl->Duration ? __rt : (ushort)(__tl->Duration - 1);");
            }
            else
                W(writer, "var __tick = __pb->Tick;");
            W(writer, "var __stage = global::Tl.LibraryViewReader.StageAt(__tl, __tick);");
            W(writer, "if (__stage == null) continue;");
            W(writer, "var __backward = Forward ? (__pb->Flags & global::Tl.PlaybackFlags.Reverse) != 0 : (__pb->Flags & global::Tl.PlaybackFlags.Reverse) == 0;");
            if (closing)
                W(writer, "var __kflags = (__backward ? global::Tl.FrameFlags.Reverse : global::Tl.FrameFlags.None) | global::Tl.FrameFlags.Interrupted;");
            else
            {
                W(writer, "var __kflags = __backward ? global::Tl.FrameFlags.Reverse : global::Tl.FrameFlags.None;");
                W(writer, "if (((__pb->Flags & global::Tl.PlaybackFlags.StartPending) != 0 && __tick > 0) || (__pb->Flags & global::Tl.PlaybackFlags.ReenterPending) != 0) __kflags |= global::Tl.FrameFlags.Synthetic;");
            }
            W(writer, "if (__tick == 0) __kflags |= global::Tl.FrameFlags.TimelineStart;");
            W(writer, "if (__tick == (ushort)(__tl->Duration - 1)) __kflags |= global::Tl.FrameFlags.TimelineEnd;");
            for (var e = 0; e < events.Count; e++)
                W(writer, "__eb" + e + ".Bind(0, __s);");
        }

        static void EmitEvents(StringBuilder writer, IReadOnlyList<UnityContext> events, List<(int index, SystemConsumer consumer)> consumers, int assignment)
        {
            var closes = false;
            foreach (var (_, consumer) in consumers)
                closes |= EventMethod(consumer, ConsumerMethodKind.OnClipEnd) != null;
            W(writer, "void Events(in ArchetypeChunk chunk, BufferAccessor<global::Tl.Unity.Playback> __playbacks)");
            W(writer, "{");
            W(writer, "var __clocks = (ushort*)chunk.GetComponentDataPtrRO(ref ClockHandle);");
            W(writer, "var __copies = (global::Tl.Unity.TimelinePlaying*)chunk.GetComponentDataPtrRO(ref PlayingHandle);");
            for (var e = 0; e < events.Count; e++)
            {
                var type = events[e].TypeName;
                W(writer, "byte* __h" + e + " = stackalloc byte[global::Tl.Events<" + type + ">.HeaderBytes];");
                W(writer, "var __has" + e + " = chunk.Has(ref " + events[e].Name + "EventHandle);");
                W(writer, "var __acc" + e + " = __has" + e + " ? chunk.GetBufferAccessor(ref " + events[e].Name + "EventHandle) : default;");
            }
            W(writer, "for (var __row = 0; __row < chunk.Count; __row++)");
            W(writer, "{");
            W(writer, "var __slots = __playbacks[__row];");
            W(writer, "var __pbs = (global::Tl.Playback*)__slots.GetUnsafeReadOnlyPtr();");
            W(writer, "var __n = __slots.Length;");
            W(writer, "var __smp = __copies == null ? 0 : (int)__copies[__row].Copies; if (__n + __smp > __slots.Capacity) __smp = 0; var __pcs = __pbs + __n;");
            for (var e = 0; e < events.Count; e++)
            {
                var type = events[e].TypeName;
                W(writer, "var __o" + e + " = default(DynamicBuffer<global::Tl.Unity.TimelineEvent<" + type + ">>);");
                W(writer, "var __cap" + e + " = 0;");
                W(writer, "if (__has" + e + ") { __o" + e + " = __acc" + e + "[__row]; __o" + e + ".Clear(); __cap" + e + " = __o" + e + ".Capacity < 8 ? 8 : __o" + e + ".Capacity; }");
            }
            W(writer, "if (__n == 0) continue;");
            W(writer, "while (true)");
            W(writer, "{");
            for (var e = 0; e < events.Count; e++)
            {
                var type = events[e].TypeName;
                W(writer, "global::Tl.Events<" + type + "> __eb" + e + ";");
                W(writer, "if (__has" + e + ") { __o" + e + ".EnsureCapacity(__cap" + e + "); __eb" + e + " = new global::Tl.Events<" + type + ">(__h" + e + ", (global::Tl.EventRecord<" + type + ">*)__o" + e + ".GetUnsafePtr(), __o" + e + ".Capacity); }");
                W(writer, "else __eb" + e + " = new global::Tl.Events<" + type + ">(__h" + e + ", null, 0);");
            }
            if (closes)
            {
                EmitSlotHeader(writer, events, consumers, true);
                EmitEventSteps(writer, events, consumers, assignment, true);
                W(writer, "}");
            }
            EmitSlotHeader(writer, events, consumers, false);
            EmitEventSteps(writer, events, consumers, assignment, false);
            W(writer, "}");
            W(writer, "}");
            var retry = new StringBuilder();
            for (var e = 0; e < events.Count; e++)
            {
                W(writer, "if (__has" + e + " && __eb" + e + ".Dropped != 0) __cap" + e + " = __o" + e + ".Capacity * 2;");
                retry.Append(e == 0 ? "" : " || ").Append("(__has" + e + " && __eb" + e + ".Dropped != 0)");
            }
            W(writer, "if (" + retry + ") continue;");
            for (var e = 0; e < events.Count; e++)
                W(writer, "if (__has" + e + ") { var __count" + e + " = __eb" + e + ".Count; __eb" + e + ".Drain(); __o" + e + ".ResizeUninitialized(__count" + e + "); }");
            W(writer, "break;");
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
