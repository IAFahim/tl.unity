using Tl.Unity;
using UnityEngine;

namespace Demo
{
    public sealed class LibraryAuthoring : MonoBehaviour
    {
        public TextAsset Library;

        void Awake()
        {
            TimelineLibraryBootstrap.Use("game.bytes", Library.bytes);
        }

        void OnDestroy()
        {
            TimelineLibraryBootstrap.Release();
        }
    }
}
