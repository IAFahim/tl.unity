using System.ComponentModel;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Tl.Unity
{
    [HideInMenu]
    [DisplayName("Tl Track")]
    [TrackClipType(typeof(TlClip))]
    public sealed class TlTrack : TrackAsset
    {
        [SerializeReference, HideInInspector] public TlClipData Template;

        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            return ScriptPlayable<TlPreviewMixer>.Create(graph, inputCount);
        }

        public override void GatherProperties(PlayableDirector director, IPropertyCollector driver)
        {
            if (director != null)
                foreach (var property in TlPreviewProperties.Transform)
                    driver.AddFromName<Transform>(director.gameObject, property);
            base.GatherProperties(director, driver);
        }

        protected override void OnCreateClip(TimelineClip clip)
        {
            if (Template == null)
                return;
            if (clip.asset is TlClip tl && tl.Data == null)
                tl.Data = Template.CreateDefault();
            clip.displayName = TlMenu.ShortName(Template.MenuName);
        }
    }
}
