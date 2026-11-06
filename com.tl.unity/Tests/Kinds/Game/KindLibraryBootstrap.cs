using Tl.Unity;
using Unity.Entities;
using UnityEngine;

namespace KindGame
{
    public sealed class KindLibraryBootstrap : MonoBehaviour
    {
        void Awake()
        {
            TlGeneratedBootstrap.InstallDiscoveredModules();
            TimelineLibraryBootstrap.Ensure(World.DefaultGameObjectInjectionWorld.EntityManager, "kinds.tlib", KindLibraryBytes.Bytes());
        }

        void OnDestroy()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated)
                TimelineLibraryBootstrap.Shutdown(world.EntityManager);
        }
    }
}
