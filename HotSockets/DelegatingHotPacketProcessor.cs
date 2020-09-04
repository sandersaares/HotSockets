using System;

namespace HotSockets
{
    public sealed class DelegatingHotPacketProcessor : IHotPacketProcessor
    {
        public DelegatingHotPacketProcessor(Action<IHotBuffer, SocketAddress> processPacket)
        {
            _processPacket = processPacket;
        }

        private readonly Action<IHotBuffer, SocketAddress> _processPacket;
        public void ProcessPacket(IHotBuffer buffer, SocketAddress from) => _processPacket(buffer, from);
    }
}
