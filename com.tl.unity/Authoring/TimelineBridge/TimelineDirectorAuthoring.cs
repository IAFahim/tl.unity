using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayableDirector), typeof(TimelinePlayerAuthoring))]
    public sealed class TimelineDirectorAuthoring : MonoBehaviour
    {
        [Tooltip("The Timeline Bridge that lists the director's Timeline asset.")]
        public TimelineBridgeAsset Bridge;

        [Tooltip("Other Timeline assets game code may start on this entity.")]
        public TimelineAsset[] MayStart = Array.Empty<TimelineAsset>();

        public string[] ExpectedStartWith()
        {
            var director = GetComponent<PlayableDirector>();
            var timeline = director == null ? null : director.playableAsset as TimelineAsset;
            var name = Bridge == null || director == null || !director.playOnAwake ? null : Bridge.ConstantOf(timeline);
            return name == null ? Array.Empty<string>() : new[] { name };
        }

        public string[] ExpectedMayStart()
        {
            var names = new List<string>();
            if (Bridge != null && MayStart != null)
                foreach (var timeline in MayStart)
                {
                    var name = Bridge.ConstantOf(timeline);
                    if (name != null && !names.Contains(name))
                        names.Add(name);
                }
            return names.ToArray();
        }

        public bool InSync()
        {
            var player = GetComponent<TimelinePlayerAuthoring>();
            return player != null
                && player.Library == (Bridge == null ? null : Bridge.Library)
                && Same(player.StartWith, ExpectedStartWith())
                && Same(player.MayStart, ExpectedMayStart());
        }

        public bool Sync()
        {
            var player = GetComponent<TimelinePlayerAuthoring>();
            if (player == null || InSync())
                return false;
            player.Library = Bridge == null ? null : Bridge.Library;
            player.StartWith = ExpectedStartWith();
            player.MayStart = ExpectedMayStart();
            return true;
        }

        void OnValidate()
        {
            if (!Sync())
                return;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(GetComponent<TimelinePlayerAuthoring>());
#endif
        }

        static bool Same(string[] a, string[] b)
        {
            a ??= Array.Empty<string>();
            if (a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }
    }
}
