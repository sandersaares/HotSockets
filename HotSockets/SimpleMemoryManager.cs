using System;
using System.Runtime.InteropServices;

namespace HotSockets
{
    /// <summary>
    /// Allocates and deallocates on the native heap, no fancy business.
    /// </summary>
    public sealed class SimpleMemoryManager : INativeMemoryManager
    {
        public IntPtr Allocate(int size) => Marshal.AllocHGlobal(size);
        public void Deallocate(IntPtr ptr) => Marshal.FreeHGlobal(ptr);
    }
}
