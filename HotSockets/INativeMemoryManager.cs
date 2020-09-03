using System;

namespace HotSockets
{
    /// <summary>
    /// Enables different mechanisms for allocating unsafe memory to be implemented.
    /// </summary>
    public interface INativeMemoryManager
    {
        IntPtr Allocate(int size);
        void Deallocate(IntPtr ptr);
    }
}
