using UnityEngine;

public sealed class MotionOverlay : MonoBehaviour
{
    void OnGUI()
    {
        GUI.Label(new Rect(12, 10, 900, 22), "Motion pack: Glide moves the cube, Breathe scales and tints the sphere, Hop spins the capsule; a Priority 5 Move pulls it right.");
        GUI.Label(new Rect(12, 30, 900, 22), "No game code: only Motion tracks (Timeline + menu > Motion).");
    }
}
