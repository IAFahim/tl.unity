using System;

namespace Tl.Unity
{
    [Serializable]
    public abstract class TlClipData
    {
        public abstract string MenuName { get; }
        public abstract Type ClipType { get; }
        public abstract int ClipSize { get; }
        public abstract TlClipData CreateDefault();
        public abstract void WriteClip(byte[] destination, int offset);
        public abstract void Bake(ref TlBaker baker);
    }
}
