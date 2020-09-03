using System;

namespace HotSockets
{
    public sealed class DelegatingHotPacketProcessor : IHotPacketProcessor
    {
        public DelegatingHotPacketProcessor(Action<IHotBuffer, UnsafeSocketAddress> processPacket)
        {
            _processPacket = processPacket;
        }

        private readonly Action<IHotBuffer, UnsafeSocketAddress> _processPacket;
        public void ProcessPacket(IHotBuffer buffer, UnsafeSocketAddress from) => _processPacket(buffer, from);
    }
}
