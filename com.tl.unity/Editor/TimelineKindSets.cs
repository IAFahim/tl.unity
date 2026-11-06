using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;

namespace Tl.Unity.Editor
{
    public readonly struct ReachedKind
    {
        public ReachedKind(int kind, ushort timeline, string source)
        {
            Kind = kind;
            Timeline = timeline;
            Source = source;
        }

        public readonly int Kind;
        public readonly ushort Timeline;
        public readonly string Source;
    }

    public sealed class KindSetPlan
    {
        public readonly List<ushort> Starts = new List<ushort>();
        public readonly List<ushort> Timelines = new List<ushort>();
        public readonly SortedDictionary<int, ReachedKind> Kinds = new SortedDictionary<int, ReachedKind>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool LibraryHasKinds;
    }

#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public static unsafe class TimelineKindSets
    {
        static readonly Dictionary<string, (DateTime Stamp, global::Tl.TimelineWorld World)> Libraries = new Dictionary<string, (DateTime, global::Tl.TimelineWorld)>();
        static KindRegistry s_Registry;

        public static KindRegistry Registry => s_Registry ??= KindRegistry.Discover();

        public static string PhysicalPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : FileUtil.GetPhysicalPath(path)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string LibraryFile(TimelineLibraryAsset library)
        {
            return (library.Folder ?? string.Empty).TrimEnd('/', '\\') + "/game.tlib";
        }

        public static global::Tl.TimelineWorld Load(TimelineLibraryAsset library)
        {
            var file = PhysicalPath(LibraryFile(library));
            if (!File.Exists(file))
                throw new FileNotFoundException("Timeline library '" + library.name + "' has no game.tlib in '" + library.Folder + "'.", file);
            var stamp = File.GetLastWriteTimeUtc(file);
            if (Libraries.TryGetValue(file, out var cached) && cached.Stamp == stamp)
                return cached.World;
            TlGeneratedBootstrap.InstallDiscoveredModules();
            var world = new global::Tl.TimelineWorld(File.ReadAllBytes(file));
            if (cached.World != null)
                cached.World.Dispose();
            Libraries[file] = (stamp, world);
            return world;
        }

        public static Type IdsType(TimelineLibraryAsset library)
        {
            if (string.IsNullOrEmpty(library.TimelineIds))
                return null;
            if (!string.IsNullOrEmpty(library.GameAssembly))
                return Type.GetType(library.TimelineIds + ", " + library.GameAssembly, false);
            foreach (var assembly in TlAssemblies.Loaded())
            {
                var type = assembly.GetType(library.TimelineIds, false);
                if (type != null)
                    return type;
            }
            return null;
        }

        public static ushort IdOf(Type ids, string name)
        {
            var field = ids == null || string.IsNullOrEmpty(name) ? null : ids.GetField(name, BindingFlags.Public | BindingFlags.Static);
            return field != null && field.IsLiteral && field.FieldType == typeof(ushort) ? (ushort)field.GetRawConstantValue() : (ushort)0;
        }

        public static string NameOf(Type ids, ushort id)
        {
            if (ids != null)
                foreach (var field in ids.GetFields(BindingFlags.Public | BindingFlags.Static))
                    if (field.IsLiteral && field.FieldType == typeof(ushort) && (ushort)field.GetRawConstantValue() == id)
                        return field.Name;
            return "#" + id;
        }

        public static List<ushort> Reachable(in global::Tl.LibraryView view, IEnumerable<ushort> roots)
        {
            var seen = new HashSet<ushort>();
            var stack = new Stack<ushort>(roots);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id == 0 || id > view.TimelineCount || !seen.Add(id))
                    continue;
                var next = view.Timelines[id - 1].Next;
                if (next != 0)
                    stack.Push(next);
            }
            var order = new List<ushort>(seen);
            order.Sort();
            return order;
        }

        public static void AddTimelineKinds(in global::Tl.LibraryView view, IReadOnlyList<ushort> timelines, Type ids, KindRegistry registry, KindSetPlan plan)
        {
            var section = view.Kinds;
            if (section.IsEmpty)
                return;
            foreach (var id in timelines)
            {
                var timeline = section.TimelineOf(id);
                if (timeline == null)
                    continue;
                for (var k = 0; k < section.KindCount && k < 64; k++)
                {
                    if ((timeline->KindMask & (1UL << k)) == 0)
                        continue;
                    var name = section.NameOf(k);
                    var index = registry.IndexOf(name);
                    if (index < 0)
                    {
                        plan.Errors.Add("timeline '" + NameOf(ids, id) + "' feeds kind '" + name + "', which no Unity system runs.");
                        continue;
                    }
                    if (!plan.Kinds.ContainsKey(index))
                        plan.Kinds[index] = new ReachedKind(index, id, "timeline '" + NameOf(ids, id) + "'");
                }
            }
        }

        public static void AddNamedKind(string name, string source, KindRegistry registry, KindSetPlan plan)
        {
            var index = string.IsNullOrEmpty(name) ? -1 : registry.IndexOf(name);
            if (index < 0)
            {
                plan.Errors.Add(source + " names kind '" + name + "', which no Unity system runs.");
                return;
            }
            if (!plan.Kinds.ContainsKey(index))
                plan.Kinds[index] = new ReachedKind(index, 0, source);
        }

        public static KindSetPlan Plan(TimelinePlayerAuthoring player, IEnumerable<Type> components, KindRegistry registry)
        {
            var plan = new KindSetPlan();
            var library = player.Library;
            if (library == null)
            {
                plan.Errors.Add("has no Library.");
                return plan;
            }
            var ids = IdsType(library);
            if (ids == null)
            {
                plan.Errors.Add("library '" + library.name + "' names timeline ids class '" + library.TimelineIds + "', which is not compiled.");
                return plan;
            }
            var view = Load(library).View;
            plan.LibraryHasKinds = !view.Kinds.IsEmpty;
            var roots = new List<ushort>();
            AddTimelines(player.StartWith, "start-with", ids, plan, roots, plan.Starts);
            AddTimelines(player.MayStart, "may-start", ids, plan, roots, null);
            plan.Timelines.AddRange(Reachable(in view, roots));
            AddTimelineKinds(in view, plan.Timelines, ids, registry, plan);
            if (player.Kinds != null)
                foreach (var name in player.Kinds)
                    AddNamedKind(name, "the Kinds list", registry, plan);
            foreach (var component in components)
            {
                if (component == null)
                    continue;
                var attribute = (RequiresKindsAttribute)Attribute.GetCustomAttribute(component, typeof(RequiresKindsAttribute), true);
                if (attribute == null)
                    continue;
                foreach (var kind in attribute.Kinds)
                    AddNamedKind(kind == null ? null : kind.Name, "component '" + component.Name + "' [RequiresKinds]", registry, plan);
            }
            var profile = player.Profile;
            if (profile != null && library.KindSets == KindSets.Exact)
                plan.Warnings.Add("has Profile '" + profile.name + "', but library '" + library.name + "' uses Exact kind sets, so the profile is ignored. Set Kind Sets to Union Per Profile to use it.");
            if (profile != null && library.KindSets == KindSets.UnionPerProfile)
            {
                var declared = new HashSet<int>();
                foreach (var name in profile.Kinds)
                {
                    AddNamedKind(name, "profile '" + profile.name + "'", registry, plan);
                    declared.Add(registry.IndexOf(name));
                }
                foreach (var reached in plan.Kinds.Values)
                    if (!declared.Contains(reached.Kind))
                        plan.Warnings.Add("reaches kind '" + registry[reached.Kind].Name + "' through " + reached.Source + ", which profile '" + profile.name + "' lacks, so it gets its own archetype.");
            }
            return plan;
        }

        static void AddTimelines(string[] names, string list, Type ids, KindSetPlan plan, List<ushort> roots, List<ushort> starts)
        {
            if (names == null)
                return;
            foreach (var name in names)
            {
                var id = IdOf(ids, name);
                if (id == 0)
                {
                    plan.Errors.Add(list + " names timeline '" + name + "', which " + ids.FullName + " does not declare.");
                    continue;
                }
                roots.Add(id);
                if (starts != null)
                    starts.Add(id);
            }
        }
    }
}
