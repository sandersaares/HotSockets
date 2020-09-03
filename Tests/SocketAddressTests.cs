using HotSockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net.Sockets;

namespace Tests
{
    [TestClass]
    public sealed class SocketAddressTests
    {
        private static INativeMemoryManager _memoryManager = new SimpleMemoryManager();

        [TestMethod]
        public void ReadWhatYouWrite()
        {
            // 0102:0304:...
            var testAddress6 = Enumerable.Range(1, 16).Select(x => (byte)x).ToArray();
            // 1.2.3.4
            var testAddress4 = Enumerable.Range(1, 4).Select(x => (byte)x).ToArray();

            using var addr = UnsafeSocketAddress.Empty(_memoryManager);

            addr.AddressFamily = AddressFamily.InterNetworkV6;
            addr.Port = 12345;
            addr.AddressV6 = testAddress6.AsSpan();

            Assert.AreEqual(AddressFamily.InterNetworkV6, addr.AddressFamily);
            Assert.AreEqual(12345, addr.Port);
            CollectionAssert.AreEqual(testAddress6, addr.AddressV6.ToArray());
            Assert.AreEqual(16, addr.Address.Length);

            addr.Clear();

            addr.AddressFamily = AddressFamily.InterNetwork;
            addr.Port = 45678;
            addr.AddressV4 = testAddress4;

            Assert.AreEqual(AddressFamily.InterNetwork, addr.AddressFamily);
            Assert.AreEqual(45678, addr.Port);
            CollectionAssert.AreEqual(testAddress4, addr.AddressV4.ToArray());
            Assert.AreEqual(4, addr.Address.Length);
        }
    }
}
