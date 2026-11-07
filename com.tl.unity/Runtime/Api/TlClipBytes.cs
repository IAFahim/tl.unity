using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Tl.Unity
{
    public static class TlClipBytes
    {
        public static int SizeOf<T>() where T : unmanaged
        {
            return UnsafeUtility.SizeOf<T>();
        }

        public static unsafe void Write<T>(in T clip, byte[] destination, int offset) where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>();
            if (destination == null || offset < 0 || offset + size > destination.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            var copy = clip;
            fixed (byte* target = &destination[offset])
                UnsafeUtility.MemCpy(target, &copy, size);
        }
    }
}
