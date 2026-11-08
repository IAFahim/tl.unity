using System.Reflection;
using System.Runtime.CompilerServices;

namespace Tl.Gen.Tlb;

internal enum KindAccepts : byte { Spans, Pulses, Both }

internal enum KindRule : byte { Combine, Each }

internal enum FeederTrigger : byte { Span, Start, End, Marker }

internal sealed record KindInfo(string Name, string FullName, Type Value, int ValueBytes, KindAccepts Accepts, KindRule Mode, int Order, string? Group, bool Folds, int? Inline = null, int GamePulses = 0)
{
    public int EntryBytes => KindCatalog.EntryBytes(Accepts, ValueBytes);
}

internal sealed record FeederInfo(int Kind, FeederTrigger Trigger, string Consumer, string Method, ulong SourceKey, byte Ordinal);

internal sealed class KindCatalog
{
    internal const string KindAttribute = "Tl.TimelineKindAttribute";
    internal const string TlKindAttribute = "Tl.TlKindAttribute";
    internal const string FeedsAttribute = "Tl.FeedsAttribute";
    internal const string PulseAttribute = "Tl.PulseAttribute";

    public readonly List<KindInfo> Kinds = new();
    public readonly List<FeederInfo> Feeders = new();

    internal static int EntryBytes(KindAccepts accepts, int valueBytes) => accepts == KindAccepts.Spans ? 8 : 8 + ((valueBytes + 3) & ~3);

    internal static int GroupEntryBytes(int largestValueBytes) => 12 + ((largestValueBytes + 3) & ~3);

    public int IndexOf(string name) => Kinds.FindIndex(k => k.Name == name);

    public static KindCatalog Discover(IEnumerable<Assembly> assemblies)
    {
        var types = assemblies.Distinct().SelectMany(GetTypesSafe).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
        var catalog = new KindCatalog();
        var keyed = new List<Type>();
        foreach (var type in types)
        {
            var (track, marker) = Sources(type);
            if (track != null) { keyed.Add(track.Value.Track); keyed.Add(track.Value.Clip); }
            if (marker != null) keyed.Add(marker);
        }
        TypeNames.Unique(keyed);
        var kindTypes = new Dictionary<Type, int>();
        foreach (var type in types)
        {
            var attribute = type.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName is KindAttribute or TlKindAttribute);
            if (attribute == null) continue;
            var where = $"kind '{type.FullName}'";
            if (!type.IsValueType) throw Bad($"{where}: [TimelineKind] needs a struct.");
            var apply = StaticMethod(type, "Apply") ?? throw Bad($"{where}: needs public static Apply(in T total, ...).");
            var parameters = apply.GetParameters();
            if (parameters.Length == 0) throw Bad($"{where}: Apply needs the kind value as its first parameter.");
            var value = Strip(parameters[0].ParameterType);
            var bytes = SizeOf(value) ?? throw Bad($"{where}: value type '{value.FullName}' must be unmanaged.");
            var accepts = (KindAccepts)Named(attribute, "Accepts", 0);
            var mode = (KindRule)Named(attribute, "Mode", 0);
            if (mode == KindRule.Combine)
            {
                var combine = StaticMethod(type, "Combine");
                var cp = combine?.GetParameters();
                if (cp == null || cp.Length != 2 || Strip(cp[0].ParameterType) != value || Strip(cp[1].ParameterType) != value)
                    throw Bad($"{where}: Combine mode needs public static Combine(ref {value.Name} total, in {value.Name} next).");
            }
            var folds = Named(attribute, "FoldsPulses", 0) != 0;
            if (folds && (mode != KindRule.Combine || accepts == KindAccepts.Spans)) throw Bad($"{where}: FoldsPulses needs Combine mode and pulses.");
            var group = attribute.NamedArguments.FirstOrDefault(n => n.MemberName == "Group").TypedValue.Value as string;
            if (catalog.Kinds.Any(k => k.Name == type.Name)) throw Bad($"{where}: another kind is already named '{type.Name}'; kind names must be unique.");
            if (catalog.Kinds.Count == 64) throw Bad($"{where}: more than 64 kinds; merge rare kinds into a kind group.");
            kindTypes[type] = catalog.Kinds.Count;
            catalog.Kinds.Add(new KindInfo(type.Name, type.FullName!, value, bytes, accepts, mode, Named(attribute, "Order", 0), string.IsNullOrEmpty(group) ? null : group, folds, Inline(attribute), Math.Max(0, Named(attribute, "GamePulses", 0))));
        }
        foreach (var g in catalog.Kinds.Where(k => k.Group != null).Select(k => k.Group!).Distinct(StringComparer.Ordinal))
            if (catalog.Kinds.Any(k => k.Name == g)) throw Bad($"kind group '{g}' has the same name as a kind.");

        var found = new List<(int Kind, FeederTrigger Trigger, string Consumer, string Method, ulong Key)>();
        foreach (var type in types)
        {
            var (track, marker) = Sources(type);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(m => m.Name, StringComparer.Ordinal))
                foreach (var attribute in method.CustomAttributes)
                {
                    var name = attribute.AttributeType.FullName;
                    if (name != FeedsAttribute && name != PulseAttribute) continue;
                    var where = $"'{type.FullName}.{method.Name}'";
                    if (attribute.ConstructorArguments.Count != 1 || attribute.ConstructorArguments[0].Value is not Type kindType)
                        throw Bad($"{where}: [{Short(name)}] needs typeof(<kind>).");
                    if (!kindTypes.TryGetValue(kindType, out var kind))
                        throw Bad($"{where}: '{kindType.FullName}' is not a [TimelineKind].");
                    var info = catalog.Kinds[kind];
                    var trigger = name == FeedsAttribute ? FeederTrigger.Span : (FeederTrigger)(1 + Named(attribute, "On", 0));
                    if (trigger == FeederTrigger.Span && info.Accepts == KindAccepts.Pulses)
                        throw Bad($"{where}: kind '{info.Name}' accepts pulses only; use [Pulse].");
                    if (trigger != FeederTrigger.Span && info.Accepts == KindAccepts.Spans)
                        throw Bad($"{where}: kind '{info.Name}' accepts spans only; set Accepts = Pulses or Both on the kind.");
                    if (Strip(method.ReturnType) != info.Value)
                        throw Bad($"{where}: returns '{method.ReturnType.FullName}', but kind '{info.Name}' takes '{info.Value.FullName}'.");
                    ulong key;
                    if (trigger == FeederTrigger.Marker)
                    {
                        if (marker == null) throw Bad($"{where}: On = Marker needs a consumer that implements IMarker<T>.");
                        key = MarkerKey(marker);
                    }
                    else
                    {
                        if (track == null) throw Bad($"{where}: [{Short(name)}] needs a consumer that implements ITrack<TTrack, TClip>.");
                        key = PairKey(track.Value.Track, track.Value.Clip);
                    }
                    found.Add((kind, trigger, type.FullName!, method.Name, key));
                }
        }
        foreach (var group in found.GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            var ordinal = 0;
            foreach (var f in group.OrderBy(f => f.Consumer, StringComparer.Ordinal).ThenBy(f => f.Method, StringComparer.Ordinal))
            {
                if (ordinal > byte.MaxValue) throw Bad($"kind '{catalog.Kinds[f.Kind].Name}': more than 256 feeders.");
                catalog.Feeders.Add(new FeederInfo(f.Kind, f.Trigger, f.Consumer, f.Method, f.Key, (byte)ordinal++));
            }
        }
        return catalog;
    }

    internal static ulong PairKey(Type track, Type clip) => Keying.Of(Keying.StableName(track) + "\0" + Keying.StableName(clip));

    internal static ulong MarkerKey(Type marker) => Keying.Of(Keying.StableName(marker));

    static ((Type Track, Type Clip)? Track, Type? Marker) Sources(Type type)
    {
        (Type, Type)? track = null;
        Type? marker = null;
        foreach (var iface in type.GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            var definition = iface.GetGenericTypeDefinition();
            if (definition == typeof(ITrack<,>)) track = (iface.GetGenericArguments()[0], iface.GetGenericArguments()[1]);
            else if (definition == typeof(IMarker<>)) marker = iface.GetGenericArguments()[0];
        }
        return (track, marker);
    }

    internal static BakeDiagnosticException Bad(string message) => new(message);

    static string Short(string? attribute) => attribute == FeedsAttribute ? "Feeds" : "Pulse";

    static MethodInfo? StaticMethod(Type type, string name)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => m.Name == name).OrderBy(m => m.GetParameters().Length).FirstOrDefault();

    static Type Strip(Type type) => type.IsByRef ? type.GetElementType()! : type;

    static int? Inline(CustomAttributeData attribute)
    {
        var inline = Named(attribute, "Inline", -1);
        return inline >= 0 ? inline : null;
    }

    static int Named(CustomAttributeData attribute, string name, int fallback)
    {
        foreach (var argument in attribute.NamedArguments)
            if (argument.MemberName == name && argument.TypedValue.Value != null) return Convert.ToInt32(argument.TypedValue.Value);
        return fallback;
    }

    static int? SizeOf(Type type)
    {
        if (!type.IsValueType) return null;
        try
        {
            var flag = (bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!.MakeGenericMethod(type).Invoke(null, null)!;
            if (flag) return null;
            return (int)typeof(SizeCell<>).MakeGenericType(type).GetField("Value")!.GetValue(null)!;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static IEnumerable<Type> GetTypesSafe(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }
}
