using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity
{
    public abstract class TlTrackAsset : TrackAsset, ITlData
    {
        [SerializeField, HideInInspector] List<TlField> m_TlData = new List<TlField>();

        public abstract Type DataType { get; }

        public List<TlField> Fields
        {
            get => m_TlData;
            set => m_TlData = value ?? new List<TlField>();
        }
    }

    public abstract class TlTrack<T> : TlTrackAsset where T : unmanaged
    {
        public override Type DataType => typeof(T);

        public T Data
        {
            get => TlFields.Read<T>(Fields);
            set => Fields = TlFields.Write(value);
        }
    }

    public abstract class TlClipAsset : PlayableAsset, ITimelineClipAsset, ITlData
    {
        [SerializeField, HideInInspector] List<TlField> m_TlData = new List<TlField>();

        public abstract Type DataType { get; }

        public List<TlField> Fields
        {
            get => m_TlData;
            set => m_TlData = value ?? new List<TlField>();
        }

        public ClipCaps clipCaps => ClipCaps.Blending;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            return Playable.Create(graph);
        }
    }

    public abstract class TlClip<T> : TlClipAsset where T : unmanaged
    {
        public override Type DataType => typeof(T);

        public T Data
        {
            get => TlFields.Read<T>(Fields);
            set => Fields = TlFields.Write(value);
        }
    }

    public abstract class TlMarkerAsset : Marker, ITlData
    {
        [SerializeField, HideInInspector] List<TlField> m_TlData = new List<TlField>();

        public abstract Type DataType { get; }

        public List<TlField> Fields
        {
            get => m_TlData;
            set => m_TlData = value ?? new List<TlField>();
        }
    }

    public abstract class TlMarker<T> : TlMarkerAsset where T : unmanaged
    {
        public override Type DataType => typeof(T);

        public T Data
        {
            get => TlFields.Read<T>(Fields);
            set => Fields = TlFields.Write(value);
        }
    }
}
