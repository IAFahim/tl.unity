namespace Tl;

public interface IBlend<TClip>
    where TClip : unmanaged
{
    void Blend(in TClip first, in TClip second, float factor, out TClip result);
}
