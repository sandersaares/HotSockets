using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace HotSockets
{
    /// <summary>
    /// Very basic Windows implementation using blocking I/O. Only UDP is supported.
    /// </summary>
    /// <remarks>
    /// Either 1 or NumCpus native sockets are bound to the same port (depending on EnableMultiCore flag).
    /// 
    /// One thread per native socket receives packets from OS.
    /// One thread per native socket sends queued packets.
    /// One thread executes callbacks on received packets.
    /// 
    /// Queues and buffers are shared between all native sockets.
    /// 
    /// No consideration given for scheduling - the OS will do as the OS will do.
    /// </remarks>
    public sealed class SimpleWindowsHotSocket : IHotSocket
    {
        // One set of buffers for read, another set for write.
        private int _bufferCount => HotSocketFineTuning.BufferCount;

        public SimpleWindowsHotSocket(SocketAddress bindTo, INativeMemoryManager memoryManager)
        {
            _memoryManager = memoryManager;

            for (var i = 0; i < _bufferCount; i++)
            {
                _availableReadBuffers.Add(new Buffer(_memoryManager));
                _availableWriteBuffers.Add(new Buffer(_memoryManager));
            }

            _availableReadBuffersReady = new SemaphoreSlim(_bufferCount, _bufferCount);
            _availableWriteBuffersReady = new SemaphoreSlim(_bufferCount, _bufferCount);
            _completedReadsReady = new SemaphoreSlim(0, _bufferCount);
            // *2 because we allow read buffers to be reused as write buffers temporarily.
            _pendingWriteBuffersReady = new SemaphoreSlim(0, _bufferCount * 2);

            var nativeSocketCount = HotSocketFineTuning.EnableMultiCore ? Environment.ProcessorCount : 1;
            var isSharedEndpoint = nativeSocketCount > 1;

            _nativeSockets = new NativeSocket[nativeSocketCount];

            for (var i = 0; i < nativeSocketCount; i++)
            {
                // We bind the first one to whatever bindTo says (e.g. auto assigned port on any IP address).
                // Then we bind the next ones to whatever the first one got bound to (e.g. specific port on specific IP address).

                var bindThisOneTo = i == 0 ? bindTo : _nativeSockets[0].BoundTo;
                _nativeSockets[i] = new NativeSocket(bindThisOneTo, isSharedEndpoint, _memoryManager);
            }

            _localAddress = _nativeSockets[0].BoundTo;

            _readThreads = new Thread[nativeSocketCount];
            _consumeThread = new Thread(ConsumeThread)
            {
                IsBackground = true,
                Name = $"{nameof(SimpleWindowsHotSocket)} consume on {bindTo}"
            };
            _writeThreads = new Thread[nativeSocketCount];

            for (var i = 0; i < nativeSocketCount; i++)
            {
                _readThreads[i] = new Thread(ReadThread)
                {
                    IsBackground = true,
                    Name = $"{nameof(SimpleWindowsHotSocket)} read #{i} on {bindTo}"
                };
                _writeThreads[i] = new Thread(WriteThread)
                {
                    IsBackground = true,
                    Name = $"{nameof(SimpleWindowsHotSocket)} write #{i} on {bindTo}"
                };
            }

            _consumeThread.Start();

            for (var i = 0; i < nativeSocketCount; i++)
                _writeThreads[i].Start(i);
        }

        #region Lifecycle
        ~SimpleWindowsHotSocket() => Dispose(false);
        public void Dispose() => Dispose(true);

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                // We close the sockets immediately to ensure that none of our threads stay blocked on them.
                // This should immediately cause all threads to exit, even before we signal cancellation.
                foreach (var socket in _nativeSockets)
                    socket.Dispose();

                // Signal all threads to stop.
                _cts.Cancel();

                // Wait for all threads to realize we are stopping.
                _consumeThread.Join();

                foreach (var thread in _readThreads)
                {
                    if (thread.IsAlive)
                        thread.Join();
                }

                foreach (var thread in _writeThreads)
                {
                    if (thread.IsAlive)
                        thread.Join();
                }

                _cts.Dispose();

                _localAddress?.Dispose();

                // Get rid of all buffers.
                foreach (var buffer in _availableReadBuffers)
                    buffer.Dispose();

                foreach (var buffer in _availableWriteBuffers)
                    buffer.Dispose();

                foreach (var buffer in _completedReads)
                    buffer.Dispose();

                foreach (var buffer in _pendingWriteBuffers)
                    buffer.Dispose();

                GC.SuppressFinalize(this);
            }
        }

        private NativeSocket[] _nativeSockets;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly INativeMemoryManager _memoryManager;

        public AddressFamily LocalAddressFamily => _localAddress.AddressFamily;
        public ReadOnlySpan<byte> LocalAddress => _localAddress.Address;
        public ushort LocalPort => _localAddress.Port;

        private SocketAddress _localAddress;
        #endregion

        private sealed class NativeSocket : IDisposable
        {
            public IntPtr Handle { get; private set; }
            public SocketAddress BoundTo { get; private set; }

            public NativeSocket(SocketAddress bindTo, bool isSharedEndpoint, INativeMemoryManager memoryManager)
            {
                Handle = Windows.WSASocketW(bindTo.AddressFamily, SocketType.Dgram, ProtocolType.Udp, IntPtr.Zero, 0, Windows.SocketConstructorFlags.WSA_FLAG_OVERLAPPED | Windows.SocketConstructorFlags.WSA_FLAG_NO_HANDLE_INHERIT);

                if (Handle == Windows.InvalidHandle)
                    throw new SocketException();

                DisableUdpConnectionReset();

                if (isSharedEndpoint)
                    EnableEndpointSharing();

                Windows.MustSucceed(Windows.bind(Handle, bindTo.Ptr, SocketAddress.Size));

                BoundTo = SocketAddress.Empty(memoryManager);
                var localAddressSize = SocketAddress.Size;

                Windows.MustSucceed(Windows.getsockname(Handle, BoundTo.Ptr, ref localAddressSize));
            }

            ~NativeSocket() => Dispose(false);
            public void Dispose() => Dispose(true);

            private void Dispose(bool disposing)
            {
                if (Handle != IntPtr.Zero)
                {
                    Windows.closesocket(Handle);
                    Handle = IntPtr.Zero;
                }

                if (disposing)
                {
                    BoundTo.Dispose();

                    GC.SuppressFinalize(this);
                }
            }

            private void DisableUdpConnectionReset()
            {
                const uint code = unchecked(0x80000000 | 0x18000000 | 12);
                int value = 0; // 0 means "do not raise connection reset errors if UDP peer goes away"

                Windows.MustSucceed(Windows.ioctlsocket(Handle, code, ref value));
            }

            private void EnableEndpointSharing()
            {
                int value = 1; // 1 means yes

                Windows.MustSucceed(Windows.setsockopt(Handle, (int)SocketOptionLevel.Socket, (int)SocketOptionName.ReuseAddress, ref value, sizeof(int)));
            }
        }

        private sealed class Buffer : IHotBuffer
        {
            private const ushort BufferLength = 1500;

            public Buffer(INativeMemoryManager memoryManager)
            {
                _memoryManager = memoryManager;

                Ptr = _memoryManager.Allocate(BufferLength);
                Addr = SocketAddress.Empty(_memoryManager);
            }

            ~Buffer() => Dispose(false);
            public void Dispose() => Dispose(true);

            private void Dispose(bool disposing)
            {
                if (disposing)
                {
                    GC.SuppressFinalize(this);

                    Addr?.Dispose();
                }

                if (Ptr != IntPtr.Zero)
                {
                    _memoryManager.Deallocate(Ptr);
                    Ptr = IntPtr.Zero;
                }
            }

            private readonly INativeMemoryManager _memoryManager;

            // Pointer to the data region.
            public IntPtr Ptr { get; private set; }

            // Most recently associated address (instance reused to avoid repeated allocations).
            public SocketAddress Addr { get; private set; }

            // Number of bytes currently in use.
            public int Length { get; internal set; }

            public int MaxLength => BufferLength;

            // Whether this is a read buffer temporarily made into a write buffer.
            public bool IsReadBufferReusedAsWriteBuffer;

            // This is increased when the buffer is redirected to be a write buffer.
            // The epoch change indicates to the consume thread that the buffer is not to be released.
            public long ReadBufferEpoch;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe ReadOnlySpan<byte> GetReadableSpan() => new ReadOnlySpan<byte>(Ptr.ToPointer(), Length);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe Span<byte> SetLengthAndGetWritableSpan(int length)
            {
                if (length < 0)
                    throw new ArgumentOutOfRangeException(nameof(length), "Requested write is negative. What?");

                if (length > MaxLength)
                    throw new ArgumentOutOfRangeException(nameof(length), "Requested write is larger than the available buffer.");

                Length = length;
                return new Span<byte>(Ptr.ToPointer(), length);
            }
        }

        #region Reading
        private IHotPacketProcessor? _processor;

        private Thread[] _readThreads;
        private Thread _consumeThread;
        private Thread[] _writeThreads;

        public void StartReadingPackets(IHotPacketProcessor processor)
        {
            if (_processor != null)
                throw new InvalidOperationException("Reading of packets from the IHotSocket has already been started.");

            _processor = processor;

            for (var i = 0; i < _readThreads.Length; i++)
                _readThreads[i].Start(i);
        }

        // We fill these buffers with data. As long as we have buffers here, we keep reading more data from the socket.
        private readonly ConcurrentBag<Buffer> _availableReadBuffers = new ConcurrentBag<Buffer>();

        // We use this to block the read thread if no buffers are ready.
        private readonly SemaphoreSlim _availableReadBuffersReady;

        // Then we put the data here. After it is processed, it goes back to above bag.
        private readonly ConcurrentQueue<Buffer> _completedReads = new ConcurrentQueue<Buffer>();

        // We use this to block the consume thread if no buffers are ready.
        private readonly SemaphoreSlim _completedReadsReady;

        private void ReadThread(object? boxedIndex)
        {
            var socketIndex = (int)boxedIndex!;

            var addrSize = SocketAddress.Size;
            IntPtr addrSizePtr;

            unsafe
            {
                addrSizePtr = new IntPtr(&addrSize);
            }

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _availableReadBuffersReady.Wait(_cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    // This is fine - we are shutting down.
                    break;
                }

                // If the semaphore got decremented, there must be a buffer available for us.
                Buffer buffer;
                HotHelpers.MustSucceed(_availableReadBuffers.TryTake(out buffer!), "Acquire read buffer after confirmation that one is available");

                var bytesRead = Windows.recvfrom(_nativeSockets[socketIndex].Handle, buffer.Ptr, buffer.MaxLength, SocketFlags.None, buffer.Addr.Ptr, addrSizePtr);

                if (bytesRead <= 0)
                {
                    // We should never get 0 to signal disconnect because this is UDP and we do not close the socket until threads finish.
                    // Obviously, there was some sort of error. This is not necessarily critical error - the next read may work, so just loop.

                    try
                    {
                        var errorCode = Windows.GetLastSocketError();

                        // This can be OK if we are shutting down.
                        if (errorCode == SocketError.Interrupted && _cts.IsCancellationRequested)
                            break;

                        InvokeErrorEvent(new SocketException((int)errorCode));

                        continue;
                    }
                    finally
                    {
                        _availableReadBuffers.Add(buffer); // Put it back - we ended up not using it.
                        _availableReadBuffersReady.Release();
                    }
                }

                buffer.Length = bytesRead;
                _completedReads.Enqueue(buffer);
                _completedReadsReady.Release();
            }
        }

        private void ConsumeThread()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _completedReadsReady.Wait(_cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    // This is fine - we are shutting down.
                    break;
                }

                // If the semaphore got decremented, there must be a buffer available for us.
                Buffer buffer;
                HotHelpers.MustSucceed(_completedReads.TryDequeue(out buffer!), "Acquire completed read buffer after confirmation that one is available");

                long epoch = buffer.ReadBufferEpoch;

                try
                {
                    _processor!.ProcessPacket(buffer, buffer.Addr);
                }
                catch (Exception ex)
                {
                    InvokeErrorEvent(ex);
                }

                // If it was taken away during processing, the epoch was incremented and it is no longer ours to use.
                if (buffer.ReadBufferEpoch == epoch)
                {
                    _availableReadBuffers.Add(buffer);
                    _availableReadBuffersReady.Release();
                }
            }
        }
        #endregion

        #region Writing
        // Ready to supply buffers, available for anyone who wants to write some data to the socket.
        private readonly ConcurrentBag<Buffer> _availableWriteBuffers = new ConcurrentBag<Buffer>();

        // We use this to block write buffer acquisition if no buffers are ready.
        private readonly SemaphoreSlim _availableWriteBuffersReady;

        // Buffers that have been filled with data and are awaiting final submission to the socket.
        private readonly ConcurrentQueue<Buffer> _pendingWriteBuffers = new ConcurrentQueue<Buffer>();

        // We use this to block the write thread if no buffers are ready.
        private readonly SemaphoreSlim _pendingWriteBuffersReady;

        public IHotBuffer AcquireWriteBuffer()
        {
            _availableWriteBuffersReady.Wait(_cts.Token);

            Buffer buffer;
            HotHelpers.MustSucceed(_availableWriteBuffers.TryTake(out buffer!), "Acquire available write buffer after confirmation that one is available.");

            return buffer;
        }

        public void ReleaseWriteBuffer(IHotBuffer buffer)
        {
            var myBuffer = (Buffer)buffer;

            if (myBuffer.IsReadBufferReusedAsWriteBuffer)
            {
                myBuffer.IsReadBufferReusedAsWriteBuffer = false;

                _availableReadBuffers.Add(myBuffer);
                _availableReadBuffersReady.Release();
            }
            else
            {
                _availableWriteBuffers.Add(myBuffer);
                _availableWriteBuffersReady.Release();
            }
        }

        public void SubmitWriteBuffer(IHotBuffer buffer, SocketAddress to)
        {
            var myBuffer = (Buffer)buffer;
            to.CopyTo(myBuffer.Addr);

            _pendingWriteBuffers.Enqueue(myBuffer);
            _pendingWriteBuffersReady.Release();
        }

        public void ForwardPacketTo(IHotBuffer buffer, SocketAddress to)
        {
            var myBuffer = (Buffer)buffer;
            to.CopyTo(myBuffer.Addr);

            Interlocked.Increment(ref myBuffer.ReadBufferEpoch);
            myBuffer.IsReadBufferReusedAsWriteBuffer = true;

            _pendingWriteBuffers.Enqueue(myBuffer);
            _pendingWriteBuffersReady.Release();
        }

        private void WriteThread(object? boxedIndex)
        {
            var socketIndex = (int)boxedIndex!;

            while (true)
            {
                try
                {
                    _pendingWriteBuffersReady.Wait(_cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    // This is fine - we are shutting down.
                    break;
                }

                // If the semaphore got decremented, there must be a buffer available for us.
                Buffer buffer;
                HotHelpers.MustSucceed(_pendingWriteBuffers.TryDequeue(out buffer!), "Acquire pending write buffer after confirmation that one is available");

                var bytesWritten = Windows.sendto(_nativeSockets[socketIndex].Handle, buffer.Ptr, buffer.Length, SocketFlags.None, buffer.Addr.Ptr, SocketAddress.Size);

                try
                {
                    if (bytesWritten == (int)SocketError.SocketError)
                    {
                        var errorCode = Windows.GetLastSocketError();

                        // This can be OK if we are shutting down.
                        if (errorCode == SocketError.Interrupted && _cts.IsCancellationRequested)
                            break;

                        InvokeErrorEvent(new SocketException((int)errorCode));
                    }
                    else if (bytesWritten != buffer.Length)
                    {
                        InvokeErrorEvent(new HotSocketException($"sendto() returned {bytesWritten} instead of expected {buffer.Length}."));
                    }
                }
                finally
                {
                    ReleaseWriteBuffer(buffer);
                }
            }
        }
        #endregion

        public event EventHandler<ErrorEventArgs>? OnError;

        private void InvokeErrorEvent(Exception ex)
        {
            var errorEvent = OnError;

            try
            {
                errorEvent?.Invoke(this, new ErrorEventArgs(ex));
            }
            catch
            {
                // No.
            }
        }
    }
}
