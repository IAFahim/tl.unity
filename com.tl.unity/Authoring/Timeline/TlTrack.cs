using System.ComponentModel;
using UnityEngine;
using UnityEngine.Timeline;

namespace Tl.Unity
{
    [HideInMenu]
    [DisplayName("Tl Track")]
    [TrackClipType(typeof(TlClip))]
    public sealed class TlTrack : TrackAsset
    {
        [SerializeReference, HideInInspector] public TlClipData Template;

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
