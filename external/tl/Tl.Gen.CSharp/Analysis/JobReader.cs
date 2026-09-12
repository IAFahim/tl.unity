using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Tl.Gen.CSharp.Model;

namespace Tl.Gen.CSharp.Analysis;

public static class JobReader
{
    private sealed record Contracts(
        INamedTypeSymbol Timeline, INamedTypeSymbol Catalog, INamedTypeSymbol Job,
        INamedTypeSymbol Hook, INamedTypeSymbol Frame, INamedTypeSymbol TimelineFrame,
        INamedTypeSymbol Builder, INamedTypeSymbol CatalogBuilder,
        INamedTypeSymbol TrackRef, INamedTypeSymbol SchemaBuilder, INamedTypeSymbol Blend);
    private sealed record Entry(INamedTypeSymbol Type, TypeDeclarationSyntax Syntax);
    private sealed record Binding(JobTrack Track, INamedTypeSymbol Clip, SyntaxNode Declaration);

    public static JobReadResult Read(CSharpCompilation compilation)
    {
        if (compilation is null)
            throw new ArgumentNullException(nameof(compilation));
        var errors = new List<DeclarationDiagnostic>();
        var contracts = Contract(compilation);
        if (contracts is null)
        {
            var site = compilation.SyntaxTrees.SelectMany(static tree => tree.GetRoot().DescendantNodes())
                .FirstOrDefault(static node => node.ToString().Contains("ITimeline", StringComparison.Ordinal));
            if (site is not null)
                Error(errors, site, "TLGEN60", "The exact Tl job declaration contracts could not be resolved.");
            return new([], [], errors);
        }
        var entries = Entries(compilation).OrderBy(static entry => Name(entry.Type), StringComparer.Ordinal).ToArray();
        var declarations = new Dictionary<INamedTypeSymbol, Entry>(SymbolEqualityComparer.Default);
        foreach (var entry in entries.Where(entry => Implements(entry.Type, contracts.Timeline)))
            declarations.Add(entry.Type, entry);
        var reader = new Reader(compilation, contracts, declarations, errors);
        var timelines = declarations.Values.Where(JobSyntax).Select(entry => reader.Timeline(entry.Type, entry.Syntax))
            .Where(static item => item is not null).Cast<JobTimeline>().OrderBy(static item => item.Namespace, StringComparer.Ordinal)
            .ThenBy(static item => item.Name, StringComparer.Ordinal).ToArray();
        var catalogs = entries.Where(entry => Implements(entry.Type, contracts.Catalog)).Select(reader.Catalog)
            .Where(static item => item is not null).Cast<JobCatalog>().OrderBy(static item => item.Namespace, StringComparer.Ordinal)
            .ThenBy(static item => item.Name, StringComparer.Ordinal).ToArray();
        var consumers = new List<JobConsumer>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in timelines.SelectMany(static t => t.Tracks))
        {
            seen.Add(track.Job.TypeName);
            if (pairs.Add(PairKey(track.Job.TypeName, track.TypeName, track.ClipTypeName)))
                consumers.Add(new(track.TypeName, track.ClipTypeName, track.Job));
        }
        foreach (var entry in entries)
        {
            if (entry.Type.TypeKind == TypeKind.Interface) continue;
            if (seen.Contains(Name(entry.Type))) continue;
            var markers = entry.Type.AllInterfaces.Where(item => Same(item.OriginalDefinition, contracts.Job)).ToArray();
            if (markers.Length == 0) continue;
            void Err(string msg) => Error(errors, entry.Syntax, "TLGEN65", $"Job '{Name(entry.Type)}' {msg}");
            if (entry.Type.IsAbstract) { Err("cannot be abstract."); continue; }
            if (entry.Type.Arity > 0 || entry.Type.TypeParameters.Length > 0) { Err("cannot be generic; open type parameters cannot be registered as timeline jobs."); continue; }
            var discovered = new List<JobConsumer>();
            var valid = true;
            foreach (var marker in markers)
            {
                var track = marker.TypeArguments[0];
                var clip = marker.TypeArguments[1];
                if (track.TypeKind == TypeKind.TypeParameter || clip.TypeKind == TypeKind.TypeParameter) { Err("cannot have open type parameters for its track or clip pairing."); valid = false; continue; }
                var ok = true;
                if (!track.IsUnmanagedType) { Err($"track type '{Name(track)}' must be an unmanaged type."); ok = false; }
                if (!clip.IsUnmanagedType) { Err($"clip type '{Name(clip)}' must be an unmanaged type."); ok = false; }
                if (ok && !track.AllInterfaces.Any(item => Same(item.OriginalDefinition, contracts.Blend) && item.TypeArguments.Length == 1 && Same(item.TypeArguments[0], clip))) { Err($"track type '{Name(track)}' must implement Tl.IBlend<{Name(clip)}>."); ok = false; }
                if (!ok) { valid = false; continue; }
                var definition = reader.Execute(entry.Type, contracts.Frame.Construct(track, clip), compilation.Assembly, entry.Syntax);
                if (definition is null) { valid = false; continue; }
                discovered.Add(new(Name(track), Name(clip), definition));
            }
            if (!valid) continue;
            foreach (var consumer in discovered.OrderBy(static item => item.TrackTypeName + "\0" + item.ClipTypeName, StringComparer.Ordinal))
                if (pairs.Add(PairKey(consumer.Job.TypeName, consumer.TrackTypeName, consumer.ClipTypeName)))
                    consumers.Add(consumer);
        }
        return new(timelines, catalogs, errors) { Consumers = consumers };
    }

    private sealed class Reader(
        CSharpCompilation compilation, Contracts contracts,
        IReadOnlyDictionary<INamedTypeSymbol, Entry> declarations,
        List<DeclarationDiagnostic> errors)
    {
        private readonly Dictionary<INamedTypeSymbol, JobTimeline> _read = new(SymbolEqualityComparer.Default);
        private readonly List<INamedTypeSymbol> _stack = [];

        public JobTimeline? Timeline(INamedTypeSymbol type, SyntaxNode site)
        {
            if (_read.TryGetValue(type, out var prior))
                return prior;
            var cycle = _stack.FindIndex(item => Same(item, type));
            if (cycle >= 0)
            {
                Error(errors, site, "TLGEN71", $"Timeline include cycle: {string.Join(" -> ", _stack.Skip(cycle).Append(type).Select(Name))}.");
                return null;
            }
            if (!declarations.TryGetValue(type, out var entry))
            {
                Error(errors, site, "TLGEN70", $"Timeline '{Name(type)}' was not found in this compilation.");
                return null;
            }
            if (!Declaration(entry, "timeline", "TLGEN61") || Define(entry, contracts.Builder, "timeline", "TLGEN62") is not { } define)
                return null;
            _stack.Add(type);
            var syntax = Method(define)!;
            var model = compilation.GetSemanticModel(syntax.SyntaxTree);
            var tracks = new List<JobTrack>();
            var clips = new List<HeterogeneousClip>();
            var before = new List<JobDefinition>();
            var after = new List<JobDefinition>();
            var locals = new Dictionary<ILocalSymbol, Binding>(SymbolEqualityComparer.Default);
            JobTimeline? included = null;
            var loops = false;
            var valid = true;
            foreach (var statement in syntax.Body!.Statements)
            {
                if (statement is LocalDeclarationStatementSyntax local && local.Declaration.Variables.Count == 1)
                {
                    var variable = local.Declaration.Variables[0];
                    var binding = Track(variable, model, define.Parameters[0], type, tracks.Count);
                    if (binding is null || model.GetDeclaredSymbol(variable) is not ILocalSymbol symbol || locals.ContainsKey(symbol))
                    {
                        Error(errors, variable, "TLGEN63", "A unique timeline local must bind builder.Track(settings).Use<TJob>().");
                        valid = false;
                    }
                    else
                    {
                        tracks.Add(binding.Track);
                        locals.Add(symbol, binding);
                    }
                    continue;
                }
                if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call })
                {
                    Error(errors, statement, "TLGEN63", "Timeline Define contains an unsupported statement.");
                    valid = false;
                    continue;
                }
                if (BuilderCall(call, model, define.Parameters[0], "Clip"))
                    valid &= Clip(call, model, locals, clips);
                else if (BuilderCall(call, model, define.Parameters[0], "Looping") && call.ArgumentList.Arguments.Count == 0)
                {
                    if (loops)
                    {
                        Error(errors, call, "TLGEN69", "Looping may be declared only once.");
                        valid = false;
                    }
                    loops = true;
                }
                else if (BuilderGeneric(call, model, define.Parameters[0], "Before", out var hook))
                    valid &= AddHook(hook, type, call, before);
                else if (BuilderGeneric(call, model, define.Parameters[0], "After", out hook))
                    valid &= AddHook(hook, type, call, after);
                else if (BuilderGeneric(call, model, define.Parameters[0], "Include", out var nested))
                {
                    if (included is not null || !Implements(nested, contracts.Timeline) || (included = Timeline(nested, call)) is null)
                    {
                        Error(errors, call, "TLGEN70", "Include requires one source timeline and may appear only once.");
                        valid = false;
                    }
                    else
                    {
                        var offset = tracks.Count;
                        tracks.AddRange(included.Tracks.Select(track => track with { Index = track.Index + offset }));
                        clips.AddRange(included.Clips.Select(clip => clip with { TrackIndex = clip.TrackIndex + offset }));
                    }
                }
                else
                {
                    Error(errors, call, "TLGEN63", "Timeline Define contains an unsupported Tl.Builder call.");
                    valid = false;
                }
            }
            _stack.RemoveAt(_stack.Count - 1);
            if (included is not null)
            {
                before.AddRange(included.Before);
                after.InsertRange(0, included.After);
                if (loops && !included.Loops)
                {
                    Error(errors, syntax, "TLGEN70", "Included timelines have incompatible looping modes.");
                    valid = false;
                }
                loops |= included.Loops;
            }
            foreach (var binding in locals.Values.Where(binding => clips.All(clip => clip.TrackIndex != binding.Track.Index)))
            {
                Error(errors, binding.Declaration, "TLGEN68", $"Track {binding.Track.Index} must declare at least one clip.");
                valid = false;
            }
            if (tracks.Count > 256 || !Overlaps(tracks.Count, clips))
            {
                Error(errors, syntax, "TLGEN68", tracks.Count > 256 ? "A timeline may contain at most 256 authored tracks." : "A track may have at most two clips active at once.");
                valid = false;
            }
            var slotConflict = tracks.Select(static track => track.Job).Concat(before).Concat(after).SelectMany(static job => job.Slots)
                .GroupBy(static slot => slot.Name, StringComparer.Ordinal)
                .FirstOrDefault(static group => group.Select(slot => slot.TypeName).Distinct(StringComparer.Ordinal).Skip(1).Any());
            if (slotConflict is not null)
            {
                Error(errors, syntax, "TLGEN67", $"Slot '{slotConflict.Key}' has incompatible type declarations.");
                valid = false;
            }
            var duration = clips.Count == 0 ? 0u : clips.Max(static clip => clip.End);
            if (loops && included is { Loops: true } && included.Duration != duration)
            {
                Error(errors, syntax, "TLGEN70", "A looping include must have the same duration as the flattened timeline.");
                valid = false;
            }
            if (!valid)
                return null;
            var result = new JobTimeline(type.Name, Space(type), loops, [], tracks, clips, before, after);
            _read.Add(type, result);
            return result;
        }

        private Binding? Track(VariableDeclaratorSyntax variable, SemanticModel model, IParameterSymbol builder, INamedTypeSymbol owner, int index)
        {
            if (variable.Initializer?.Value is not InvocationExpressionSyntax use
                || use.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax generic, Expression: InvocationExpressionSyntax track }
                || generic.Identifier.ValueText != "Use" || generic.TypeArgumentList.Arguments.Count != 1 || use.ArgumentList.Arguments.Count != 0
                || track.ArgumentList.Arguments.Count != 1 || !BuilderCall(track, model, builder, "Track")
                || !On(use, model, "Use", contracts.TrackRef)
                || model.GetTypeInfo(generic.TypeArgumentList.Arguments[0]).Type is not INamedTypeSymbol job)
                return null;
            var expression = track.ArgumentList.Arguments[0].Expression;
            var info = model.GetTypeInfo(expression);
            if (ResolvedType(info.ConvertedType, info.Type) is not INamedTypeSymbol settings || !settings.IsUnmanagedType || !Bounded(expression, model))
            {
                Error(errors, expression, "TLGEN64", "Track settings must be an unmanaged bounded constructor, literal, or constant expression.");
                return null;
            }
            var markers = job.AllInterfaces.Where(item => Same(item.OriginalDefinition, contracts.Job)).ToArray();
            var matches = markers.Where(item => Same(item.TypeArguments[0], settings)).ToArray();
            var markerTrack = matches.Length == 1 ? matches[0].TypeArguments[0] : null;
            var payload = matches.Length == 1 ? matches[0].TypeArguments[1] as INamedTypeSymbol : null;
            if (!job.IsUnmanagedType || matches.Length != 1 || markerTrack is null || payload is null || !payload.IsUnmanagedType)
            {
                Error(errors, use, "TLGEN65", $"Job '{Name(job)}' must implement exactly one Tl.ITimelineJob<{Name(settings)}, TClip> pairing.");
                return null;
            }
            var definition = Execute(job, contracts.Frame.Construct(settings, payload), owner, use);
            return definition is null ? null : new(new(index, Name(settings), Name(payload), Canonical(expression, model), definition), payload, variable);
        }

        private bool AddHook(INamedTypeSymbol type, INamedTypeSymbol owner, SyntaxNode site, ICollection<JobDefinition> target)
        {
            if (!type.IsUnmanagedType || !Implements(type, contracts.Hook))
            {
                Error(errors, site, "TLGEN66", $"Hook '{Name(type)}' must be unmanaged and implement Tl.IHook.");
                return false;
            }
            var hook = Execute(type, contracts.TimelineFrame, owner, site);
            if (hook is not null)
                target.Add(hook);
            return hook is not null;
        }

        public JobDefinition? Execute(INamedTypeSymbol type, ITypeSymbol frame, ISymbol owner, SyntaxNode site)
        {
            var candidates = type.GetMembers("Execute").OfType<IMethodSymbol>().Where(method => !method.IsImplicitlyDeclared
                && method.IsStatic && method.ReturnsVoid && method.Arity == 0 && method.MethodKind == MethodKind.Ordinary
                && method.Parameters.Length > 0 && method.Parameters[0].RefKind == RefKind.In
                && Same(method.Parameters[0].Type, frame)
                && compilation.IsSymbolAccessibleWithin(method, owner)).ToArray();
            var method = candidates.Length == 1 ? candidates[0] : null;
            if (method is null)
            {
                Error(errors, site, "TLGEN66", $"'{Name(type)}' must declare one accessible static void Execute beginning with in {Name(frame)}.");
                return null;
            }
            var slots = new List<TimelineSlot>();
            foreach (var parameter in method.Parameters.Skip(1))
            {
                if (parameter.RefKind is not (RefKind.In or RefKind.Ref) || parameter.IsOptional || parameter.IsParams || !parameter.Type.IsUnmanagedType)
                {
                    Error(errors, Site(parameter, site), "TLGEN67", $"Every gameplay parameter of '{Name(type)}.Execute' must be a required unmanaged in or ref parameter; out is unsupported in alpha.3.");
                    return null;
                }
                slots.Add(new(parameter.Name, Name(parameter.Type), parameter.RefKind == RefKind.In ? SlotMode.Input : SlotMode.Reference));
            }
            return new(Name(type), slots);
        }

        private bool Clip(InvocationExpressionSyntax call, SemanticModel model, IReadOnlyDictionary<ILocalSymbol, Binding> locals, ICollection<HeterogeneousClip> clips)
        {
            var args = Args(call, model, 4);
            if (args is null || model.GetSymbolInfo(args[0]).Symbol is not ILocalSymbol local || !locals.TryGetValue(local, out var binding))
            {
                Error(errors, call, "TLGEN68", "Clip requires a track local declared earlier in Define.");
                return false;
            }
            var info = model.GetTypeInfo(args[1]);
            var type = ResolvedType(info.ConvertedType, info.Type);
            if (!Same(type, binding.Clip) || !Bounded(args[1], model) || !Unsigned(args[2], model, out var start)
                || !Unsigned(args[3], model, out var end) || start >= end)
            {
                Error(errors, call, "TLGEN68", $"Clip requires a bounded '{Name(binding.Clip)}' payload and constant uint bounds with start less than end.");
                return false;
            }
            clips.Add(new(binding.Track.Index, Name(binding.Clip), Canonical(args[1], model), start, end));
            return true;
        }

        public JobCatalog? Catalog(Entry entry)
        {
            if (!Declaration(entry, "catalog", "TLGEN72") || Define(entry, contracts.CatalogBuilder, "catalog", "TLGEN73") is not { } define)
                return null;
            var syntax = Method(define)!;
            var model = compilation.GetSemanticModel(syntax.SyntaxTree);
            var schemas = new List<JobSchema>();
            var schemaNames = new HashSet<string>(StringComparer.Ordinal);
            var assetNames = new HashSet<string>(StringComparer.Ordinal);
            var assetMembers = new Dictionary<string, string>(StringComparer.Ordinal);
            var valid = true;
            foreach (var statement in syntax.Body!.Statements)
            {
                if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call }
                    || !Schema(call, model, define.Parameters[0], out var marker, out var assets))
                {
                    Error(errors, statement, "TLGEN74", "Catalog Define requires builder.Schema<T>().Asset<Timeline>() chains.");
                    valid = false;
                    continue;
                }
                if (!marker.IsUnmanagedType || marker.Arity != 0 || !schemaNames.Add(marker.Name))
                {
                    Error(errors, call, "TLGEN75", $"Schema '{Name(marker)}' must have a unique simple name and be non-generic unmanaged.");
                    valid = false;
                }
                string[]? expected = null;
                var names = new List<string>();
                foreach (var (asset, assetSite) in assets)
                {
                    var timeline = Implements(asset, contracts.Timeline) ? Timeline(asset, assetSite) : null;
                    if (timeline is null || !assetNames.Add(Name(asset)))
                    {
                        Error(errors, assetSite, "TLGEN76", $"Asset '{Name(asset)}' must be a valid source timeline and appear only once in a catalog.");
                        valid = false;
                        continue;
                    }
                    valid &= ValidateCatalogAssetCapacity(assetNames.Count, assetSite, errors);
                    if (assetMembers.TryGetValue(asset.Name, out var prior))
                    {
                        Error(errors, assetSite, "TLGEN78", $"Asset '{Name(asset)}' conflicts with '{prior}'; catalog asset simple names must be unique.");
                        valid = false;
                        continue;
                    }
                    assetMembers.Add(asset.Name, Name(asset));
                    var slots = Joined(timeline);
                    expected ??= slots;
                    if (!expected.SequenceEqual(slots, StringComparer.Ordinal))
                    {
                        Error(errors, assetSite, "TLGEN77", $"Asset '{Name(asset)}' does not match schema '{marker.Name}'.");
                        valid = false;
                    }
                    names.Add(Name(asset));
                }
                if (names.Count == 0)
                    valid = false;
                schemas.Add(new(marker.Name, names));
            }
            return valid ? new(entry.Type.Name, Space(entry.Type), schemas) : null;
        }

        private bool Schema(InvocationExpressionSyntax outer, SemanticModel model, IParameterSymbol builder,
            out INamedTypeSymbol marker, out IReadOnlyList<(INamedTypeSymbol, SyntaxNode)> assets)
        {
            marker = null!;
            var result = new List<(INamedTypeSymbol, SyntaxNode)>();
            var current = outer;
            while (current.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic, Expression: InvocationExpressionSyntax prior }
                && generic.Identifier.ValueText == "Asset" && generic.TypeArgumentList.Arguments.Count == 1 && current.ArgumentList.Arguments.Count == 0
                && model.GetTypeInfo(generic.TypeArgumentList.Arguments[0]).Type is INamedTypeSymbol asset && On(current, model, "Asset", contracts.SchemaBuilder))
            {
                result.Add((asset, generic.TypeArgumentList.Arguments[0]));
                current = prior;
            }
            if (result.Count == 0 || current.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax schema } member
                || schema.Identifier.ValueText != "Schema" || schema.TypeArgumentList.Arguments.Count != 1 || current.ArgumentList.Arguments.Count != 0
                || !Same(model.GetSymbolInfo(member.Expression).Symbol, builder)
                || model.GetTypeInfo(schema.TypeArgumentList.Arguments[0]).Type is not INamedTypeSymbol schemaType
                || !On(current, model, "Schema", contracts.CatalogBuilder))
            {
                assets = [];
                return false;
            }
            result.Reverse();
            marker = schemaType;
            assets = result;
            return true;
        }

        private static string[] Joined(JobTimeline timeline)
            => timeline.Tracks.Select(static track => track.Job).Concat(timeline.Before).Concat(timeline.After)
                .SelectMany(static job => job.Slots).GroupBy(static slot => (slot.Name, slot.TypeName))
                .Select(static group => $"{group.Key.Name}\0{group.Key.TypeName}\0{(group.Any(slot => slot.Mode == SlotMode.Reference) ? 1 : 0)}")
                .OrderBy(static item => item, StringComparer.Ordinal).ToArray();

        private bool Declaration(Entry entry, string kind, string code)
        {
            var syntax = entry.Type.DeclaringSyntaxReferences.Select(static reference => reference.GetSyntax()).OfType<TypeDeclarationSyntax>();
            if (entry.Type.TypeKind == TypeKind.Struct && entry.Type.DeclaredAccessibility == Accessibility.Public && entry.Type.IsReadOnly
                && entry.Type.Arity == 0 && entry.Type.ContainingType is null
                && syntax.All(static item => item is StructDeclarationSyntax && item.Modifiers.Any(token => token.IsKind(SyntaxKind.PartialKeyword))))
                return true;
            Error(errors, entry.Syntax, code, $"The {kind} '{Name(entry.Type)}' must be a public readonly partial top-level non-generic struct.");
            return false;
        }

        private IMethodSymbol? Define(Entry entry, INamedTypeSymbol builder, string kind, string code)
        {
            var methods = entry.Type.GetMembers("Define").OfType<IMethodSymbol>().Where(static method => !method.IsImplicitlyDeclared).ToArray();
            var method = methods.Length == 1 ? methods[0] : null;
            var syntax = method is null ? null : Method(method);
            if (method is not null && syntax is { Body: not null } && method.DeclaredAccessibility == Accessibility.Public
                && method.IsStatic && method.Arity == 0 && method.ReturnsVoid && method.Parameters.Length == 1
                && method.Parameters[0].RefKind == RefKind.None && Same(method.Parameters[0].Type, builder)
                && syntax.ParameterList.Parameters[0].Modifiers.Any(static token => token.ValueText == "scoped"))
                return method;
            Error(errors, entry.Syntax, code, $"The {kind} '{Name(entry.Type)}' requires public static void Define(scoped {Name(builder)} builder) with a block body.");
            return null;
        }
    }

    private sealed class Qualifier(SemanticModel model) : CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var rewritten = Rewritten(base.VisitObjectCreationExpression(node), node);
            return rewritten.WithType(QualifiedType(model.GetTypeInfo(node).Type, rewritten.Type));
        }
        public override SyntaxNode VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
            => QualifiedImplicit(
                model.GetTypeInfo(node).Type,
                Rewritten(base.VisitImplicitObjectCreationExpression(node), node));
        public override SyntaxNode VisitCastExpression(CastExpressionSyntax node)
        {
            var rewritten = Rewritten(base.VisitCastExpression(node), node);
            return rewritten.WithType(QualifiedType(model.GetTypeInfo(node.Type).Type, rewritten.Type));
        }
        public override SyntaxNode VisitDefaultExpression(DefaultExpressionSyntax node)
        {
            var rewritten = Rewritten(base.VisitDefaultExpression(node), node);
            return rewritten.WithType(QualifiedType(model.GetTypeInfo(node.Type).Type, rewritten.Type));
        }
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            => model.GetSymbolInfo(node).Symbol is IFieldSymbol { IsStatic: true } field && (field.HasConstantValue || field.ContainingType.TypeKind == TypeKind.Enum)
                ? SyntaxFactory.ParseExpression($"{Name(field.ContainingType)}.{Escape(field.Name)}") : base.VisitMemberAccessExpression(node);
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
            => model.GetSymbolInfo(node).Symbol is IFieldSymbol { IsStatic: true } field && (field.HasConstantValue || field.ContainingType.TypeKind == TypeKind.Enum)
                ? SyntaxFactory.ParseExpression($"{Name(field.ContainingType)}.{Escape(field.Name)}") : base.VisitIdentifierName(node);
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
            => node.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }
                && model.GetConstantValue(node) is { HasValue: true, Value: string value }
                    ? SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value))
                    : base.VisitInvocationExpression(node);
    }

    private static Contracts? Contract(Compilation compilation)
    {
        string[] names = ["Tl.ITimeline", "Tl.ITimelineCatalog", "Tl.ITimelineJob`2", "Tl.IHook", "Tl.Frame`2", "Tl.TimelineFrame", "Tl.Builder", "Tl.CatalogBuilder", "Tl.TrackRef`1", "Tl.SchemaBuilder`1", "Tl.IBlend`1"];
        var types = names.Select(compilation.GetTypeByMetadataName).ToArray();
        return types.Any(static type => type is null) ? null : new(types[0]!, types[1]!, types[2]!, types[3]!, types[4]!, types[5]!, types[6]!, types[7]!, types[8]!, types[9]!, types[10]!);    }

    private static IReadOnlyList<Entry> Entries(CSharpCompilation compilation)
    {
        var result = new List<Entry>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees)
        foreach (var syntax in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            if (compilation.GetSemanticModel(tree).GetDeclaredSymbol(syntax) is { } type && seen.Add(type))
                result.Add(new(type, syntax));
        return result;
    }

    private static bool JobSyntax(Entry entry)
        => entry.Type.GetMembers("Define").SelectMany(static member => member.DeclaringSyntaxReferences)
            .Select(static reference => reference.GetSyntax()).SelectMany(static syntax => syntax.DescendantNodes())
            .OfType<InvocationExpressionSyntax>().Any(static call => call.Expression is MemberAccessExpressionSyntax
                { Name: GenericNameSyntax { Identifier.ValueText: "Use" } });

    private static bool BuilderCall(InvocationExpressionSyntax call, SemanticModel model, IParameterSymbol builder, string name)
        => call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == name
            && Same(model.GetSymbolInfo(member.Expression).Symbol, builder) && On(call, model, name, builder.Type);

    private static bool BuilderGeneric(InvocationExpressionSyntax call, SemanticModel model, IParameterSymbol builder, string name, out INamedTypeSymbol type)
    {
        type = null!;
        return call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } member
            && generic.Identifier.ValueText == name && generic.TypeArgumentList.Arguments.Count == 1 && call.ArgumentList.Arguments.Count == 0
            && Same(model.GetSymbolInfo(member.Expression).Symbol, builder)
            && model.GetTypeInfo(generic.TypeArgumentList.Arguments[0]).Type is INamedTypeSymbol found
            && On(call, model, name, builder.Type) && (type = found) is not null;
    }

    private static bool On(InvocationExpressionSyntax call, SemanticModel model, string name, ITypeSymbol type)
    {
        var info = model.GetSymbolInfo(call);
        return info.Symbol is IMethodSymbol method && MethodOn(method, name, type)
            || info.CandidateSymbols.OfType<IMethodSymbol>().Any(candidate => MethodOn(candidate, name, type));
    }

    private static ExpressionSyntax[]? Args(InvocationExpressionSyntax call, SemanticModel model, int count)
    {
        if (model.GetOperation(call) is not IInvocationOperation operation || operation.TargetMethod.Parameters.Length != count)
            return null;
        var result = new ExpressionSyntax?[count];
        foreach (var argument in operation.Arguments)
            if (argument.Parameter is { Ordinal: >= 0 } parameter && parameter.Ordinal < count && argument.Syntax is ArgumentSyntax syntax)
                result[parameter.Ordinal] = syntax.Expression;
        return CompleteArguments(result);
    }

    internal static bool Bounded(ExpressionSyntax expression, SemanticModel model)
    {
        if (model.GetConstantValue(expression).HasValue)
            return BoundedConstant(expression);
        return expression switch
        {
            ObjectCreationExpressionSyntax item => BoundedObject(item, model),
            ImplicitObjectCreationExpressionSyntax item => item.Initializer is null && item.ArgumentList.Arguments.All(argument => Bounded(argument.Expression, model)),
            ParenthesizedExpressionSyntax item => Bounded(item.Expression, model),
            CastExpressionSyntax item => Bounded(item.Expression, model),
            PrefixUnaryExpressionSyntax item => Bounded(item.Operand, model),
            CheckedExpressionSyntax item => Bounded(item.Expression, model),
            DefaultExpressionSyntax or LiteralExpressionSyntax => true,
            _ => false,
        };
    }

    private static bool Unsigned(ExpressionSyntax expression, SemanticModel model, out uint value)
        => TryUnsignedConstant(model.GetConstantValue(expression).Value, out value);

    internal static bool TryUnsignedConstant(object? constant, out uint value)
    {
        switch (constant)
        {
            case byte item: value = item; return true;
            case ushort item: value = item; return true;
            case char item: value = item; return true;
            case int item when item >= 0: value = (uint)item; return true;
            case uint item: value = item; return true;
            default: value = 0; return false;
        }
    }

    private static bool Overlaps(int count, IReadOnlyCollection<HeterogeneousClip> clips)
        => Enumerable.Range(0, count).All(index => clips.Where(clip => clip.TrackIndex == index).ToArray() is var own
            && own.SelectMany(static clip => new[] { clip.Start, clip.End }).Distinct().All(cut => own.Count(clip => clip.Start <= cut && cut < clip.End) <= 2));
    internal static (string Code, string Message)? CatalogAssetCapacityDiagnostic(int count)
        => (uint)count <= ushort.MaxValue + 1u
            ? null
            : ("TLGEN78", "A catalog may contain at most 65,536 nonempty assets because route zero is reserved.");
    internal static bool ValidateCatalogAssetCapacity(int count, SyntaxNode site, ICollection<DeclarationDiagnostic> errors)
    {
        if (CatalogAssetCapacityDiagnostic(count) is not { } diagnostic)
            return true;
        Error(errors, site, diagnostic.Code, diagnostic.Message);
        return false;
    }
    internal static ITypeSymbol? ResolvedType(ITypeSymbol? converted, ITypeSymbol? natural) => converted ?? natural;
    internal static bool MethodOn(IMethodSymbol method, string name, ITypeSymbol type)
        => method.Name == name && Same(method.ContainingType.OriginalDefinition, type.OriginalDefinition);
    internal static ExpressionSyntax[]? CompleteArguments(ExpressionSyntax?[] arguments)
        => arguments.Any(static item => item is null) ? null : arguments.Cast<ExpressionSyntax>().ToArray();
    internal static bool BoundedConstant(ExpressionSyntax expression)
        => expression is not InvocationExpressionSyntax || expression is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } };
    internal static bool BoundedObject(ObjectCreationExpressionSyntax expression, SemanticModel model)
        => expression.Initializer is null && (expression.ArgumentList is null || expression.ArgumentList.Arguments.All(argument => Bounded(argument.Expression, model)));
    internal static TypeSyntax QualifiedType(ITypeSymbol? type, TypeSyntax original)
        => type is null ? original : SyntaxFactory.ParseTypeName(Name(type));
    internal static ExpressionSyntax QualifiedImplicit(ITypeSymbol? type, ImplicitObjectCreationExpressionSyntax original)
        => type is null
            ? original
            : SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(Name(type)), original.ArgumentList, null)
                .WithNewKeyword(SyntaxFactory.Token(SyntaxKind.NewKeyword).WithTrailingTrivia(SyntaxFactory.Space));
    internal static T Rewritten<T>(SyntaxNode? rewritten, T original) where T : SyntaxNode
        => rewritten as T ?? original;
    internal static string Canonical(ExpressionSyntax expression, SemanticModel model)
        => Rewritten(new Qualifier(model).Visit(expression), expression).WithoutTrivia().ToFullString();
    private static MethodDeclarationSyntax? Method(IMethodSymbol method) => method.DeclaringSyntaxReferences.Select(static reference => reference.GetSyntax()).OfType<MethodDeclarationSyntax>().SingleOrDefault();
    private static SyntaxNode Site(ISymbol symbol, SyntaxNode fallback) => symbol.DeclaringSyntaxReferences.Select(static reference => reference.GetSyntax()).FirstOrDefault() ?? fallback;
    private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol contract) => type.AllInterfaces.Any(item => Same(item.OriginalDefinition, contract));
    private static string PairKey(string job, string track, string clip) => job + "\0" + track + "\0" + clip;
    private static bool Same(ISymbol? left, ISymbol? right) => SymbolEqualityComparer.Default.Equals(left, right);
    private static string Name(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    private static string Space(INamedTypeSymbol type) => type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
    private static string Escape(string value) => SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None ? value : "@" + value;
    private static void Error(ICollection<DeclarationDiagnostic> errors, SyntaxNode node, string code, string message)
    {
        var span = node.GetLocation().GetLineSpan();
        errors.Add(new(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, code, message)
        {
            SpanStart = node.SpanStart, SpanLength = node.Span.Length,
            EndLine = span.EndLinePosition.Line + 1, EndColumn = span.EndLinePosition.Character + 1,
        });
    }
}
