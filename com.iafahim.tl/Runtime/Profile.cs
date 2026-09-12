#pragma warning disable CS0436
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace System.Threading
{
    internal static class Interlocked
    {
        private static readonly object Gate = new();

        public static int CompareExchange(ref int location1, int value, int comparand)
        {
            lock (Gate)
            {
                var current = location1;
                if (current == comparand) location1 = value;
                return current;
            }
        }

        public static long CompareExchange(ref long location1, long value, long comparand)
        {
            lock (Gate)
            {
                var current = location1;
                if (current == comparand) location1 = value;
                return current;
            }
        }

        public static int Exchange(ref int location1, int value)
        {
            lock (Gate)
            {
                var current = location1;
                location1 = value;
                return current;
            }
        }

        public static long Exchange(ref long location1, long value)
        {
            lock (Gate)
            {
                var current = location1;
                location1 = value;
                return current;
            }
        }

        public static nint Exchange(ref nint location1, nint value)
        {
            lock (Gate)
            {
                var current = location1;
                location1 = value;
                return current;
            }
        }

        public static int Increment(ref int location) => Add(ref location, 1);

        public static int Decrement(ref int location) => Add(ref location, -1);

        public static int Add(ref int location1, int value)
        {
            lock (Gate)
            {
                location1 += value;
                return location1;
            }
        }

        public static long Add(ref long location1, long value)
        {
            lock (Gate)
            {
                location1 += value;
                return location1;
            }
        }

        public static void MemoryBarrier() => Thread.MemoryBarrier();
    }
}

namespace System.Runtime.CompilerServices
{
    [Flags]
    internal enum MethodImplOptions
    {
        Unmanaged = 0x0004,
        NoInlining = 0x0008,
        ForwardRef = 0x0010,
        Synchronized = 0x0020,
        NoOptimization = 0x0040,
        PreserveSig = 0x0080,
        AggressiveInlining = 0x0100,
        AggressiveOptimization = 0x0200,
        InternalCall = 0x1000,
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
    internal sealed class MethodImplAttribute : Attribute
    {
        public MethodImplOptions Value { get; }

        public MethodImplAttribute() => Value = 0;

        public MethodImplAttribute(MethodImplOptions options) => Value = options;
    }
}

namespace System.Runtime.InteropServices
{
    internal static unsafe class NativeMemory
    {
        public static void* AlignedAlloc(nuint byteCount, nuint alignment) =>
            UnsafeUtility.Malloc((long)byteCount, (int)alignment, Allocator.Persistent);

        public static void AlignedFree(void* ptr) =>
            UnsafeUtility.Free(ptr, Allocator.Persistent);
    }
}
