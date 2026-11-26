namespace Tl;

public static unsafe partial class GlideKernel
{
    [MethodImpl(KindStore.Inline)]
    public static void RunScalar(ulong* slots, int count, ulong worldReverse)
    {
        for (var i = 0; i < count; i++) slots[i] = SlotWord.Glide(slots[i], worldReverse);
    }
}
