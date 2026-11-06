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

        static void Generate(SourceProductionContext context, ImmutableArray<UnitySystemModel?> models, Compilation compilation)
        {
            var read = compilation is CSharpCompilation csharp
                ? SystemReader.Read(csharp)
                : ((IReadOnlyList<SystemDecl>)new List<SystemDecl>(), (IReadOnlyList<SystemAssignment>)new List<SystemAssignment>(), (IReadOnlyList<DeclarationDiagnostic>)new List<DeclarationDiagnostic>());
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
            var kinds = UnityKindPlan.Plan(compilation, assignments, models, context);
            var events = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var model in models)
            {
                if (model is null)
                    continue;
                var index = UnityKindPlan.AssignmentOf(assignments, model);
                if (index < 0 || assignments[index].Kinds.Count == 0)
                    continue;
                foreach (var evented in model.Events)
                    events.Add(evented.TypeName);
            }
            if (kinds.Count != 0)
            {
                var id = Sanitize(compilation.AssemblyName ?? "Game");
                context.AddSource("TlUnity.Kinds.g.cs", SourceText.From(UnityKindEmitter.EmitKinds(assignments, kinds, new List<string>(events), id), System.Text.Encoding.UTF8));
            }
            foreach (var model in models)
            {
                if (model is null)
                    continue;
                var index = UnityKindPlan.AssignmentOf(assignments, model);
                var own = new List<UnityKindInfo>();
                if (index >= 0)
                    foreach (var kind in assignments[index].Kinds)
                        foreach (var info in kinds)
                            if (info.Name == kind.Name)
                                own.Add(info);
                var text = own.Count == 0
                    ? UnitySystemEmitter.Emit(model)
                    : UnityKindJobEmitter.Emit(model, assignments[index], index, own);
                context.AddSource(UnitySystemEmitter.FileName(model), SourceText.From(text, System.Text.Encoding.UTF8));
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
