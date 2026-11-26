using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Tl.Gen.CSharp.Analysis;

namespace Tl.Gen.Plain
{
    [Generator(LanguageNames.CSharp)]
    public sealed class TlGenPlainGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValueProvider<int> Count(string name, bool method) => context.SyntaxProvider.ForAttributeWithMetadataName(
                name,
                method ? static (node, _) => node is MethodDeclarationSyntax : static (node, _) => node is TypeDeclarationSyntax,
                static (_, _) => true).Collect().Select(static (found, _) => found.Length);
            var tracks = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax syntax && (syntax.Identifier.ValueText.StartsWith("TlCompiled_", StringComparison.Ordinal) || syntax.BaseList is not null && syntax.BaseList.Types.Any(static candidate => Base(candidate.Type) is "ITlTrack" or "ITrack" or "IMarker")),
                static (_, _) => true).Collect().Select(static (found, _) => found.Length);
            var declarations = Count("Tl.TimelineSystemAttribute", false)
                .Combine(Count("Tl.TlKindAttribute", false)).Select(static (pair, _) => pair.Left + pair.Right)
                .Combine(Count("Tl.TimelineKindAttribute", false)).Select(static (pair, _) => pair.Left + pair.Right)
                .Combine(Count("Tl.TlStateAttribute", false)).Select(static (pair, _) => pair.Left + pair.Right)
                .Combine(Count("Tl.FeedsAttribute", true)).Select(static (pair, _) => pair.Left + pair.Right)
                .Combine(Count("Tl.PulseAttribute", true)).Select(static (pair, _) => pair.Left + pair.Right)
                .Combine(tracks).Select(static (pair, _) => pair.Left + pair.Right);
            var combined = declarations.Combine(context.CompilationProvider);
            context.RegisterSourceOutput(combined, static (production, pair) => Generate(production, pair.Left, pair.Right));
        }

        static string Base(TypeSyntax type) => type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => "",
        };

        static void Generate(SourceProductionContext context, int declarations, Compilation compilation)
        {
            if (declarations == 0 || compilation is not CSharpCompilation csharp || csharp.AssemblyName is "Tl.Core" or "Tl.Unity")
                return;
            var id = Sanitize(csharp.AssemblyName ?? "Game");
            var emitted = Tl.Gen.CSharp.SystemGeneration.Emit(csharp, id);
            foreach (var diagnostic in emitted.Diagnostics)
                context.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(diagnostic.Code, "Invalid timeline declaration", "{0}", "Tl.Generation", diagnostic.Code == "TLGEN115" ? DiagnosticSeverity.Info : diagnostic.Code is "TLGEN98" or "TLGEN113" or "TLGEN114" ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, true),
                    Location(diagnostic), diagnostic.Message));
            if (emitted.Artifacts.Count == 0)
                return;
            foreach (var artifact in emitted.Artifacts)
                context.AddSource(artifact.RelativePath, SourceText.From(artifact.Content.Replace("\r\n", "\n"), new UTF8Encoding(false)));
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
