#if !NET
global using NativeMemory = Tl.NativeAlloc;
global using Unsafe = Tl.UPoly;
global using static Tl.JitHints;

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
    internal sealed class UnscopedRefAttribute : Attribute
    {
    }

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

#if !NET
    internal static class JitHints
    {
        internal const MethodImplOptions AggressiveOptimization = (MethodImplOptions)512;
    }
#endif

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

    internal static unsafe class UPoly
    {
#if NET
        public static int SizeOf<T>() where T : struct => Unsafe.SizeOf<T>();

        public static ref TTo As<TFrom, TTo>(ref TFrom source) => ref Unsafe.As<TFrom, TTo>(ref source);

        public static ref T NullRef<T>() => ref Unsafe.NullRef<T>();


        public static ref T Add<T>(ref T source, int offset) => ref Unsafe.Add(ref source, offset);

        public static ref T Add<T>(ref T source, nint offset) => ref Unsafe.Add(ref source, offset);
        public static ref T Add<T>(ref T source, nuint offset) => ref Unsafe.Add(ref source, offset);


        public static void* AsPointer<T>(ref T value) => Unsafe.AsPointer(ref value);

        public static ref T AsRef<T>(in T source) => ref Unsafe.AsRef(in source);

        public static void InitBlock(void* start, byte value, uint count) => Unsafe.InitBlock(start, value, count);

        public static T ReadUnaligned<T>(void* source) => Unsafe.ReadUnaligned<T>(source);
#else
        public static int SizeOf<T>() where T : struct => Marshal.SizeOf<T>();

        public static ref TTo As<TFrom, TTo>(ref TFrom source)
            where TFrom : unmanaged
            where TTo : unmanaged
        {
            fixed (TFrom* p = &source) return ref *(TTo*)p;
        }

        public static ref T AsRef<T>(in T source) where T : unmanaged
        {
            fixed (T* p = &source) return ref *p;
        }

        public static ref T NullRef<T>() where T : unmanaged => ref *(T*)null;

        public static ref T Add<T>(ref T source, int offset) where T : unmanaged
        {
            fixed (T* p = &source) return ref p[offset];
        }

        public static ref T Add<T>(ref T source, nint offset) where T : unmanaged
        {
            fixed (T* p = &source) return ref p[offset];
        }

        public static ref T Add<T>(ref T source, nuint offset) where T : unmanaged
        {
            fixed (T* p = &source) return ref p[offset];
        }

        public static void* AsPointer<T>(ref T value) where T : unmanaged
        {
            fixed (T* p = &value) return p;
        }

        public static void InitBlock(void* start, byte value, uint count)
            => new Span<byte>(start, (int)count).Clear();

        public static T ReadUnaligned<T>(void* source) where T : unmanaged
            => MemoryMarshal.Read<T>(new ReadOnlySpan<byte>(source, sizeof(T)));
#endif
    internal static Span<T> Pin<T>(ref T value) where T : unmanaged
    {
        fixed (T* p = &value) return new Span<T>(p, 1);
    }

    internal static ReadOnlySpan<T> PinRead<T>(in T value) where T : unmanaged
    {
        fixed (T* p = &value) return new ReadOnlySpan<T>(p, 1);
    }

    }
}
