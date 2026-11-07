using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Playables;

namespace Tl.Unity.Editor
{
    public sealed class TlDirectorBaker : Baker<PlayableDirector>
    {
        public override void Bake(PlayableDirector director)
        {
            if (GetComponent<TimelinePlayerAuthoring>() != null)
                return;
            var timelines = TlDirectorTimelines.Collect(this, director, GetComponent<TlAlsoPlays>());
            if (timelines.Count == 0)
                return;
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            var added = new HashSet<ComponentType>
            {
                ComponentType.ReadWrite<TlTimelineSet>(),
                ComponentType.ReadWrite<Playback>(),
                ComponentType.ReadWrite<TimelineRequest>(),
                ComponentType.ReadWrite<TimelinePlaying>(),
                ComponentType.ReadWrite<TlUnregistered>(),
                ComponentType.ReadWrite<TlPlayOnAwake>(),
            };
            var baker = new TlBaker(this, entity, added, director);
            var rate = TlProjectSettings.instance.TicksPerSecond;
            var catalog = TlTimelinePacker.Catalog;
            var blobs = new List<BlobAssetReference<TlTimelineBlob>>();
            var mask = 0UL;
            foreach (var timeline in timelines)
            {
                try
                {
                    var blob = TlTimelinePacker.Pack(timeline, rate, catalog, ref baker, director, out var reached);
                    AddBlobAsset(ref blob, out _);
                    blobs.Add(blob);
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
            AddComponent<TlUnregistered>(entity);
            if (director.playOnAwake && ReferenceEquals(director.playableAsset, timelines[0]))
                AddComponent(entity, new TlPlayOnAwake { Slot = 0, At = (ushort)Math.Min(ushort.MaxValue, TlTimelinePacker.Ticks(director.initialTime, rate)) });
            TlKindBaking.AddReached(this, entity, mask, catalog.Kinds, added, director.name);
        }
    }
}
