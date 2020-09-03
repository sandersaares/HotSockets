namespace HotSockets
{
    /// <summary>
    /// Processes packets as they are received.
    /// </summary>
    /// <remarks>
    /// Methods are called by IHotSocket implementations and may be called on any thread and many times in parallel (depending on IHotSocket implementation).
    /// </remarks>
    public interface IHotPacketProcessor
    {
        // TODO: We might benefit from a mechanism that allows packet forwarding without buffer copying (and without using a separate write buffer).

        /// <remarks>
        /// The socket retains ownership of all passed arguments. Copy them if you need to preserve any of the values.
        /// </remarks>
        void ProcessPacket(IHotBuffer buffer, UnsafeSocketAddress from);
    }
}
