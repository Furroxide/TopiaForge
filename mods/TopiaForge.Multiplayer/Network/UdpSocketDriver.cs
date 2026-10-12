using System;
using System.Net;
using System.Net.Sockets;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>A datagram socket as the network thread uses it: paths in, paths out, nothing about addresses above.</summary>
    internal interface IDatagramSocket : IDisposable
    {
        /// <summary>Gets the local port the socket is bound to.</summary>
        int LocalPort { get; }

        /// <summary>Blocks until a datagram is waiting or <paramref name="timeoutMicros"/> passes.</summary>
        bool WaitReadable(long timeoutMicros);

        /// <summary>Reads one waiting datagram, if any. Oversized datagrams are dropped.</summary>
        bool TryReceive(byte[] buffer, out int count, out TransportPath? path);

        /// <summary>Sends a datagram on <paramref name="path"/>; a path this socket cannot reach is dropped.</summary>
        void Send(TransportPath path, byte[] buffer, int offset, int count);
    }

    /// <summary>
    /// One dual-mode UDP socket, bound to every local address. It carries the measured workarounds for the game's Mono
    /// (<c>MultiplayerMonoSpike.md</c>, section 1): IPv4 destinations are mapped to IPv6 before sending, because Mono
    /// fails to send to a plain IPv4 endpoint from a dual-mode socket; and Windows' connection-reset reports are turned
    /// off, so an ICMP port-unreachable cannot break the next receive. Errors carry only their socket error code.
    /// </summary>
    internal sealed class UdpSocketDriver : IDatagramSocket
    {
        /// <summary>The Windows ioctl that stops ICMP port-unreachable surfacing as a reset on the next receive.</summary>
        private const int SioUdpConnectionReset = -1744830452;
        private const int ReceiveBufferBytes = 1024 * 1024;

        private readonly Socket socket;
        private readonly byte[] scratch = new byte[65536];
        private EndPoint remote = new IPEndPoint(IPAddress.IPv6Any, 0);
        private bool disposed;

        /// <summary>Binds to <paramref name="port"/>, or to a free port when it is zero.</summary>
        internal UdpSocketDriver(int port)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.DualMode = true;
                socket.Blocking = false;
                socket.ReceiveBufferSize = ReceiveBufferBytes;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    socket.IOControl(SioUdpConnectionReset, new byte[] { 0, 0, 0, 0 }, null);
                }

                // A host's well-known port must not be shared with another process on Windows, where a second bind
                // could otherwise steal its datagrams.
                if (port != 0 && Environment.OSVersion.Platform == PlatformID.Win32NT) socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                LocalPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public int LocalPort { get; }

        /// <summary>Gets how many datagrams were dropped for being larger than a transport datagram or failing to send.</summary>
        internal long Dropped { get; private set; }

        public bool WaitReadable(long timeoutMicros)
        {
            if (disposed) return false;
            var wait = (int)Math.Max(0, Math.Min(timeoutMicros, int.MaxValue));
            try
            {
                return socket.Poll(wait, SelectMode.SelectRead);
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public bool TryReceive(byte[] buffer, out int count, out TransportPath? path)
        {
            count = 0;
            path = null;
            if (disposed || buffer == null) return false;
            while (true)
            {
                try
                {
                    // Every datagram lands in scratch space first. Windows reports the whole queue as available, and
                    // Linux truncates silently, so only a full read can tell an oversized datagram from a legal one.
                    remote = new IPEndPoint(IPAddress.IPv6Any, 0);
                    var received = socket.ReceiveFrom(scratch, 0, scratch.Length, SocketFlags.None, ref remote);
                    if (received > buffer.Length)
                    {
                        Dropped++;
                        continue;
                    }

                    Buffer.BlockCopy(scratch, 0, buffer, 0, received);
                    count = received;
                    path = UdpPaths.For((IPEndPoint)remote);
                    return true;
                }
                catch (SocketException exception) when (IsTransient(exception.SocketErrorCode))
                {
                    if (exception.SocketErrorCode == SocketError.WouldBlock) return false;
                    Dropped++;
                }
            }
        }

        public void Send(TransportPath path, byte[] buffer, int offset, int count)
        {
            if (disposed || !UdpPaths.TryGetEndPoint(path, out var endpoint))
            {
                Dropped++;
                return;
            }

            try
            {
                socket.SendTo(buffer, offset, count, SocketFlags.None, endpoint);
            }
            catch (SocketException exception) when (IsTransient(exception.SocketErrorCode))
            {
                // A full send buffer or an unreachable host loses this datagram, as any network may; recovery resends.
                Dropped++;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            socket.Dispose();
        }

        private static bool IsTransient(SocketError error) =>
            error == SocketError.WouldBlock || error == SocketError.ConnectionReset || error == SocketError.MessageSize ||
            error == SocketError.NoBufferSpaceAvailable || error == SocketError.HostUnreachable ||
            error == SocketError.NetworkUnreachable || error == SocketError.AddressNotAvailable || error == SocketError.Interrupted;
    }
}
