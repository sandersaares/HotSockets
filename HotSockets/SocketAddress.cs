using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace HotSockets
{
    /// <summary>
    /// An IP address and port combination.
    /// </summary>
    /// <remarks>
    /// Instances are designed to be reused for allocation-free packet processing.
    /// </remarks>
    public sealed class SocketAddress : IDisposable
    {
        public const int Size = 28; /* Eyeballed from https://docs.microsoft.com/en-us/windows/win32/winsock/sockaddr-2 */

        // Family-specific to make initialization obvious. You don't need to keep it IPv4 if you don't want to.
        public static SocketAddress IPv4(ReadOnlySpan<byte> address, ushort port, INativeMemoryManager memoryManager)
        {
            var instance = new SocketAddress(memoryManager)
            {
                AddressFamily = AddressFamily.InterNetwork,
                Port = port
            };
            address.CopyTo(instance.AddressV4);

            return instance;
        }

        // Family-specific to make initialization obvious. You don't need to keep it IPv6 if you don't want to.
        public static SocketAddress IPv6(ReadOnlySpan<byte> address, ushort port, INativeMemoryManager memoryManager)
        {
            var instance = new SocketAddress(memoryManager)
            {
                AddressFamily = AddressFamily.InterNetworkV6,
                Port = port
            };
            address.CopyTo(instance.AddressV6);

            return instance;
        }

        // Auto-detects address family.
        public static SocketAddress New(ReadOnlySpan<byte> address, ushort port, INativeMemoryManager memoryManager)
        {
            switch (address.Length)
            {
                case 4:
                    return IPv4(address, port, memoryManager);
                case 16:
                    return IPv6(address, port, memoryManager);
                default:
                    throw new NotSupportedException($"Could not detect address family based on IP address of length {address.Length}.");
            }
        }

        public static SocketAddress Empty(INativeMemoryManager memoryManager) => new SocketAddress(memoryManager);

        private SocketAddress(INativeMemoryManager memoryManager)
        {
            _memoryManager = memoryManager;

            Ptr = _memoryManager.Allocate(Size);
            Clear();
        }

        ~SocketAddress() => Dispose(false);
        public void Dispose() => Dispose(true);

        private void Dispose(bool disposing)
        {
            if (disposing)
                GC.SuppressFinalize(this);

            if (Ptr != IntPtr.Zero)
            {
                _memoryManager.Deallocate(Ptr);
                Ptr = IntPtr.Zero;
            }
        }

        private readonly INativeMemoryManager _memoryManager;

        public IntPtr Ptr { get; private set; }
        public unsafe Span<byte> Span => new Span<byte>(Ptr.ToPointer(), Size);

        public void Clear()
        {
            Windows.RtlZeroMemory(Ptr, new UIntPtr(Size));
        }

        public AddressFamily AddressFamily
        {
            get => (AddressFamily)BinaryPrimitives.ReadInt16LittleEndian(Span);
            set => BinaryPrimitives.WriteInt16LittleEndian(Span, (short)value);
        }

        public ushort Port
        {
            get => BinaryPrimitives.ReadUInt16BigEndian(Span.Slice(2));
            set => BinaryPrimitives.WriteUInt16BigEndian(Span.Slice(2), value);
        }

        /// <summary>
        /// Gets or sets the address depending on the currently set AddressFamily.
        /// 
        /// The setter copies data into the SocketAddress. To update in-place, modify the returned span.
        /// </summary>
        public Span<byte> Address
        {
            get
            {
                switch (AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        return AddressV4;
                    case AddressFamily.InterNetworkV6:
                        return AddressV6;
                    default:
                        throw new NotSupportedException($"Address family not supported: {AddressFamily}");
                }
            }
            set
            {
                var storage = Address;
                if (value.Length != storage.Length)
                    throw new ArgumentException($"Size of provided address ({value.Length}) is not the same as expected for the current address family ({storage.Length}).", nameof(value));

                value.CopyTo(storage);
            }
        }

        /// <summary>
        /// Gets or sets the IPv4 address. Valid only if AddressFamily indicates IPv4.
        /// 
        /// The setter copies data into the SocketAddress. To update in-place, modify the returned span.
        /// </summary>
        public Span<byte> AddressV4
        {
            get => Span.Slice(4, 4);
            set => value.CopyTo(Span.Slice(4, 4));
        }

        /// <summary>
        /// Gets or sets the IPv6 address. Valid only if AddressFamily indicates IPv6.
        /// 
        /// The setter copies data into the SocketAddress. To update in-place, modify the returned span.
        /// </summary>
        public Span<byte> AddressV6
        {
            get => Span.Slice(8, 16);
            set => value.CopyTo(Span.Slice(8, 16));
        }

        public override string ToString()
        {
            if (Ptr == IntPtr.Zero)
                return $"Disposed {nameof(SocketAddress)}";
            else if (AddressFamily == AddressFamily.InterNetwork || AddressFamily == AddressFamily.InterNetworkV6)
                return new IPEndPoint(new IPAddress(Address), Port).ToString();
            else if (AddressFamily == 0)
                return $"Uninitialized {nameof(SocketAddress)}";

            return $"Address from unsupported family {(int)AddressFamily}";
        }

        public unsafe void CopyTo(SocketAddress other)
        {
            if (this == other)
                return;

            Buffer.MemoryCopy(Ptr.ToPointer(), other.Ptr.ToPointer(), Size, Size);
        }
    }
}