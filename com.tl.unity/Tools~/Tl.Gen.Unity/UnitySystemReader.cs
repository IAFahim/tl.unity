using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tl.Gen.Unity
{
    internal static class UnitySystemReader
    {
        internal static UnitySystemModel Read(SemanticModel model, StructDeclarationSyntax syntax, CancellationToken cancel)
        {
            if (model.GetDeclaredSymbol(syntax, cancel) is not INamedTypeSymbol symbol)
                return null;
            var ns = symbol.ContainingNamespace is INamespaceSymbol space && !space.IsGlobalNamespace
                ? space.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : "global::Tl.Unity.Generated";
            var columns = new List<UnityColumn>();
            var contexts = new List<UnityContext>();
            var diagnostics = new List<UnityDiagnostic>();
            var errors = false;
            foreach (var member in symbol.GetMembers().OfType<IFieldSymbol>())
            {
                cancel.ThrowIfCancellationRequested();
                if (member.IsStatic || member.IsImplicitlyDeclared)
                    continue;
                if (IsSpan(member.Type, out var element, out var readOnly))
                {
                    if (element is null || !element.IsUnmanagedType)
                    {
                        diagnostics.Add(new UnityDiagnostic("TLUNITY9001", "Column '" + member.Name + "' must be Span<T> over an unmanaged element type.", true));
                        errors = true;
                        continue;
                    }
                    columns.Add(new UnityColumn(member.Name, element.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), readOnly));
                    continue;
                }
                if (!member.Type.IsUnmanagedType)
                {
                    diagnostics.Add(new UnityDiagnostic("TLUNITY9002", "Context '" + member.Name + "' must be unmanaged for Burst jobs.", true));
                    errors = true;
                    continue;
                }
                contexts.Add(new UnityContext(member.Name, member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }
            if (columns.Count == 0)
                diagnostics.Add(new UnityDiagnostic("TLUNITY9003", "System '" + symbol.Name + "' declares no Span<T> columns; the job advances playbacks only.", false));
            return new UnitySystemModel(ns, symbol.Name, columns, contexts, diagnostics, errors);
        }

        static bool IsSpan(ITypeSymbol type, out ITypeSymbol element, out bool readOnly)
        {
            if (type is INamedTypeSymbol named && named.IsGenericType && named.TypeArguments.Length == 1
                && named.ContainingNamespace != null && named.ContainingNamespace.ToDisplayString() == "System"
                && (named.MetadataName == "Span`1" || named.MetadataName == "ReadOnlySpan`1"))
            {
                element = named.TypeArguments[0];
                readOnly = named.MetadataName != "Span`1";
                return true;
            }
            element = null;
            readOnly = false;
            return false;
        }

        internal static Diagnostic ToDiagnostic(UnityDiagnostic diagnostic)
        {
            var descriptor = new DiagnosticDescriptor(
                diagnostic.Id,
                diagnostic.Id,
                diagnostic.Message,
                "Tl.Unity",
                diagnostic.IsError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                true);
            return Diagnostic.Create(descriptor, Location.None);
        }
    }
}
