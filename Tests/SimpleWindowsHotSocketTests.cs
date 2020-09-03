using HotSockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Text;
using System.Threading;

namespace Tests
{
    [TestClass]
    public sealed class SimpleWindowsHotSocketTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

        private static INativeMemoryManager _memoryManager = new SimpleMemoryManager();

        [TestMethod]
        public void CreateAndDestroy_DoesNotThrow()
        {
            using var bindTo = UnsafeSocketAddress.IPv4(new byte[] { 127, 0, 0, 1 }, 0, _memoryManager);

            using (var socket = new SimpleWindowsHotSocket(bindTo, _memoryManager))
            {
            }
        }

        [TestMethod]
        public void PingPong()
        {
            Exception? exception = null;

            // A -> B: Ping
            // B -> A: Pong

            var ping = Encoding.UTF8.GetBytes("ping");
            var pong = Encoding.UTF8.GetBytes("pong");

            using var bindToA = UnsafeSocketAddress.IPv4(new byte[] { 127, 0, 0, 1 }, 0, _memoryManager);
            using var bindToB = UnsafeSocketAddress.IPv4(new byte[] { 127, 0, 0, 1 }, 0, _memoryManager);

            using var socketA = new SimpleWindowsHotSocket(bindToA, _memoryManager);
            using var socketB = new SimpleWindowsHotSocket(bindToA, _memoryManager);

            // Addresses they are really bound to.
            using var boundA = UnsafeSocketAddress.New(socketA.LocalAddress, socketA.LocalPort, _memoryManager);
            using var boundB = UnsafeSocketAddress.New(socketB.LocalAddress, socketB.LocalPort, _memoryManager);

            socketA.OnError += (s, e) => exception ??= e.GetException();
            socketB.OnError += (s, e) => exception ??= e.GetException();

            var gotPing = new ManualResetEventSlim();
            var gotPong = new ManualResetEventSlim();

            void ProcessPacketA(IHotBuffer packet, UnsafeSocketAddress from)
            {
                if (gotPong.IsSet)
                    Assert.Fail("Socket A received unexpected packet after it already got 'pong' packet.");

                if (!gotPing.IsSet)
                    Assert.Fail("Socket A received unexpected packet before socket B even received 'ping' packet.");

                CollectionAssert.AreEqual(pong, packet.GetReadableSpan().ToArray());
                gotPong.Set();
            }

            void ProcessPacketB(IHotBuffer packet, UnsafeSocketAddress from)
            {
                if (gotPing.IsSet)
                    Assert.Fail("Socket B received unexpected packet after it already got 'ping' packet.");

                if (gotPong.IsSet)
                    Assert.Fail("Socket B received unexpected packet after socket A already got 'pong' packet.");

                CollectionAssert.AreEqual(ping, packet.GetReadableSpan().ToArray());
                gotPing.Set();

                socketB.SubmitWriteBuffer(socketB.AcquireWriteBuffer().FillFrom(pong), from);
            }

            socketA.StartReadingPackets(new DelegatingHotPacketProcessor(ProcessPacketA));
            socketB.StartReadingPackets(new DelegatingHotPacketProcessor(ProcessPacketB));

            socketA.SubmitWriteBuffer(socketA.AcquireWriteBuffer().FillFrom(ping), boundB);

            // Expect ping-pong to happen. If an exception was set, prefer reporting it instead of generic failure.
            var succeeded = WaitHandle.WaitAll(new[] { gotPing.WaitHandle, gotPong.WaitHandle }, Timeout);

            if (exception != null)
                Assert.Fail(exception.ToString());

            Assert.IsTrue(succeeded, $"Ping-pong packets travelled between two sockets. gotPing: {gotPing.IsSet} gotPong: {gotPong.IsSet}");
        }
    }
}
