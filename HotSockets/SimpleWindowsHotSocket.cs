﻿using System;
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
    public sealed class SimpleWindowsHotSocket : IHotSocket
    {
        // TODO: How does buffer count affect throughput?

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

            _socketHandle = Windows.WSASocketW(bindTo.AddressFamily, SocketType.Dgram, ProtocolType.Udp, IntPtr.Zero, 0, Windows.SocketConstructorFlags.WSA_FLAG_OVERLAPPED | Windows.SocketConstructorFlags.WSA_FLAG_NO_HANDLE_INHERIT);

            if (_socketHandle == Windows.InvalidHandle)
                throw new SocketException();

            DisableUdpConnectionReset();

            Windows.MustSucceed(Windows.bind(_socketHandle, bindTo.Ptr, SocketAddress.Size));

            _localAddress = SocketAddress.Empty(_memoryManager);
            var localAddressSize = SocketAddress.Size;

            Windows.MustSucceed(Windows.getsockname(_socketHandle, _localAddress.Ptr, ref localAddressSize));
            LocalAddressFamily = _localAddress.AddressFamily;

            _readThread = new Thread(ReadThread)
            {
                IsBackground = true,
                Name = $"{nameof(SimpleWindowsHotSocket)} read on {bindTo}"
            };
            _consumeThread = new Thread(ConsumeThread)
            {
                IsBackground = true,
                Name = $"{nameof(SimpleWindowsHotSocket)} consume on {bindTo}"
            };
            _writeThread = new Thread(WriteThread)
            {
                IsBackground = true,
                Name = $"{nameof(SimpleWindowsHotSocket)} write on {bindTo}"
            };
            _consumeThread.Start();
            _writeThread.Start();
        }

        #region Lifecycle
        ~SimpleWindowsHotSocket() => Dispose(false);
        public void Dispose() => Dispose(true);

        private void Dispose(bool disposing)
        {
            // We close the socket immediately to ensure that none of our threads stay blocked on it.
            // This should immediately cause all threads to exit, even before we signal cancellation.
            if (_socketHandle != IntPtr.Zero)
            {
                Windows.closesocket(_socketHandle);
                _socketHandle = IntPtr.Zero;
            }

            if (disposing)
            {
                // Signal all threads to stop.
                _cts.Cancel();

                // Wait for all threads to realize we are stopping.
                _consumeThread.Join();

                if (_readThread.IsAlive)
                    _readThread.Join();

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

        private IntPtr _socketHandle;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly INativeMemoryManager _memoryManager;

        public AddressFamily LocalAddressFamily { get; }
        public ReadOnlySpan<byte> LocalAddress => _localAddress.Address;
        public ushort LocalPort => _localAddress.Port;

        private SocketAddress _localAddress;

        private void DisableUdpConnectionReset()
        {
            const uint code = unchecked(0x80000000 | 0x18000000 | 12);
            int value = 0; // 0 means "do not raise connection reset errors if UDP peer goes away"

            Windows.MustSucceed(Windows.ioctlsocket(_socketHandle, code, ref value));
        }
        #endregion

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

        // TODO: Does the core-affinity of these threads have relevance? Do we lose or gain if we co-host on same core or split them up?
        private Thread _readThread;
        private Thread _consumeThread;
        private Thread _writeThread;

        public void StartReadingPackets(IHotPacketProcessor processor)
        {
            if (_processor != null)
                throw new InvalidOperationException("Reading of packets from the IHotSocket has already been started.");

            _processor = processor;
            _readThread.Start();
        }

        // We fill these buffers with data. As long as we have buffers here, we keep reading more data from the socket.
        private readonly ConcurrentBag<Buffer> _availableReadBuffers = new ConcurrentBag<Buffer>();

        // We use this to block the read thread if no buffers are ready.
        private readonly SemaphoreSlim _availableReadBuffersReady;

        // Then we put the data here. After it is processed, it goes back to above bag.
        private readonly ConcurrentQueue<Buffer> _completedReads = new ConcurrentQueue<Buffer>();

        // We use this to block the consume thread if no buffers are ready.
        private readonly SemaphoreSlim _completedReadsReady;

        private void ReadThread()
        {
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

                var bytesRead = Windows.recvfrom(_socketHandle, buffer.Ptr, buffer.MaxLength, SocketFlags.None, buffer.Addr.Ptr, addrSizePtr);

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

        private void WriteThread()
        {
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

                var bytesWritten = Windows.sendto(_socketHandle, buffer.Ptr, buffer.Length, SocketFlags.None, buffer.Addr.Ptr, SocketAddress.Size);

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
