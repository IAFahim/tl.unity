using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
            context.RegisterSourceOutput(systems, static (context, model) =>
            {
                if (model is null)
                    return;
                foreach (var diagnostic in model.Diagnostics)
                    context.ReportDiagnostic(UnitySystemReader.ToDiagnostic(diagnostic));
                if (!model.HasErrors)
                    context.AddSource(UnitySystemEmitter.FileName(model), UnitySystemEmitter.Emit(model));
            });
        }
    }
}
