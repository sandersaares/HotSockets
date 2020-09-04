using System;

namespace HotSockets
{
    public static class HotExtensions
    {
        /// <summary>
        /// Fills the buffer with data and sets the length.
        /// This is just a shortcut if you already have the data ready in a different buffer.
        /// </summary>
        public static IHotBuffer FillFrom(this IHotBuffer buffer, ReadOnlySpan<byte> data)
        {
            var destination = buffer.SetLengthAndGetWritableSpan(data.Length);
            data.CopyTo(destination);

            return buffer;
        }

        /// <summary>
        /// Creates a NEW INSTANCE of SocketAddress holding the socket's local address.
        /// </summary>
        public static SocketAddress GetLocalAddress(this IHotSocket socket, INativeMemoryManager memoryManager)
        {
            return SocketAddress.New(socket.LocalAddress, socket.LocalPort, memoryManager);
        }
    }
}
