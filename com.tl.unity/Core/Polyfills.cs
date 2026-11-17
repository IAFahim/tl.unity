#if !NET
global using NativeMemory = Tl.NativeAlloc;
global using Unsafe = Tl.UPoly;

namespace System.Runtime.CompilerServices
{
    internal sealed class IsExternalInit
    {
    }

    internal sealed class SkipLocalsInitAttribute : Attribute
    {
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    internal sealed class UnconditionalSuppressMessageAttribute : Attribute
    {
        public UnconditionalSuppressMessageAttribute(string category, string checkId)
        {
        }

        public string? Justification { get; set; }
    }
}
#endif
namespace Tl
{
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    internal static unsafe class NativeAlloc
    {
#if NET
        public static void* Alloc(nuint size) => NativeMemory.Alloc(size);
        public static void* AllocZeroed(nuint size) => NativeMemory.AllocZeroed(size);
        public static void* AlignedAlloc(nuint size, nuint alignment) => NativeMemory.AlignedAlloc(size, alignment);
        public static void AlignedFree(void* ptr) => NativeMemory.AlignedFree(ptr);
        public static void Free(void* ptr) => NativeMemory.Free(ptr);
        public static void Clear(void* ptr, nuint bytes) => new Span<byte>(ptr, (int)bytes).Clear();
#else
        public static void* Alloc(nuint size) => AlignedAlloc(size, 16);

        public static void* AllocZeroed(nuint size)
        {
            var block = AlignedAlloc(size, 16);
            new Span<byte>(block, (int)size).Clear();
            return block;
        }

        public static void* AlignedAlloc(nuint size, nuint alignment)
        {
            var raw = (byte*)Marshal.AllocHGlobal((nint)(size + alignment + (nuint)sizeof(nuint)));
            var aligned = (byte*)(((nuint)raw + (nuint)sizeof(nuint) + alignment - 1) & ~(alignment - 1));
            ((nuint*)aligned)[-1] = (nuint)(aligned - raw);
            return aligned;
        }

        public static void AlignedFree(void* ptr)
        {
            if (ptr == null) return;
            Marshal.FreeHGlobal((nint)((byte*)ptr - ((nuint*)ptr)[-1]));
        }

        public static void Free(void* ptr)
        {
            if (ptr == null) return;
            Marshal.FreeHGlobal((nint)((byte*)ptr - ((nuint*)ptr)[-1]));
        }
        public static void Clear(void* ptr, nuint bytes) => new Span<byte>(ptr, (int)bytes).Clear();
#endif
    }

#if !NET
    internal static unsafe class UPoly
    {
        public static T ReadUnaligned<T>(void* source) where T : unmanaged
            => MemoryMarshal.Read<T>(new ReadOnlySpan<byte>(source, sizeof(T)));
    }
#endif
}
