using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Entities;

namespace Tl.Unity
{
    public struct TlLibraryHandle : IComponentData
    {
        internal GCHandle Value;

        internal TlLibrary Library => (TlLibrary)Value.Target;
    }

    public struct TlLibraryIndex : IComponentData
    {
        public NativeHashMap<Hash128, ushort> Ids;

        public ushort IdOf(Hash128 asset) => Ids.TryGetValue(asset, out var id) ? id : (ushort)0;
    }
}
