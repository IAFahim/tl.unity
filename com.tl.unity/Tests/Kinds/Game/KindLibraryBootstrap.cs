using Tl.Unity;
using UnityEngine;

namespace KindGame
{
    public sealed class KindLibraryBootstrap : MonoBehaviour
    {
        void Awake()
        {
            TimelineLibraryBootstrap.Use("kinds.tlib", KindLibraryBytes.Bytes());
        }

        void OnDestroy()
        {
            TimelineLibraryBootstrap.Release();
        }
    }
}
