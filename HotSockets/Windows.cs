using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace HotSockets
{
    static class Windows
    {
        private const string Ws2_32 = "ws2_32.dll";

        public static readonly IntPtr InvalidHandle = IntPtr.Subtract(IntPtr.Zero, 1);

        [DllImport("kernel32.dll")]
        public static extern void RtlZeroMemory(IntPtr dst, UIntPtr length);

        static Windows()
        {
            // Ensure that WSAStartup has been called once per process.
            // The System.Net.NameResolution contract is responsible for the initialization.
            Dns.GetHostName();
        }

        public static SocketError GetLastSocketError()
        {
            int win32Error = Marshal.GetLastWin32Error();
            Debug.Assert(win32Error != 0, "Expected non-0 error");
            return (SocketError)win32Error;
        }

        public static void MustSucceed(SocketError result)
        {
            if (result == SocketError.Success)
                return;

            throw new SocketException();
        }

        [Flags]
        public enum SocketConstructorFlags
        {
            WSA_FLAG_OVERLAPPED = 0x01,
            WSA_FLAG_MULTIPOINT_C_ROOT = 0x02,
            WSA_FLAG_MULTIPOINT_C_LEAF = 0x04,
            WSA_FLAG_MULTIPOINT_D_ROOT = 0x08,
            WSA_FLAG_MULTIPOINT_D_LEAF = 0x10,
            WSA_FLAG_NO_HANDLE_INHERIT = 0x80,
        }

        [DllImport(Ws2_32, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr WSASocketW(
                                        [In] AddressFamily addressFamily,
                                        [In] SocketType socketType,
                                        [In] ProtocolType protocolType,
                                        [In] IntPtr protocolInfo,
                                        [In] uint group,
                                        [In] SocketConstructorFlags flags);

        [DllImport(Ws2_32, ExactSpelling = true, SetLastError = true)]
        internal static extern SocketError ioctlsocket(
            [In] IntPtr socketHandle,
            [In] uint controlCode,
            [In, Out] ref int argp);

        [DllImport(Ws2_32, ExactSpelling = true, SetLastError = true)]
        internal static extern SocketError bind(
            [In] IntPtr socketHandle,
            [In] IntPtr socketAddress,
            [In] int socketAddressSize);

        [DllImport(Ws2_32, ExactSpelling = true, SetLastError = true)]
        internal static extern SocketError closesocket(
            [In] IntPtr socketHandle);

        [DllImport(Ws2_32, ExactSpelling = true, SetLastError = true)]
        internal static extern SocketError getsockname(
            [In] IntPtr socketHandle,
            [In] IntPtr socketAddress,
            [In, Out] ref int socketAddressSize);

        [DllImport(Ws2_32, SetLastError = true)]
        internal static extern unsafe int recvfrom(
            [In] IntPtr socketHandle,
            [In] IntPtr buffer,
            [In] int bufferLength,
            [In] SocketFlags socketFlags,
            [In] IntPtr socketAddress,
            [In] IntPtr socketAddressSize);

        [DllImport(Ws2_32, SetLastError = true)]
        internal static extern unsafe int sendto(
            [In] IntPtr socketHandle,
            [In] IntPtr buffer,
            [In] int bufferLen,
            [In] SocketFlags socketFlags,
            [In] IntPtr socketAddress,
            [In] int socketAddressSize);
    }
}
