using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HotSockets
{
    /// <summary>
    /// Allocates and deallocates on the native heap, no fancy business.
    /// </summary>
    public sealed class SimpleMemoryManager : INativeMemoryManager
    {
        // Internal for tests: delta of allocations-deallocations.
        internal long Delta;

        public IntPtr Allocate(int size)
        {
            Interlocked.Increment(ref Delta);
            return Marshal.AllocHGlobal(size);
        }

        public void Deallocate(IntPtr ptr)
        {
            Marshal.FreeHGlobal(ptr);
            Interlocked.Decrement(ref Delta);
        }
    }
}
