using UnityEngine;
using UnityEngine.Timeline;

public sealed class HasteAuthoring : MonoBehaviour
{
    [Tooltip("The buff timeline to start. Add it to Tl Also Plays on this object's director too.")]
    public TimelineAsset Haste;
    [Tooltip("Duel tick of the first Haste start.")]
    public int First = 100;
    [Tooltip("Duel tick of the second start; it restarts Haste, the bonus does not stack.")]
    public int Second = 250;
}
