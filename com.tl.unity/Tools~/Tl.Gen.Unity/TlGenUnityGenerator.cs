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

        internal const string AutoNamespace = "global::Tl.Generated";

        static readonly string[] Markers = { "TimelineSystem", "TimelineKind", "TlKind", "TlClip", "TlState", "Feeds", "Pulse", "ITrack", "ITlTrack", "IMarker" };

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

        static void CollectStatesAndInline(CSharpCompilation compilation, List<INamedTypeSymbol> states, Dictionary<string, int> inline)
        {
            var tlState = compilation.GetTypeByMetadataName("Tl.TlStateAttribute");
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol type)
                        continue;
                    if (tlState is not null && type.TypeKind == TypeKind.Struct && !states.Contains(type, SymbolEqualityComparer.Default))
                        foreach (var a in type.GetAttributes())
                            if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, tlState)) { states.Add(type); break; }
                    foreach (var a in type.GetAttributes())
                        if (NewStyle.IsKindAttribute(a.AttributeClass))
                            foreach (var pair in a.NamedArguments)
                                if (pair.Key == "Inline" && pair.Value.Value is int value && value > 0)
                                    inline[type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)] = value;
                }
            }
        }

        static ImmutableArray<SystemTypeModel> FilterUnityStates(ImmutableArray<SystemTypeModel> models, CSharpCompilation compilation)
        {
            var tlState = compilation.GetTypeByMetadataName("Tl.TlStateAttribute");
            var component = compilation.GetTypeByMetadataName("Unity.Entities.IComponentData");
            var contextTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in models)
                if (model.Kind == SystemModelKind.System && model.System is not null)
                    foreach (var field in model.System.Fields)
                        if (field.Kind == SystemFieldKind.Context)
                        {
                            contextTypes.Add(field.ElementTypeName);
                            contextTypes.Add(field.TypeName);
                        }
            bool IsUnityStateName(string typeName)
            {
                if (contextTypes.Contains(typeName))
                    return false;
                var sym = UnityLayout.Resolve(compilation, typeName);
                if (sym is null || sym.TypeKind != TypeKind.Struct)
                    return false;
                if (tlState is not null)
                    foreach (var a in sym.GetAttributes())
                        if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, tlState))
                            return true;
                if (component is not null)
                    foreach (var face in sym.AllInterfaces)
                        if (SymbolEqualityComparer.Default.Equals(face, component))
                            return true;
                return false;
            }
            var result = ImmutableArray.CreateBuilder<SystemTypeModel>(models.Length);
            foreach (var model in models)
            {
                if (model.Kind == SystemModelKind.Kind && model.KindDecl is not null)
                {
                    var applyPlain = new List<bool>(model.KindDecl.ApplyParams.Count);
                    for (var i = 0; i < model.KindDecl.ApplyParams.Count; i++)
                    {
                        var isPlain = model.ApplyPlain is not null && i < model.ApplyPlain.Count && model.ApplyPlain[i];
                        applyPlain.Add(isPlain && IsUnityStateName(model.KindDecl.ApplyParams[i].TypeName));
                    }
                    List<bool>? idlePlain = null;
                    if (model.KindDecl.IdleParams is not null)
                    {
                        idlePlain = new List<bool>(model.KindDecl.IdleParams.Count);
                        for (var i = 0; i < model.KindDecl.IdleParams.Count; i++)
                        {
                            var isPlain = model.IdlePlain is not null && i < model.IdlePlain.Count && model.IdlePlain[i];
                            idlePlain.Add(isPlain && IsUnityStateName(model.KindDecl.IdleParams[i].TypeName));
                        }
                    }
                    result.Add(model with { ApplyPlain = applyPlain, IdlePlain = idlePlain });
                }
                else if (model.Kind == SystemModelKind.Feeder)
                {
                    IReadOnlyList<string>? filteredFacts = model.Facts;
                    if (model.Facts is { Count: > 1 })
                    {
                        var nextFacts = new List<string>(model.Facts.Count) { model.Facts[0] };
                        for (var i = 1; i < model.Facts.Count; i++)
                        {
                            var parts = model.Facts[i].Split('\0');
                            if (parts.Length == 3 && parts[1] == "1" && !IsUnityStateName(parts[0]))
                                nextFacts.Add(parts[0] + "\00\0" + parts[2]);
                            else
                                nextFacts.Add(model.Facts[i]);
                        }
                        filteredFacts = nextFacts;
                    }
                    var filteredDerived = model.DerivedColumn is not null && IsUnityStateName(model.DerivedColumn) ? model.DerivedColumn : null;
                    result.Add(model with { Facts = filteredFacts, DerivedColumn = filteredDerived });
                }
                else
                {
                    result.Add(model);
                }
            }
            return result.ToImmutable();
        }

        static void Generate(SourceProductionContext context, ImmutableArray<UnitySystemModel?> found, Compilation compilation)
        {
            if (compilation is not CSharpCompilation csharp || !Relevant(csharp))
                return;
            var id = Sanitize(compilation.AssemblyName ?? "Game");
            var key = NewStyle.FeedKey(compilation.AssemblyName ?? "");
            UnityMirrorEmitter.Emit(context, csharp);
            var rawStates = new List<INamedTypeSymbol>();
            var inline = new Dictionary<string, int>(StringComparer.Ordinal);
            CollectStatesAndInline(csharp, rawStates, inline);
            var states = UnityStateEmitter.Emit(context, rawStates, csharp);
            var models = new List<UnitySystemModel?>(found);
            var collected = SystemReader.Order(SystemReader.CollectModels(csharp), csharp, default);
            var read = SystemReader.AssignModels(FilterUnityStates(collected, csharp), id);
            var diagnostics = new List<DeclarationDiagnostic>(read.Diagnostics);
            Connections.Warn(csharp, diagnostics);
            UnitySystemModel? auto = null;
            if (read.AutoSource.Length > 0)
            {
                var autoName = NewStyle.AutoNameOf(id);
                SystemDecl? autoDecl = null;
                foreach (var s in read.Systems)
                    if (s.TypeName == AutoNamespace + "." + autoName) { autoDecl = s; break; }
                if (autoDecl is not null)
                {
                    var columns = new List<UnityColumn>();
                    var autoEvents = new List<UnityContext>();
                    foreach (var f in autoDecl.Fields)
                        if (f.Kind == SystemFieldKind.Events) autoEvents.Add(new UnityContext(f.Name, f.ElementTypeName));
                        else if (f.Kind is SystemFieldKind.Column or SystemFieldKind.ColumnReadOnly) columns.Add(new UnityColumn(f.Name, f.ElementTypeName, f.Kind == SystemFieldKind.ColumnReadOnly, true));
                    auto = new UnitySystemModel(AutoNamespace, autoName, columns, new List<UnityContext>(), autoEvents, new List<UnityDiagnostic>(), false);
                    models.Add(auto);
                }
            }
            var assignments = read.Assignments;
            var failed = false;
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Code == "TLGEN107")
                    continue;
                var info = diagnostic.Code == "TLGEN115";
                var warning = diagnostic.Code is "TLGEN98" or "TLGEN113" or "TLGEN114" or "TLGEN117";
                if (!warning && !info)
                    failed = true;
                context.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(diagnostic.Code, "Invalid timeline declaration", "{0}", "Tl.Generation", info ? DiagnosticSeverity.Info : warning ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, true),
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
            var derived = new List<Tl.Gen.CSharp.Model.KindDecl>();
            foreach (var assignment in assignments)
                foreach (var kind in assignment.Kinds)
                    if (kind.Derived != Tl.Gen.CSharp.Model.DerivedKind.None && !derived.Exists(d => d.Id == kind.Id))
                        derived.Add(kind);
            if (derived.Count != 0)
                context.AddSource("TlDerivedKinds.g.cs", SourceText.From(Tl.Gen.CSharp.DerivedKindsEmitter.Emit(derived), System.Text.Encoding.UTF8));
            var kinds = UnityKindPlan.Plan(compilation, assignments, models, inline, context);
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
            var fused = new List<UnityKindJobEmitter.FusedSystem>();
            var fusable = true;
            foreach (var model in models)
            {
                if (model is null || ReferenceEquals(model, auto))
                    continue;
                var index = UnityKindPlan.AssignmentOf(assignments, model);
                if (index < 0 || assignments[index].Kinds.Count == 0)
                    continue;
                var own = new List<UnityKindInfo>();
                foreach (var kind in assignments[index].Kinds)
                    foreach (var info in kinds)
                        if (info.Key == kind.Id && !info.Foreign)
                            own.Add(info);
                if (own.Count == 0)
                    continue;
                if ((fused.Count != 0 && model.Namespace != fused[0].Model.Namespace) || !UnityKindJobEmitter.Fusable(model, assignments[index], own))
                    fusable = false;
                fused.Add(new UnityKindJobEmitter.FusedSystem { Model = model, Assignment = assignments[index], AssignmentIndex = index, Kinds = own });
            }
            if (fusable && fused.Count != 0)
                context.AddSource(UnityKindJobEmitter.FusedFileName, SourceText.From(UnityKindJobEmitter.EmitFused(fused), System.Text.Encoding.UTF8));
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
                    if (own.Count == 0 && !isAuto && model.Events.Count != 0)
                        context.ReportDiagnostic(Diagnostic.Create(
                            new DiagnosticDescriptor("TLUNITY9109", "Events of a system without kinds do not run", "System '{0}' declares events but runs no kinds of its own; Unity does not run its event methods. Move its consumers out of the [TimelineSystem] struct so the generated system runs them, or give it a kind.", "Tl.Unity", DiagnosticSeverity.Warning, true),
                            Microsoft.CodeAnalysis.Location.None, model.SystemTypeName));
                    var text = own.Count == 0 && !autoEvents
                        ? UnitySystemEmitter.Emit(model)
                        : UnityKindJobEmitter.Emit(model, assignments[index], index, own);
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
                    context.AddSource(UnityFeedJobEmitter.FileName, SourceText.From(UnityFeedJobEmitter.Emit(model, assignments[index], index, fed, key), System.Text.Encoding.UTF8));
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
