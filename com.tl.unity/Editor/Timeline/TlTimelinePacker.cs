using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Tl.Gen.Tlb;
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
        static BakeKinds s_Catalog;

        public static BakeKinds Catalog => s_Catalog ??= LibraryBake.Kinds(KindAssemblies());

        [InitializeOnLoadMethod]
        static void InstallResolvers()
        {
            TlClipData.AssetResolver = AssetResolver;
            TlBaking.TickRateResolver = () => TlProjectSettings.instance.TicksPerSecond;
        }

        public static BlobAssetReference<TlTimelineBlob> Pack(TimelineAsset timeline, uint tickRate, BakeKinds catalog, ref TlBaker baker, UnityEngine.Object context, out ulong kindMask)
        {
            var name = timeline.name;
            var typed = Typed(timeline, tickRate, ref baker, context, out var refs, out var channels, out var written);
            var names = new string[refs.Count];
            for (var i = 0; i < names.Length; i++)
                names[i] = i == 0 && typed.Next != null ? typed.Next : "~" + i;
            var manifest = LibraryBake.Manifest(names, channels, tickRate);
            var timelineBytes = Bake(typed, manifest, written, refs, 0);
            uint[] sites = null;
            if (written.Any(w => TlDirectorTimelines.IdFields(w.data).Any()))
                sites = Sites(timelineBytes, Bake(typed, manifest, written, refs, Marker), refs.Count);
            kindMask = LibraryBake.KindMask(name, timelineBytes, catalog);
            return TlTimelineBlobs.Create(AssetOf(timeline), tickRate, name, timelineBytes, LibraryBake.KindPayload(name, timelineBytes, catalog), refs.Select(AssetOf).ToArray(), sites);
        }

        public static string Json(TimelineAsset timeline, uint tickRate, ref TlBaker baker, out int channelCount)
        {
            var typed = Typed(timeline, tickRate, ref baker, timeline, out var refs, out var channels, out var written);
            channelCount = channels.Length;
            var manifest = LibraryBake.Manifest(refs.Select(r => r.name).ToArray(), channels, tickRate);
            Fill(written, refs, 0);
            return TimelineJson.Export(typed, manifest);
        }

        static TypedTimeline Typed(TimelineAsset timeline, uint tickRate, ref TlBaker baker, UnityEngine.Object context, out List<TimelineAsset> refs, out string[] channels, out List<(object item, TlClipData data)> written)
        {
            var name = timeline.name;
            var typed = new TypedTimeline { Name = name, Duration = Ticks(timeline.duration, tickRate) };
            refs = new List<TimelineAsset>();
            channels = Array.Empty<string>();
            var settings = TlDirectorTimelines.Settings(timeline);
            if (settings != null)
            {
                typed.Loops = settings.End == TlEnd.Loop;
                typed.Hold = settings.End == TlEnd.Hold;
                if (settings.End == TlEnd.Next && settings.Next != null)
                {
                    typed.Next = settings.Next.name;
                    refs.Add(settings.Next);
                }
                if (settings.Channel > 0)
                {
                    channels = Enumerable.Range(1, settings.Channel).Select(c => c.ToString()).ToArray();
                    typed.Channel = channels[channels.Length - 1];
                }
            }
            written = new List<(object item, TlClipData data)>();
            TlClipData.AssetResolver = AssetResolver;
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
                        if (baker.IsActive)
                            data.Bake(ref baker);
                    }
                    catch (Exception error)
                    {
                        Debug.LogWarning(TlMenu.ShortName(data.MenuName) + " baking failed on the clip at " + Seconds(clip.start) + " in " + name + " (" + NameOf(context) + "): " + error.Message + " The clip is skipped.", clip.asset);
                        continue;
                    }
                    foreach (var target in TlDirectorTimelines.IdFields(data))
                        if (!refs.Contains(target))
                            refs.Add(target);
                    var typedClip = new TypedClip { Name = clip.displayName, Type = clipType, Start = start, End = end };
                    typedTrack.Clips.Add(typedClip);
                    written.Add((typedClip, data));
                    typed.Duration = Math.Max(typed.Duration, end);
                }
                if (typedTrack.Clips.Count > 0)
                    typed.Tracks.Add(typedTrack);
            }
            foreach (var marker in TlDirectorTimelines.Markers(timeline).OrderBy(m => m.time))
            {
                var data = marker.Data;
                try
                {
                    if (baker.IsActive)
                        data.Bake(ref baker);
                }
                catch (Exception error)
                {
                    Debug.LogWarning(TlMenu.ShortName(data.MenuName) + " baking failed on the marker at " + Seconds(marker.time) + " in " + name + " (" + NameOf(context) + "): " + error.Message + " The marker is skipped.", marker);
                    continue;
                }
                foreach (var target in TlDirectorTimelines.IdFields(data))
                    if (!refs.Contains(target))
                        refs.Add(target);
                var tick = Ticks(marker.time, tickRate);
                var typedMarker = new TypedMarker { Type = data.ClipType, Tick = tick };
                typed.Markers.Add(typedMarker);
                written.Add((typedMarker, data));
                typed.Duration = Math.Max(typed.Duration, tick + 1);
            }
            return typed;
        }

        const ushort Marker = 0x8000;

        static byte[] Bake(TypedTimeline typed, LibraryManifest manifest, List<(object item, TlClipData data)> written, List<TimelineAsset> refs, ushort marker)
        {
            Fill(written, refs, marker);
            return LibraryBake.BakeTyped(typed, manifest);
        }

        static void Fill(List<(object item, TlClipData data)> written, List<TimelineAsset> refs, ushort marker)
        {
            TlClipData.LocalIdResolver = asset => asset is TimelineAsset target && refs.IndexOf(target) is var index && index >= 0 ? (ushort)((index + 1) | marker) : (ushort)0;
            try
            {
                foreach (var (item, data) in written)
                {
                    var bytes = new byte[data.ClipSize];
                    data.WriteClip(bytes, 0);
                    if (item is TypedClip clip)
                        clip.Data = bytes;
                    else
                        ((TypedMarker)item).Data = bytes;
                }
            }
            finally
            {
                TlClipData.LocalIdResolver = null;
            }
        }

        static uint[] Sites(byte[] plain, byte[] marked, int count)
        {
            var sites = new List<uint>();
            if (plain.Length != marked.Length)
                throw new InvalidOperationException("the timeline references could not be located");
            for (var i = 0; i + 1 < plain.Length; i++)
            {
                var a = plain[i] | (plain[i + 1] << 8);
                var b = marked[i] | (marked[i + 1] << 8);
                if (a == b || a < 1 || a > count || b != (a | Marker))
                    continue;
                sites.Add((uint)i);
                sites.Add((uint)a);
                i++;
            }
            return sites.ToArray();
        }

        static EntitiesHash AssetResolver(UnityEngine.Object asset) => asset is TimelineAsset timeline ? AssetOf(timeline) : default;

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
            for (var f = 0; f < registry.Feeds.Count; f++)
                names.Add(registry.FeedSourceOf(f));
            return TlAssemblies.Loaded()
                .Where(a => names.Contains(a.GetName().Name))
                .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
                .ToList();
        }

        static string Seconds(double seconds) => seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";

        static string NameOf(UnityEngine.Object context) => context == null ? "no object" : context.name;
    }
}
