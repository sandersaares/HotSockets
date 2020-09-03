using System;

namespace HotSockets
{
    /// <summary>
    /// A buffer managed by an IHotSocket implementation.
    /// </summary>
    /// <remarks>
    /// All IHotSocket reads and writes are performed via IHotBuffer implementations.
    /// </remarks>
    public interface IHotBuffer : IDisposable
    {
        /// <summary>
        /// The most recently defined length of the buffer.
        /// 
        /// Value is set either by IHotSocket implementation (for read buffers) or by SetLengthAndGetSpan() (for write buffers).
        /// </summary>
        int Length { get; }

        /// <summary>
        /// Sets the length of the buffer and returns the span for the acquired range.
        /// 
        /// Call this before writing data.
        /// </summary>
        Span<byte> SetLengthAndGetWritableSpan(int length);

        /// <summary>
        /// Gets the span using the most recently defined length.
        /// </summary>
        ReadOnlySpan<byte> GetReadableSpan();
    }
}
