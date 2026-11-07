using System;
using Unity.Entities;

namespace Tl
{
    [Serializable]
    public struct TlTimeline : IEquatable<TlTimeline>
    {
        public Hash128 Asset;

        public bool IsValid => Asset.IsValid;

        public bool Equals(TlTimeline other) => Asset.Equals(other.Asset);

        public override bool Equals(object obj) => obj is TlTimeline other && Equals(other);

        public override int GetHashCode() => Asset.GetHashCode();
    }
}
