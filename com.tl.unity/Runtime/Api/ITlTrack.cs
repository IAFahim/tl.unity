namespace Tl
{
    public interface ITlTrack<TClip> : ITrack<TlNoSettings<TClip>, TClip>
        where TClip : unmanaged
    {
    }

    public struct TlNoSettings<TClip> : IBlend<TClip>
        where TClip : unmanaged
    {
        public void Blend(in TClip first, in TClip second, float factor, out TClip result)
        {
            result = first;
        }
    }
}
