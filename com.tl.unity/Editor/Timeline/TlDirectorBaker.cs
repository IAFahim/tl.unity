using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity.Editor
{
    public sealed class TlDirectorBaker : Baker<PlayableDirector>
    {
        public override void Bake(PlayableDirector director)
        {
            var alsoPlays = GetComponent<TlAlsoPlays>();
            var timelines = TlDirectorTimelines.Collect(this, director, alsoPlays);
            if (timelines.Count == 0)
                return;
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            var added = new HashSet<ComponentType>
            {
                ComponentType.ReadWrite<TlTimelineSet>(),
                ComponentType.ReadWrite<Playback>(),
                ComponentType.ReadWrite<TimelineRequest>(),
                ComponentType.ReadWrite<TimelinePlaying>(),
                ComponentType.ReadWrite<TimelineHorizon>(),
                ComponentType.ReadWrite<TlUnregistered>(),
                ComponentType.ReadWrite<TlPlayOnAwake>(),
                ComponentType.ReadWrite<TlAlsoPlayOnAwake>(),
            };
            var baker = new TlBaker(this, entity, added, director);
            var rate = TlProjectSettings.instance.TicksPerSecond;
            var catalog = TlTimelinePacker.Catalog;
            var blobs = new List<BlobAssetReference<TlTimelineBlob>>();
            var packed = new List<TimelineAsset>();
            var mask = 0UL;
            foreach (var timeline in timelines)
            {
                try
                {
                    var blob = TlTimelinePacker.Pack(timeline, rate, catalog, ref baker, director, out var reached);
                    AddBlobAsset(ref blob, out _);
                    blobs.Add(blob);
                    packed.Add(timeline);
                    mask |= reached;
                }
                catch (Exception error)
                {
                    Debug.LogError("Tl could not prepare " + timeline.name + " on " + director.name + ": " + error.Message, director);
                }
            }
            if (blobs.Count == 0)
                return;
            var set = AddBuffer<TlTimelineSet>(entity);
            foreach (var blob in blobs)
                set.Add(new TlTimelineSet { Blob = blob });
            AddBuffer<Playback>(entity);
            AddBuffer<TimelineRequest>(entity);
            AddComponent<TimelinePlaying>(entity);
            SetComponentEnabled<TimelinePlaying>(entity, false);
            AddComponent<TimelineHorizon>(entity);
            AddComponent<TlUnregistered>(entity);
            var playableSlot = director.playOnAwake ? packed.IndexOf(director.playableAsset as TimelineAsset) : -1;
            var alsoSlots = new List<int>();
            if (alsoPlays != null)
                foreach (var alsoOnAwake in alsoPlays.PlayOnAwake)
                {
                    var slot = alsoOnAwake == null ? -1 : packed.IndexOf(alsoOnAwake);
                    if (slot >= 0 && slot != playableSlot && !alsoSlots.Contains(slot))
                        alsoSlots.Add(slot);
                }
            if (playableSlot >= 0 || alsoSlots.Count > 0)
                AddComponent(entity, new TlPlayOnAwake { Slot = playableSlot, At = playableSlot >= 0 ? (ushort)Math.Min(ushort.MaxValue, TlTimelinePacker.Ticks(director.initialTime, rate)) : (ushort)0 });
            if (alsoSlots.Count > 0)
            {
                var alsoOnAwakeSlots = AddBuffer<TlAlsoPlayOnAwake>(entity);
                foreach (var slot in alsoSlots)
                    alsoOnAwakeSlots.Add(new TlAlsoPlayOnAwake { Slot = slot });
            }
            TlKindBaking.AddReached(this, entity, mask, catalog.Names, added, director.name);
        }
    }
}
