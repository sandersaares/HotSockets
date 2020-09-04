using System;
using System.IO;
using System.Net.Sockets;

namespace HotSockets
{
    public interface IHotSocket : IDisposable
    {
        AddressFamily LocalAddressFamily { get; }

        /// <summary>
        /// The address the socket is bound to.
        /// </summary>
        ReadOnlySpan<byte> LocalAddress { get; }

        /// <summary>
        /// The port the socket is bound to.
        /// </summary>
        ushort LocalPort { get; }

        /// <summary>
        /// Starts reading packets from the socket, delivering them to the provided packet processor.
        /// You can only call this once. Reading will not stop until the socket is disposed.
        /// </summary>
        void StartReadingPackets(IHotPacketProcessor processor);

        /// <summary>
        /// Obtains a buffer that can be used to later submit data to the socket.
        /// If no buffers are available, blocks until a buffer becomes available.
        /// </summary>
        /// <remarks>
        /// Acquired buffers become locked until they are submitted back to the socket.
        /// </remarks>
        /// <exception cref="OperationCanceledException">Thrown if the socket is closed while blocked in this call.</exception>
        IHotBuffer AcquireWriteBuffer();

        // TODO: IHotBuffer ConvertReadBufferToWriteBuffer(IHotBuffer buffer);

        /// <summary>
        /// If an acquired buffer cannot, for whatever reason, be submitted, you can release it here.
        /// </summary>
        void ReleaseWriteBuffer(IHotBuffer buffer);

        /// <summary>
        /// Enqueues a buffer for delivery to the indicated remote address.
        /// </summary>
        /// <remarks>
        /// The socket does NOT take ownership of the address.
        /// The socket always owns all buffers, so it remains owner of buffer, as was the case before.
        /// </remarks>
        void SubmitWriteBuffer(IHotBuffer buffer, SocketAddress to);

        /// <summary>
        /// If there is any type of error during processing, it is reported via this event.
        /// The reporting is synchronous, so don't cause delays in the event handler and slow down data processing!
        /// 
        /// The socket will attempt to ignore all errors where possible, so this event is just for logging.
        /// </summary>
        event EventHandler<ErrorEventArgs>? OnError;
    }
}
