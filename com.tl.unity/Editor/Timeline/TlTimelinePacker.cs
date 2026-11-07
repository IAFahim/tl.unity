using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Tl.Gen.Tlb;
using Tl.Unity.Bake;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;
using EntitiesHash = Unity.Entities.Hash128;

namespace Tl.Unity.Editor
{
#if UNITY_7000_0_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    public static class TlTimelinePacker
    {
        static TlKindCatalog s_Catalog;

        public static TlKindCatalog Catalog => s_Catalog ??= TlBake.Catalog(KindAssemblies());

        public static BlobAssetReference<TlTimelineBlob> Pack(TimelineAsset timeline, uint tickRate, TlKindCatalog catalog, ref TlBaker baker, UnityEngine.Object context, out ulong kindMask)
        {
            var name = timeline.name;
            var typed = new TypedTimeline { Name = name, Duration = Ticks(timeline.duration, tickRate) };
            var next = Array.Empty<string>();
            var nextAssets = Array.Empty<EntitiesHash>();
            var channels = Array.Empty<string>();
            var settings = TlDirectorTimelines.Settings(timeline);
            if (settings != null)
            {
                typed.Loops = settings.End == TlEnd.Loop;
                typed.Hold = settings.End == TlEnd.Hold;
                if (settings.End == TlEnd.Next && settings.Next != null)
                {
                    typed.Next = settings.Next.name;
                    next = new[] { settings.Next.name };
                    nextAssets = new[] { AssetOf(settings.Next) };
                }
                if (settings.Channel > 0)
                {
                    channels = Enumerable.Range(1, settings.Channel).Select(c => c.ToString()).ToArray();
                    typed.Channel = channels[channels.Length - 1];
                }
            }
            foreach (var track in timeline.GetOutputTracks())
            {
                if (!(track is TlTrack tl) || tl.mutedInHierarchy || tl.Template == null)
                    continue;
                var clipType = tl.Template.ClipType;
                var typedTrack = new TypedTrack { Name = tl.name, Type = typeof(TlNoSettings<>).MakeGenericType(clipType) };
                foreach (var clip in tl.GetClips().OrderBy(c => c.start))
                {
                    var data = (clip.asset as TlClip)?.Data;
                    if (data == null || data.ClipType != clipType)
                    {
                        Debug.LogWarning("The clip at " + Seconds(clip.start) + " in " + name + " (" + NameOf(context) + ") has no " + TlMenu.ShortName(tl.Template.MenuName) + " values. The clip is skipped.", clip.asset);
                        continue;
                    }
                    var start = Ticks(clip.start, tickRate);
                    var end = Math.Max(start + 1, Ticks(clip.end, tickRate));
                    try
                    {
                        data.Bake(ref baker);
                    }
                    catch (Exception error)
                    {
                        Debug.LogWarning(TlMenu.ShortName(data.MenuName) + " baking failed on the clip at " + Seconds(clip.start) + " in " + name + " (" + NameOf(context) + "): " + error.Message + " The clip is skipped.", clip.asset);
                        continue;
                    }
                    var bytes = new byte[data.ClipSize];
                    data.WriteClip(bytes, 0);
                    typedTrack.Clips.Add(new TypedClip { Name = clip.displayName, Type = clipType, Start = start, End = end, Data = bytes });
                    typed.Duration = Math.Max(typed.Duration, end);
                }
                typed.Tracks.Add(typedTrack);
            }
            var manifest = TlBake.Manifest(next, channels, tickRate);
            var tlb = TlBake.BakeTyped(typed, manifest);
            kindMask = TlBake.Analyze(name, tlb, catalog).KindMask;
            return TlTimelineBlobs.Create(AssetOf(timeline), tickRate, name, tlb, TlBake.KindBytes(name, tlb, catalog), nextAssets);
        }

        public static uint Ticks(double seconds, uint tickRate)
        {
            return seconds <= 0 ? 0u : (uint)Math.Round(seconds * tickRate);
        }

        public static EntitiesHash AssetOf(TimelineAsset timeline)
        {
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(timeline, out string guid, out long _) && !string.IsNullOrEmpty(guid))
                return new EntitiesHash(guid);
            return new EntitiesHash(UnityEngine.Hash128.Compute(timeline.name).ToString());
        }

        static IEnumerable<Assembly> KindAssemblies()
        {
            var registry = TimelineKindSets.Registry;
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var k = 0; k < registry.Count; k++)
                names.Add(registry.SourceOf(k));
            return TlAssemblies.Loaded()
                .Where(a => names.Contains(a.GetName().Name))
                .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
                .ToList();
        }

        static string Seconds(double seconds) => seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";

        static string NameOf(UnityEngine.Object context) => context == null ? "no object" : context.name;
    }
}
