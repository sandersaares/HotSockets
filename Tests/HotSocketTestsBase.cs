using HotSockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

// TODO: Track alloc/dealloc counts.

namespace Tests
{
    /// <summary>
    /// Base class for testing different implementations of IHotSocket.
    /// </summary>
    public abstract class HotSocketTestsBase : IDisposable
    {
        /// <summary>
        /// If we need to wait for something to happen, we wait max this long (ideally less).
        /// </summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

        protected HotSocketTestsBase()
        {
        }

        public void Dispose()
        {
            Assert.AreEqual(0, ((SimpleMemoryManager)_memoryManager).Delta, "Memory manager allocation/deallocation delta at end of test was not 0");
        }

        protected abstract IHotSocket CreateSocket(SocketAddress bindTo);

        protected readonly INativeMemoryManager _memoryManager = new SimpleMemoryManager();

        private sealed class SocketHarness : IDisposable
        {
            public IHotSocket Socket { get; }
            public SocketAddress BoundTo { get; }

            // Errors remain available after disposal.
            public ConcurrentBag<Exception> Errors { get; } = new ConcurrentBag<Exception>();

            public SocketHarness(IHotSocket socket, SocketAddress boundTo)
            {
                Socket = socket;
                BoundTo = boundTo;

                socket.OnError += (s, e) => Errors.Add(e.GetException());
            }

            public void Dispose()
            {
                BoundTo.Dispose();
                Socket.Dispose();

                // Dump any errors to stdout.
                foreach (var error in Errors)
                    Console.WriteLine(error.ToString());
            }
        }

        private SocketHarness CreateSocketOnRandomPort()
        {
            using var bindTo = SocketAddress.IPv4(new byte[] { 127, 0, 0, 1 }, 0, _memoryManager);

            var socket = CreateSocket(bindTo);
            var boundTo = socket.GetLocalAddress(_memoryManager);

            return new SocketHarness(socket, boundTo);
        }

        [TestMethod]
        public void CreateAndDestroy_DoesNotThrow()
        {
            using var harness = CreateSocketOnRandomPort();

            Assert.AreEqual(0, harness.Errors.Count);
        }

        [TestMethod]
        public void PingPongWithCopy()
        {
            // A -> B: Ping
            // B -> A: Pong

            var ping = Encoding.UTF8.GetBytes("ping");
            var pong = Encoding.UTF8.GetBytes("pong");

            using var harnessA = CreateSocketOnRandomPort();
            using var harnessB = CreateSocketOnRandomPort();

            var gotPing = new ManualResetEventSlim();
            var gotPong = new ManualResetEventSlim();

            void ProcessPacketA(IHotBuffer packet, SocketAddress from)
            {
                if (gotPong.IsSet)
                    Assert.Fail("Socket A received unexpected packet after it already got 'pong' packet.");

                if (!gotPing.IsSet)
                    Assert.Fail("Socket A received unexpected packet before socket B even received 'ping' packet.");

                CollectionAssert.AreEqual(pong, packet.GetReadableSpan().ToArray());
                gotPong.Set();
            }

            void ProcessPacketB(IHotBuffer packet, SocketAddress from)
            {
                if (gotPing.IsSet)
                    Assert.Fail("Socket B received unexpected packet after it already got 'ping' packet.");

                if (gotPong.IsSet)
                    Assert.Fail("Socket B received unexpected packet after socket A already got 'pong' packet.");

                CollectionAssert.AreEqual(ping, packet.GetReadableSpan().ToArray());
                gotPing.Set();

                harnessB.Socket.SubmitWriteBuffer(harnessB.Socket.AcquireWriteBuffer().FillFrom(pong), from);
            }

            harnessA.Socket.StartReadingPackets(new DelegatingHotPacketProcessor(ProcessPacketA));
            harnessB.Socket.StartReadingPackets(new DelegatingHotPacketProcessor(ProcessPacketB));

            harnessA.Socket.SubmitWriteBuffer(harnessA.Socket.AcquireWriteBuffer().FillFrom(ping), harnessB.BoundTo);

            // Expect ping-pong to happen. If an exception was set, prefer reporting it instead of generic failure.
            var succeeded = WaitHandle.WaitAll(new[] { gotPing.WaitHandle, gotPong.WaitHandle }, Timeout);

            Assert.AreEqual(0, harnessA.Errors.Count);
            Assert.AreEqual(0, harnessB.Errors.Count);

            Assert.IsTrue(succeeded, $"Ping-pong packets travelled between two sockets. gotPing: {gotPing.IsSet} gotPong: {gotPong.IsSet}");
        }

        [TestMethod]
        public void PingPingWithForward()
        {
            // A -> B: Ping
            // B -> A: Ping

            var ping = Encoding.UTF8.GetBytes("ping");

            using var harnessA = CreateSocketOnRandomPort();
            using var harnessB = CreateSocketOnRandomPort();

            var gotPing1 = new ManualResetEventSlim();
            var gotPing2 = new ManualResetEventSlim();

            void ProcessPacketA(IHotBuffer packet, SocketAddress from)
            {
                if (gotPing2.IsSet)
                    Assert.Fail("Socket A received unexpected packet after it already got 'pong' packet.");

                if (!gotPing1.IsSet)
                    Assert.Fail("Socket A received unexpected packet before socket B even received 'ping' packet.");

                CollectionAssert.AreEqual(ping, packet.GetReadableSpan().ToArray());
                gotPing2.Set();
            }

            void ProcessPacketB(IHotBuffer packet, SocketAddress from)
            {
                if (gotPing1.IsSet)
                    Assert.Fail("Socket B received unexpected packet after it already got 'ping' packet.");

                if (gotPing2.IsSet)
                    Assert.Fail("Socket B received unexpected packet after socket A already got 'pong' packet.");

                CollectionAssert.AreEqual(ping, packet.GetReadableSpan().ToArray());
                gotPing1.Set();

                harnessB.Socket.ForwardPacketTo(packet, from);
            }

            harnessA.Socket.StartReadingPackets(new DelegatingHotPacketProcessor(ProcessPacketA));
            harnessB.Socket.StartReadingPackets(new DelegatingHotPacketProcessor(ProcessPacketB));

            harnessA.Socket.SubmitWriteBuffer(harnessA.Socket.AcquireWriteBuffer().FillFrom(ping), harnessB.BoundTo);

            // Expect ping-pong to happen. If an exception was set, prefer reporting it instead of generic failure.
            var succeeded = WaitHandle.WaitAll(new[] { gotPing1.WaitHandle, gotPing2.WaitHandle }, Timeout);

            Assert.AreEqual(0, harnessA.Errors.Count);
            Assert.AreEqual(0, harnessB.Errors.Count);

            Assert.IsTrue(succeeded, $"Ping-ping packets travelled between two sockets. gotPing1: {gotPing1.IsSet} gotPing2: {gotPing2.IsSet}");
        }
    }
}
