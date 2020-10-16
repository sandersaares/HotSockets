using BenchmarkDotNet.Attributes;
using HotSockets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Benchmark
{
    // TODO: Display loss% as column
    // TODO: Display outoforder% as column
    // NOTE: Measuring reorder% only makes sense if we have one write thread.
    // TODO: Display errorcount as column
    // TODO: Calculate PPS

    /// <summary>
    /// We send N packets from socket A to socket B and measure how long it takes. That's it.
    /// </summary>
    [SimpleJob(BenchmarkDotNet.Engines.RunStrategy.ColdStart, launchCount: 50, warmupCount: 0, targetCount: 1, invocationCount: 1)]
    public class TheNeedForSpeed : IDisposable
    {
        [Params(500_000)]
        public int PacketCount;

        [Params(250)]
        public int PacketSize;

        // SimpleWindowsHotSocket: more threads makes it slower.
        [Params(1, 4)]
        public int SendThreadCount;

        // SimpleWindowsHotSocket: does not have any performance impact under simple benchmarks.
        [Params(64, 1024)]
        public int BufferCount;

        [Params(false, true)]
        public bool MultiCore;

        /// <summary>
        /// If we think the benchmark has finished but it doesn't seem to be finishing, we give it this much time before we call it quits.
        /// This might happen because some packets got lost on the way, so we'll never see all of them arrive. That's okay - we measure loss%, too.
        /// </summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

        /// <summary>
        /// When we are waiting for timeout, we consider the receiving completed if no more packets have arrived in this amount of time.
        /// </summary>
        private static readonly TimeSpan CompletionEvaluationInterval = TimeSpan.FromSeconds(0.1);

        static TheNeedForSpeed()
        {
        }

#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
        public TheNeedForSpeed()
#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
        {
        }

        [IterationSetup]
        public void SetupBenchmark()
        {
            HotSocketFineTuning.BufferCount = BufferCount;
            HotSocketFineTuning.EnableMultiCore = MultiCore;

            using var bindTo = SocketAddress.IPv4(new byte[] { 127, 0, 0, 1 }, 0, _memoryManager);

            _socketA = new SimpleWindowsHotSocket(bindTo, _memoryManager);
            _socketB = new SimpleWindowsHotSocket(bindTo, _memoryManager);

            _socketA.OnError += OnError;
            _socketB.OnError += OnError;

            _addressA = _socketA.GetLocalAddress(_memoryManager);
            _addressB = _socketB.GetLocalAddress(_memoryManager);
        }

        public void Dispose()
        {
            _socketB.Dispose();
            _socketA.Dispose();

            _addressB.Dispose();
            _addressA.Dispose();
        }

        private IHotSocket _socketA;
        private IHotSocket _socketB;

        private SocketAddress _addressA;
        private SocketAddress _addressB;

        private static readonly INativeMemoryManager _memoryManager = new SimpleMemoryManager();

        private long _errors;
        private long _pingsSent;
        private long _pingsReceived;

        private void OnError(object? sender, ErrorEventArgs e)
        {
            Interlocked.Increment(ref _errors);
            Task.Run(() => Console.WriteLine(e.GetException().Message));
        }

        [Benchmark]
        public void Ping()
        {
            void OnPacketReceived(IHotBuffer packet, SocketAddress from)
            {
                Interlocked.Increment(ref _pingsReceived);
            }

            // We do not start reading on A because we don't expect to receive anything on it.
            _socketB.StartReadingPackets(new DelegatingHotPacketProcessor(OnPacketReceived));

            var duration = Stopwatch.StartNew();

            var sendThreads = StartSendThreads();
            sendThreads.ForEach(t => t.Join());

            WaitForReceivesToStopAndSummarize(duration);
        }

        private long _pongsSent;
        private long _pongsReceived;

        [Benchmark]
        public void PingPong()
        {
            void OnPacketReceivedA(IHotBuffer packet, SocketAddress from)
            {
                Interlocked.Increment(ref _pongsReceived);
            }

            void OnPacketReceivedB(IHotBuffer packet, SocketAddress from)
            {
                Interlocked.Increment(ref _pingsReceived);

                // Send pong.
                /*var buffer = _socketB.AcquireWriteBuffer();
                buffer.SetLengthAndGetWritableSpan(PacketSize);
                _socketB.SubmitWriteBuffer(buffer, _addressA);*/

                _socketB.ForwardPacketTo(packet, _addressA);
                Interlocked.Increment(ref _pongsSent);
            }

            _socketA.StartReadingPackets(new DelegatingHotPacketProcessor(OnPacketReceivedA));
            _socketB.StartReadingPackets(new DelegatingHotPacketProcessor(OnPacketReceivedB));

            var duration = Stopwatch.StartNew();

            var sendThreads = StartSendThreads();
            sendThreads.ForEach(t => t.Join());

            WaitForReceivesToStopAndSummarize(duration);
        }

        private List<Thread> StartSendThreads()
        {
            // If it doesn't evenly divide... that's your problem. Don't use badly dividing numbers.
            var packetsPerThread = PacketCount / SendThreadCount;

            var sendThreads = new List<Thread>();

            void DoSendThread(object? boxedIndex)
            {
                var index = (int)boxedIndex!;

                for (var i = 0; i < packetsPerThread; i++)
                {
                    var buffer = _socketA.AcquireWriteBuffer();
                    buffer.SetLengthAndGetWritableSpan(PacketSize);
                    _socketA.SubmitWriteBuffer(buffer, _addressB);
                    Interlocked.Increment(ref _pingsSent);
                }
            }

            for (var i = 0; i < SendThreadCount; i++)
            {
                var thread = new Thread(DoSendThread)
                {
                    IsBackground = true
                };
                thread.Start(i);

                sendThreads.Add(thread);
            }

            return sendThreads;
        }

        private void WaitForReceivesToStopAndSummarize(Stopwatch duration)
        {
            var timeout = Stopwatch.StartNew();
            var lastKnownReceived = _pingsReceived;

            while (true)
            {
                Thread.Sleep(CompletionEvaluationInterval);

                var newReceived = _pingsReceived;
                if (newReceived == lastKnownReceived)
                    break; // We consider all receives completed.

                lastKnownReceived = newReceived;

                if (timeout.Elapsed >= Timeout)
                {
                    Console.WriteLine($"Receives did not stop within permitted timeout of {Timeout.TotalSeconds:F1} seconds. Timeout occurred!");
                    break;
                }
            }

            duration.Stop();

            var totalSent = _pingsSent + _pongsSent;
            var totalReceived = _pingsReceived + _pongsReceived;
            var lostPackets = totalSent - totalReceived;

            var lostRatio = 1.0 * lostPackets / totalSent;
            Console.WriteLine($"Lost {lostPackets} packets out of {totalSent} ({lostRatio:P3})");

            // We calculate PPS based on received values (so as not to give bonus for sent but lost packets).
            var pps = totalReceived / duration.Elapsed.TotalSeconds;
            var kpps = pps / 1000;
            Console.WriteLine($"{kpps:F1} KPPS");
        }
    }
}
