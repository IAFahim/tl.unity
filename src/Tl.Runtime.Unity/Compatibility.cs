using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ModuleInitializerAttribute : Attribute { }
}

namespace System.Runtime.InteropServices
{
    internal static unsafe class NativeMemory
    {
        private static readonly object gate = new();
        private static readonly System.Collections.Generic.HashSet<IntPtr> allocations = new();

        static NativeMemory()
        {
            AppDomain.CurrentDomain.DomainUnload += (_, _) => ReleaseDomain();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseDomain();
        }

        private static void ReleaseDomain()
        {
            lock(gate)
            {
                foreach(var pointer in allocations) Marshal.FreeHGlobal(pointer);
                allocations.Clear();
            }
        }
        internal static void* AlignedAlloc(nuint bytes, nuint alignment)
        {
            if (alignment < (nuint)sizeof(void*) || (alignment & (alignment - 1)) != 0)
                throw new ArgumentException("Allocation alignment must be a pointer-sized power of two.");
            var address = (byte*)Marshal.AllocHGlobal(new IntPtr(checked((long)(bytes + alignment + (nuint)sizeof(void*)))));
            var aligned = (byte*)(((nuint)address + (nuint)sizeof(void*) + alignment - 1) & ~(alignment - 1));
            ((void**)aligned)[-1] = address;
            lock(gate) allocations.Add((IntPtr)address);
            return aligned;
        }

        internal static void AlignedFree(void* pointer)
        {
            if (pointer == null) return;
            var address = (IntPtr)((void**)pointer)[-1];
            lock(gate) if(allocations.Remove(address)) Marshal.FreeHGlobal(address);
        }
    }
}
