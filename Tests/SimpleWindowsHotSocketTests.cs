using HotSockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests
{
    [TestClass]
    public sealed class SimpleWindowsHotSocketTests : HotSocketTestsBase
    {
        protected override IHotSocket CreateSocket(SocketAddress bindTo)
        {
            return new SimpleWindowsHotSocket(bindTo, _memoryManager);
        }
    }
}
