namespace Tl
{
    public interface ITlBaker<TClip>
        where TClip : unmanaged
    {
        void Bake(ref TlBaker baker, in TClip clip);
    }
}
