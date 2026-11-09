using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Tl.Gen.CSharp.Analysis;
using Tl.Gen.CSharp.Model;

namespace Tl.Gen.Unity
{
    [Generator]
    public sealed class TlGenUnityGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var systems = context.SyntaxProvider.ForAttributeWithMetadataName(
                "Tl.TimelineSystemAttribute",
                static (node, _) => node is StructDeclarationSyntax,
                static (generatorContext, token) => UnitySystemReader.Read(generatorContext.SemanticModel, (StructDeclarationSyntax)generatorContext.TargetNode, token));
            var combined = systems.Collect().Combine(context.CompilationProvider);
            context.RegisterSourceOutput(combined, static (production, pair) => Generate(production, pair.Left, pair.Right));
        }

        static readonly string[] Markers = { "TimelineSystem", "TimelineKind", "TlKind", "TlClip", "TlColumn", "Feeds", "Pulse", "ITrack", "ITlTrack", "IMarker" };

        static bool Relevant(CSharpCompilation compilation)
        {
            if (compilation.AssemblyName == "Tl.Unity" || compilation.AssemblyName == "Tl.Core")
                return false;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var name = System.IO.Path.GetFileName(tree.FilePath ?? "");
                if (name.StartsWith("TlUnity.", StringComparison.Ordinal) && name.EndsWith(".g.cs", StringComparison.Ordinal))
                    return false;
            }
            foreach (var tree in compilation.SyntaxTrees)
            {
                var text = tree.GetText().ToString();
                foreach (var marker in Markers)
                    if (text.IndexOf(marker, StringComparison.Ordinal) >= 0)
                        return true;
            }
            return false;
        }

        static Func<ITypeSymbol, bool> StateRule(Compilation compilation)
        {
            var component = compilation.GetTypeByMetadataName("Unity.Entities.IComponentData");
            var state = compilation.GetTypeByMetadataName("Tl.TlColumnAttribute");
            return type =>
            {
                if (NewStyle.IsStateType(type, state))
                    return true;
                if (component is null || type.TypeKind != TypeKind.Struct)
                    return false;
                foreach (var face in type.AllInterfaces)
                    if (SymbolEqualityComparer.Default.Equals(face, component))
                        return true;
                return false;
            };
        }

        static void Generate(SourceProductionContext context, ImmutableArray<UnitySystemModel?> found, Compilation compilation)
        {
            if (compilation is not CSharpCompilation csharp || !Relevant(csharp))
                return;
            var id = Sanitize(compilation.AssemblyName ?? "Game");
            var key = NewStyle.FeedKey(compilation.AssemblyName ?? "");
            UnityMirrorEmitter.Emit(context, csharp);
            var analysis = NewStyle.Prepare(csharp, id, StateRule(csharp));
            var states = UnityStateEmitter.Emit(context, analysis.States, csharp);
            var models = new List<UnitySystemModel?>(found);
            UnitySystemModel? auto = null;
            if (analysis.HasAuto)
            {
                var columns = new List<UnityColumn>();
                foreach (var column in analysis.AutoColumns)
                    columns.Add(new UnityColumn(column.Name, column.TypeName, !column.Writable, true));
                var autoEvents = new List<UnityContext>();
                foreach (var evented in analysis.AutoEvents)
                    autoEvents.Add(new UnityContext(evented.Name, evented.TypeName));
                auto = new UnitySystemModel(NewStyle.AutoNamespace, analysis.AutoName, columns, new List<UnityContext>(), autoEvents, new List<UnityDiagnostic>(), false);
                models.Add(auto);
            }
            var read = SystemReader.Read(analysis.Compilation);
            var assignments = read.Item2;
            var failed = false;
            foreach (var diagnostic in read.Item3)
            {
                if (diagnostic.Code != "TLGEN98")
                    failed = true;
                context.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(diagnostic.Code, "Invalid timeline declaration", "{0}", "Tl.Generation", diagnostic.Code == "TLGEN98" ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, true),
                    Location(diagnostic), diagnostic.Message));
            }
            foreach (var model in models)
            {
                if (model is null)
                    continue;
                foreach (var diagnostic in model.Diagnostics)
                    context.ReportDiagnostic(UnitySystemReader.ToDiagnostic(diagnostic));
                if (model.HasErrors)
                    failed = true;
            }
            if (failed)
                return;
            var kinds = UnityKindPlan.Plan(compilation, assignments, models, analysis.Inline, context);
            var events = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var model in models)
            {
                if (model is null)
                    continue;
                var index = UnityKindPlan.AssignmentOf(assignments, model);
                if (index < 0 || assignments[index].Kinds.Count == 0 && !ReferenceEquals(model, auto))
                    continue;
                foreach (var evented in model.Events)
                    events.Add(evented.TypeName);
            }
            var components = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var state in states)
                components.Add(state);
            foreach (var element in events)
                components.Add("global::Tl.Unity.TimelineEvent<" + element + ">");
            foreach (var kind in kinds)
            {
                if (kind.Foreign)
                    continue;
                components.Add(kind.Entry);
                components.Add(kind.Active);
                components.Add(kind.Enabled);
            }
            foreach (var model in models)
                if (model is not null)
                    foreach (var column in UnityKindJobEmitter.OwnedColumnsOf(model))
                        components.Add(column);
            if (components.Count != 0)
                context.AddSource(UnityRegistrationEmitter.FileName, SourceText.From(UnityRegistrationEmitter.Emit(components, id), System.Text.Encoding.UTF8));
            if (kinds.Count != 0 || events.Count != 0)
                context.AddSource("TlUnity.KindTypes.g.cs", SourceText.From(UnityKindEmitter.EmitKinds(assignments, kinds, new List<string>(events), id, key), System.Text.Encoding.UTF8));
            foreach (var model in models)
            {
                if (model is null)
                    continue;
                var index = UnityKindPlan.AssignmentOf(assignments, model);
                var own = new List<UnityKindInfo>();
                var fed = new List<UnityKindInfo>();
                if (index >= 0)
                    foreach (var kind in assignments[index].Kinds)
                        foreach (var info in kinds)
                            if (info.Key == kind.Id)
                                (info.Foreign ? fed : own).Add(info);
                var isAuto = ReferenceEquals(model, auto);
                var autoEvents = isAuto && index >= 0 && model.Events.Count != 0;
                if (own.Count != 0 || !isAuto || autoEvents)
                {
                    var text = own.Count == 0 && !autoEvents
                        ? UnitySystemEmitter.Emit(model)
                        : UnityKindJobEmitter.Emit(model, assignments[index], index, own, analysis.TlFrameFeeders);
                    context.AddSource(UnitySystemEmitter.FileName(model), SourceText.From(text, System.Text.Encoding.UTF8));
                    if (isAuto)
                        context.AddSource(UnityAutoSystemEmitter.FileName, SourceText.From(UnityAutoSystemEmitter.Emit(model), System.Text.Encoding.UTF8));
                }
                if (fed.Count != 0)
                {
                    if (!isAuto)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            new DiagnosticDescriptor("TLUNITY9106", "Kind from another assembly fed by a declared system", "System '{0}' feeds kinds declared in another assembly; feed them from consumers outside [TimelineSystem] structs, which run in the generated feed system.", "Tl.Unity", DiagnosticSeverity.Error, true),
                            Microsoft.CodeAnalysis.Location.None, model.SystemTypeName));
                        continue;
                    }
                    context.AddSource(UnityFeedJobEmitter.FileName, SourceText.From(UnityFeedJobEmitter.Emit(model, assignments[index], index, fed, analysis.TlFrameFeeders, key), System.Text.Encoding.UTF8));
                }
            }
        }

        static string Sanitize(string name)
        {
            var chars = new char[name.Length];
            for (var i = 0; i < name.Length; i++)
                chars[i] = char.IsLetterOrDigit(name[i]) ? name[i] : '_';
            return new string(chars);
        }

        static Location Location(DeclarationDiagnostic diagnostic)
        {
            return diagnostic.SpanStart < 0 || diagnostic.File == null
                ? Microsoft.CodeAnalysis.Location.None
                : Microsoft.CodeAnalysis.Location.Create(
                    diagnostic.File,
                    new TextSpan(diagnostic.SpanStart, diagnostic.SpanLength),
                    new LinePositionSpan(
                        new LinePosition(diagnostic.Line - 1, diagnostic.Column - 1),
                        new LinePosition(diagnostic.EndLine - 1, diagnostic.EndColumn - 1)));
        }
    }
}
