using Microsoft.CodeAnalysis;
using Tl.Gen.CSharp.Model;

namespace Tl.Gen.Unity
{
    internal static class UnityKindPlan
    {
        internal static bool IsEventMethod(ConsumerMethod method)
        {
            if (method.Kind != ConsumerMethodKind.OnClipStart && method.Kind != ConsumerMethodKind.OnClipEnd && method.Kind != ConsumerMethodKind.ExecuteActive)
                return false;
            if (method.Params.Count < 2 || method.Params[0].Kind != ConsumerParamKind.Frame)
                return false;
            for (var p = 1; p < method.Params.Count; p++)
                if (method.Params[p].Kind != ConsumerParamKind.Events)
                    return false;
            return true;
        }

        internal static string EventElement(string typeName)
        {
            var start = typeName.IndexOf('<');
            var end = typeName.LastIndexOf('>');
            return start >= 0 && end > start ? typeName.Substring(start + 1, end - start - 1) : typeName;
        }

        internal static int AssignmentOf(IReadOnlyList<SystemAssignment> assignments, UnitySystemModel model)
        {
            var index = -1;
            for (var a = 0; a < assignments.Count; a++)
                if (assignments[a].System.TypeName == model.Namespace + "." + model.SystemTypeName || assignments[a].System.TypeName == model.SystemTypeName)
                    index = a;
            return index;
        }

        static IReadOnlyList<string> ColumnsOf(IReadOnlyList<SystemAssignment> assignments, IReadOnlyList<UnitySystemModel?> models, KindDecl kind)
        {
            foreach (var model in models)
            {
                if (model is null)
                    continue;
                var index = AssignmentOf(assignments, model);
                if (index < 0)
                    continue;
                foreach (var owned in assignments[index].Kinds)
                    if (owned.Name == kind.Name)
                        return UnityKindJobEmitter.ColumnsOf(model);
            }
            return new List<string>();
        }

        internal const int InlineBudgetBytes = 128;

        internal static IReadOnlyList<UnityKindInfo> Plan(Compilation compilation, IReadOnlyList<SystemAssignment> assignments, IReadOnlyList<UnitySystemModel?> models, IReadOnlyDictionary<string, int> inline, SourceProductionContext context)
        {
            var byName = new SortedDictionary<string, KindDecl>(StringComparer.Ordinal);
            foreach (var assignment in assignments)
            {
                foreach (var kind in assignment.Kinds)
                {
                    if (kind.Group.Length != 0)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            new DiagnosticDescriptor("TLUNITY9104", "Kind group not supported", "Kind '{0}' uses Group '{1}'; kind groups run on .NET only in this release and are skipped on Unity.", "Tl.Unity", DiagnosticSeverity.Warning, true),
                            Location.None, kind.Name, kind.Group));
                        continue;
                    }
                    byName[kind.TypeName] = kind;
                }
                foreach (var consumer in assignment.Consumers)
                    foreach (var method in consumer.Methods)
                        if (method.Kind != ConsumerMethodKind.Feeder && method.Kind != ConsumerMethodKind.Fold && !IsEventMethod(method))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(
                                new DiagnosticDescriptor("TLUNITY9105", "Legacy consumer method not dispatched", "Consumer '{0}' declares {1}, which Unity does not dispatch. Unity dispatches feeders, folds, and OnClipStart, OnClipEnd and ExecuteActive methods whose parameters after the Frame are all Events<T>. Feed a kind instead.", "Tl.Unity", DiagnosticSeverity.Warning, true),
                                Location.None, consumer.ConsumerTypeName, method.Kind.ToString()));
                        }
            }
            var infos = new List<UnityKindInfo>();
            foreach (var pair in byName)
            {
                var kind = pair.Value;
                var value = UnityLayout.Resolve(compilation, kind.ValueTypeName);
                if (value is null || !UnityLayout.TrySize(value, out var valueBytes, out _))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        new DiagnosticDescriptor("TLUNITY9102", "Kind value layout", "Kind '{0}' value type '{1}' needs a sequential unmanaged layout of primitives, enums and structs.", "Tl.Unity", DiagnosticSeverity.Error, true),
                        Location.None, kind.Name, kind.ValueTypeName));
                    continue;
                }
                var entryBytes = 8 + UnityLayout.Round(valueBytes, 4);
                infos.Add(new UnityKindInfo(kind,valueBytes, entryBytes, Capacity(compilation, kind, entryBytes, inline), (UnityKindEmitter.NamespaceOf(kind.TypeName) is { Length: > 0 } space ? space : "TlUnityGenerated"), UnityKindEmitter.Signature(assignments, kind), ColumnsOf(assignments, models, kind)));
            }
            return infos;
        }

        static string Capacity(Compilation compilation, KindDecl kind, int entryBytes, IReadOnlyDictionary<string, int> inline)
        {
            if (inline.TryGetValue(kind.TypeName, out var pinned))
                return pinned.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var symbol in compilation.GetSymbolsWithName("KindCapacity", SymbolFilter.Type))
            {
                if (symbol is not INamedTypeSymbol type)
                    continue;
                foreach (var member in type.GetMembers(kind.Name))
                    if (member is IFieldSymbol { IsConst: true })
                        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + kind.Name;
            }
            var computed = InlineBudgetBytes / entryBytes;
            return (computed < 1 ? 1 : computed).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
