using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tl.Gen.Unity
{
    internal sealed class UnityAnalysis
    {
        public UnityAnalysis(CSharpCompilation compilation, UnitySystemModel? auto, IReadOnlyDictionary<string, int> inline, ISet<string> tlFrameFeeders)
        {
            Compilation = compilation;
            Auto = auto;
            Inline = inline;
            TlFrameFeeders = tlFrameFeeders;
        }

        public readonly CSharpCompilation Compilation;
        public readonly UnitySystemModel? Auto;
        public readonly IReadOnlyDictionary<string, int> Inline;
        public readonly ISet<string> TlFrameFeeders;
    }

    internal static class UnityNewStyle
    {
        internal const string AutoNamespace = "global::Tl.Generated";
        static readonly SymbolDisplayFormat Full = SymbolDisplayFormat.FullyQualifiedFormat;

        internal static string FeederKey(string consumer, string method)
        {
            return consumer + "." + method;
        }

        internal static UnityAnalysis Prepare(CSharpCompilation compilation, string id)
        {
            var inline = new Dictionary<string, int>(StringComparer.Ordinal);
            var feeders = new HashSet<string>(StringComparer.Ordinal);
            var tlKind = compilation.GetTypeByMetadataName("Tl.TlKindAttribute");
            var oldKind = compilation.GetTypeByMetadataName("Tl.TimelineKindAttribute");
            var tlFrame = compilation.GetTypeByMetadataName("Tl.TlFrame`1");
            var system = compilation.GetTypeByMetadataName("Tl.TimelineSystemAttribute");
            var feeds = compilation.GetTypeByMetadataName("Tl.FeedsAttribute");
            var pulse = compilation.GetTypeByMetadataName("Tl.PulseAttribute");
            var component = compilation.GetTypeByMetadataName("Unity.Entities.IComponentData");
            if (oldKind is null || system is null || feeds is null || pulse is null)
                return new UnityAnalysis(compilation, null, inline, feeders);
            var kinds = new List<INamedTypeSymbol>();
            var consumers = new List<INamedTypeSymbol>();
            var systemColumns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol type)
                        continue;
                    foreach (var attribute in type.GetAttributes())
                    {
                        if (Same(attribute.AttributeClass, oldKind) || Same(attribute.AttributeClass, tlKind))
                        {
                            kinds.Add(type);
                            foreach (var pair in attribute.NamedArguments)
                                if (pair.Key == "Inline" && pair.Value.Value is int value && value > 0)
                                    inline[type.ToDisplayString(Full)] = value;
                        }
                        if (Same(attribute.AttributeClass, system))
                            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
                                if (field.Type is INamedTypeSymbol { IsGenericType: true } span && (span.MetadataName == "Span`1" || span.MetadataName == "ReadOnlySpan`1"))
                                    systemColumns.Add(span.TypeArguments[0].ToDisplayString(Full));
                    }
                    foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                        foreach (var attribute in method.GetAttributes())
                            if (Same(attribute.AttributeClass, feeds) || Same(attribute.AttributeClass, pulse))
                            {
                                if (!consumers.Contains(type, SymbolEqualityComparer.Default))
                                    consumers.Add(type);
                                if (tlFrame is not null && method.Parameters.Length > 0 && method.Parameters[0].Type is INamedTypeSymbol first && Same(first.OriginalDefinition, tlFrame))
                                    feeders.Add(FeederKey(type.ToDisplayString(Full), method.Name));
                            }
                }
            }
            var autoKinds = new HashSet<string>(StringComparer.Ordinal);
            var columns = new List<(string type, bool writable)>();
            if (component is not null)
                foreach (var kind in kinds)
                {
                    var apply = kind.GetMembers("Apply").OfType<IMethodSymbol>().FirstOrDefault(static m => m.IsStatic && m.ReturnsVoid && m.Arity == 0);
                    if (apply is null || apply.Parameters.Length < 2)
                        continue;
                    var direct = true;
                    for (var i = 1; i < apply.Parameters.Length; i++)
                    {
                        var parameter = apply.Parameters[i];
                        if (!Implements(parameter.Type, component) || systemColumns.Contains(parameter.Type.ToDisplayString(Full)))
                            direct = false;
                    }
                    if (!direct)
                        continue;
                    autoKinds.Add(kind.ToDisplayString(Full));
                    for (var i = 1; i < apply.Parameters.Length; i++)
                        AddColumn(columns, apply.Parameters[i].Type.ToDisplayString(Full), apply.Parameters[i].RefKind == RefKind.Ref);
                }
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var consumer in consumers)
                foreach (var method in consumer.GetMembers().OfType<IMethodSymbol>())
                    foreach (var attribute in method.GetAttributes())
                    {
                        if (!Same(attribute.AttributeClass, feeds) && !Same(attribute.AttributeClass, pulse))
                            continue;
                        if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol fed || !autoKinds.Contains(fed.ToDisplayString(Full)))
                            continue;
                        members.Add(consumer.ToDisplayString(Full));
                        foreach (var parameter in method.Parameters)
                            if (component is not null && Implements(parameter.Type, component))
                                AddColumn(columns, parameter.Type.ToDisplayString(Full), parameter.RefKind == RefKind.Ref);
                    }
            var needsRewrite = feeders.Count != 0 || members.Count != 0 || HasAttribute(kinds, tlKind);
            var autoName = "TlAuto_" + id;
            var autoFull = AutoNamespace + "." + autoName;
            var result = compilation;
            if (needsRewrite)
            {
                foreach (var tree in compilation.SyntaxTrees)
                {
                    var model = compilation.GetSemanticModel(tree);
                    var rewriter = new Rewriter(model, tlKind, tlFrame, members, autoFull);
                    var root = tree.GetRoot();
                    var rewritten = rewriter.Visit(root);
                    if (rewriter.Changed)
                        result = result.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(rewritten, tree.Options));
                }
            }
            UnitySystemModel? auto = null;
            if (columns.Count != 0)
            {
                var text = new StringBuilder();
                text.Append("namespace Tl.Generated\n{\n[global::Tl.TimelineSystem]\npublic ref partial struct ").Append(autoName).Append("\n{\n");
                var unityColumns = new List<UnityColumn>();
                for (var i = 0; i < columns.Count; i++)
                {
                    var name = "C" + i + "_" + Simple(columns[i].type);
                    text.Append("public global::System.").Append(columns[i].writable ? "Span<" : "ReadOnlySpan<").Append(columns[i].type).Append("> ").Append(name).Append(";\n");
                    unityColumns.Add(new UnityColumn(name, columns[i].type, !columns[i].writable, true));
                }
                text.Append("}\n}\n");
                var options = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? CSharpParseOptions.Default;
                result = result.AddSyntaxTrees(CSharpSyntaxTree.ParseText(text.ToString(), options, "TlAuto.g.cs"));
                auto = new UnitySystemModel(AutoNamespace, autoName, unityColumns, new List<UnityContext>(), new List<UnityContext>(), new List<UnityDiagnostic>(), false);
            }
            return new UnityAnalysis(result, auto, inline, feeders);
        }

        static void AddColumn(List<(string type, bool writable)> columns, string type, bool writable)
        {
            for (var i = 0; i < columns.Count; i++)
                if (columns[i].type == type)
                {
                    if (writable && !columns[i].writable)
                        columns[i] = (type, true);
                    return;
                }
            columns.Add((type, writable));
        }

        static string Simple(string type)
        {
            var bare = type.StartsWith("global::", StringComparison.Ordinal) ? type.Substring(8) : type;
            var angle = bare.IndexOf('<');
            if (angle >= 0)
                bare = bare.Substring(0, angle);
            var dot = bare.LastIndexOf('.');
            var name = dot < 0 ? bare : bare.Substring(dot + 1);
            var builder = new StringBuilder();
            foreach (var c in name)
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            return builder.ToString();
        }

        static bool HasAttribute(List<INamedTypeSymbol> kinds, INamedTypeSymbol? attribute)
        {
            if (attribute is null)
                return false;
            foreach (var kind in kinds)
                foreach (var data in kind.GetAttributes())
                    if (Same(data.AttributeClass, attribute))
                        return true;
            return false;
        }

        static bool Implements(ITypeSymbol type, INamedTypeSymbol face)
        {
            if (type.TypeKind != TypeKind.Struct)
                return false;
            foreach (var candidate in type.AllInterfaces)
                if (Same(candidate, face))
                    return true;
            return false;
        }

        static bool Same(ISymbol? a, ISymbol? b)
        {
            return a is not null && b is not null && SymbolEqualityComparer.Default.Equals(a, b);
        }

        sealed class Rewriter : CSharpSyntaxRewriter
        {
            readonly SemanticModel _model;
            readonly INamedTypeSymbol? _tlKind;
            readonly INamedTypeSymbol? _tlFrame;
            readonly ISet<string> _members;
            readonly string _auto;

            public Rewriter(SemanticModel model, INamedTypeSymbol? tlKind, INamedTypeSymbol? tlFrame, ISet<string> members, string auto)
            {
                _model = model;
                _tlKind = tlKind;
                _tlFrame = tlFrame;
                _members = members;
                _auto = auto;
            }

            public bool Changed { get; private set; }

            public override SyntaxNode? VisitAttribute(AttributeSyntax node)
            {
                if (_tlKind is not null && _model.GetTypeInfo(node).Type is INamedTypeSymbol type && Same(type, _tlKind)
                    || _tlKind is not null && _model.GetSymbolInfo(node).Symbol is IMethodSymbol ctor && Same(ctor.ContainingType, _tlKind))
                {
                    Changed = true;
                    var kept = new List<AttributeArgumentSyntax>();
                    if (node.ArgumentList is not null)
                        foreach (var argument in node.ArgumentList.Arguments)
                            if (argument.NameEquals?.Name.Identifier.ValueText != "Inline")
                                kept.Add(argument);
                    var renamed = SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::Tl.TimelineKind"));
                    return kept.Count == 0 ? renamed : renamed.WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(kept)));
                }
                return base.VisitAttribute(node);
            }

            public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
            {
                if (_tlFrame is not null && _model.GetSymbolInfo(node).Symbol is INamedTypeSymbol type && Same(type.OriginalDefinition, _tlFrame) && type.TypeArguments.Length == 1)
                {
                    Changed = true;
                    var clip = type.TypeArguments[0].ToDisplayString(Full);
                    return SyntaxFactory.ParseTypeName("global::Tl.Frame<global::Tl.TlNoSettings<" + clip + ">, " + clip + ">").WithTriviaFrom(node);
                }
                return base.VisitGenericName(node);
            }

            public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
            {
                if (node.Right is GenericNameSyntax && _tlFrame is not null && _model.GetSymbolInfo(node).Symbol is INamedTypeSymbol type && Same(type.OriginalDefinition, _tlFrame) && type.TypeArguments.Length == 1)
                {
                    Changed = true;
                    var clip = type.TypeArguments[0].ToDisplayString(Full);
                    return SyntaxFactory.ParseTypeName("global::Tl.Frame<global::Tl.TlNoSettings<" + clip + ">, " + clip + ">").WithTriviaFrom(node);
                }
                return base.VisitQualifiedName(node);
            }

            public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
            {
                var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
                if (_model.GetDeclaredSymbol(node) is INamedTypeSymbol type && _members.Contains(type.ToDisplayString(Full)))
                {
                    Changed = true;
                    var member = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(
                        SyntaxFactory.ParseName("global::Tl.TimelineSystemMember"),
                        SyntaxFactory.ParseAttributeArgumentList("(typeof(" + _auto + "))"))));
                    return visited.AddAttributeLists(member);
                }
                return visited;
            }
        }
    }
}
