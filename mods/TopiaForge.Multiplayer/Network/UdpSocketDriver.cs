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

        /// <summary>
        /// Reads at most one waiting datagram; false when none is waiting. A datagram the socket had to drop, oversized
        /// or failed, is consumed with a null <paramref name="path"/>, so every call does bounded work and the caller
        /// counts it against its turn.
        /// </summary>
        bool TryReceive(byte[] buffer, out int count, out TransportPath? path);

        /// <summary>Sends a datagram on <paramref name="path"/>; a path this socket cannot reach is dropped.</summary>
        void Send(TransportPath path, byte[] buffer, int offset, int count);
    }

    /// <summary>
    /// The game's UDP socket, bound to every local address. Where the operating system has IPv6, it is one dual-mode
    /// socket that carries the measured workarounds for the game's Mono (<c>MultiplayerMonoSpike.md</c>, section 1): IPv4
    /// destinations are mapped to IPv6 before sending, because Mono fails to send to a plain IPv4 endpoint from a
    /// dual-mode socket; and Windows' connection-reset reports are turned off, so an ICMP port-unreachable cannot break the
    /// next receive. Where it has none, as on a Linux kernel with IPv6 turned off, the game under Proton included, it is
    /// one IPv4 socket with the same settings, bound to the IPv4 form of the bind address. That socket hears and reaches
    /// IPv4 alone: a path to an IPv6 address is dropped, and an IPv6-only bind address is refused rather than replaced.
    /// Either way the stack sees an IPv4 peer in the form a dual-mode socket reports, IPv4 mapped into IPv6, so paths,
    /// sources, LAN entries and the NAT layer compare as they always have. Errors carry only their socket error code.
    /// </summary>
    /// <remarks>
    /// The one signal for the IPv4 socket is the operating system's own answer to the call the dual-mode socket needs:
    /// creating an IPv6 socket fails with <see cref="SocketError.AddressFamilyNotSupported"/>. Windows, Linux and Wine
    /// answer that call alike, whichever runtime asks. <see cref="Socket.OSSupportsIPv6"/> is not consulted, because it
    /// can be wrong under the game's Mono: Mono derives it from the IPv6 interface index each network adapter reports,
    /// not from a socket, and under Wine the adapters are Wine's own. Upstream Wine reports no such index, so the property
    /// would read false even where the kernel has IPv6, and move every player there off the dual-mode socket. The
    /// descriptor endpoint and its client open their TCP sockets by the same rule, through <see cref="IPSockets"/>.
    /// </remarks>
    internal sealed class UdpSocketDriver : IDatagramSocket
    {
        /// <summary>The Windows ioctl that stops ICMP port-unreachable surfacing as a reset on the next receive.</summary>
        private const int SioUdpConnectionReset = -1744830452;
        private const int ReceiveBufferBytes = 1024 * 1024;

        private readonly Socket socket;
        private readonly IPAddress any;
        private readonly byte[] scratch = new byte[65536];
        private EndPoint remote;
        private bool disposed;

        /// <summary>Binds to <paramref name="port"/>, or to a free port when it is zero.</summary>
        /// <param name="bindAddress">
        /// The address to bind, every local address by default. Tests bind a loopback address: binding every address makes
        /// Windows ask the player to allow the program through its firewall.
        /// </param>
        /// <param name="withoutIPv6">
        /// Opens the IPv4 socket a system without IPv6 gets, on any system, so tests run their checks over both sockets.
        /// </param>
        internal UdpSocketDriver(int port, IPAddress? bindAddress = null, bool withoutIPv6 = false)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            socket = IPSockets.Open(SocketType.Dgram, ProtocolType.Udp, withoutIPv6);
            IPv4Only = socket.AddressFamily == AddressFamily.InterNetwork;
            any = IPv4Only ? IPAddress.Any : IPAddress.IPv6Any;
            remote = new IPEndPoint(any, 0);
            try
            {
                socket.Blocking = false;
                socket.ReceiveBufferSize = ReceiveBufferBytes;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    socket.IOControl(SioUdpConnectionReset, new byte[] { 0, 0, 0, 0 }, null);
                }

                // A host's well-known port must not be shared with another process on Windows, where a second bind
                // could otherwise steal its datagrams.
                if (port != 0 && Environment.OSVersion.Platform == PlatformID.Win32NT) socket.ExclusiveAddressUse = true;
                socket.Bind(IPSockets.LocalEndPoint(socket, bindAddress, port));
                LocalPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public int LocalPort { get; }

        /// <summary>Gets whether the socket is IPv4 only, as on a system without IPv6: it hears and reaches IPv4 peers alone.</summary>
        internal bool IPv4Only { get; }

        /// <summary>
        /// Gets how many datagrams were dropped for being larger than a transport datagram, for failing to send, or for
        /// going where the socket cannot reach.
        /// </summary>
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
            try
            {
                // Every datagram lands in scratch space first. Windows reports the whole queue as available, and Linux
                // truncates silently, so only a full read can tell an oversized datagram from a legal one.
                remote = new IPEndPoint(any, 0);
                var received = socket.ReceiveFrom(scratch, 0, scratch.Length, SocketFlags.None, ref remote);
                if (received > buffer.Length)
                {
                    Dropped++;
                    return true;
                }

                Buffer.BlockCopy(scratch, 0, buffer, 0, received);
                count = received;

                // An IPv4 socket reports a plain IPv4 source and a dual-mode one its mapped form; a path keys both by the
                // mapped form, so one peer has one path whichever socket heard it.
                path = UdpPaths.For((IPEndPoint)remote);
                return true;
            }
            catch (SocketException exception) when (IsTransient(exception.SocketErrorCode))
            {
                if (exception.SocketErrorCode == SocketError.WouldBlock) return false;
                Dropped++;
                return true;
            }
        }

        public void Send(TransportPath path, byte[] buffer, int offset, int count)
        {
            if (disposed || !TryGetDestination(path, out var destination))
            {
                Dropped++;
                return;
            }

            try
            {
                socket.SendTo(buffer, offset, count, SocketFlags.None, destination);
            }
            catch (SocketException exception) when (IsTransient(exception.SocketErrorCode))
            {
                // A full send buffer or an unreachable host loses this datagram, as any network may; recovery resends.
                Dropped++;
            }
        }

        /// <summary>
        /// Gets where a path sends. The dual-mode socket takes the path's endpoint in the stack's form, IPv4 mapped into
        /// IPv6. The IPv4 socket takes the plain IPv4 endpoint read straight from the path, so a send there costs no more,
        /// and has none for a path to an IPv6 address, which it cannot reach.
        /// </summary>
        private bool TryGetDestination(TransportPath path, out IPEndPoint destination) =>
            IPv4Only ? UdpPaths.TryGetIPv4EndPoint(path, out destination) : UdpPaths.TryGetEndPoint(path, out destination);

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

    /// <summary>
    /// Opens the network layer's IP sockets by the rule <see cref="UdpSocketDriver"/> records, the descriptor endpoint's
    /// TCP sockets as well as the game's UDP one, and moves endpoints between the stack's form and the socket's. The stack
    /// names an IPv4 peer by its IPv4-mapped IPv6 form: a dual-mode socket takes that form as it is, and an IPv4 socket
    /// takes the plain IPv4 address it maps.
    /// </summary>
    internal static class IPSockets
    {
        /// <summary>
        /// Opens one dual-mode IPv6 socket where the operating system has IPv6, and one IPv4 socket where creating the IPv6
        /// socket fails with <see cref="SocketError.AddressFamilyNotSupported"/>.
        /// </summary>
        /// <param name="withoutIPv6">Takes the IPv4 branch as a system without IPv6 does, on any system; tests use it.</param>
        internal static Socket Open(SocketType type, ProtocolType protocol, bool withoutIPv6 = false) =>
            Open(type, protocol, withoutIPv6 ? SocketError.AddressFamilyNotSupported : SocketError.Success);

        /// <summary>
        /// Opens a socket by the same rule, with the IPv6 socket refused with <paramref name="refusal"/> on any system unless
        /// it is <see cref="SocketError.Success"/>; tests use it. Only the refusal a system without IPv6 gives opens the
        /// IPv4 socket. Any other reaches the caller as it is, so a passing failure never moves a player who has IPv6 off
        /// the dual-mode socket.
        /// </summary>
        internal static Socket Open(SocketType type, ProtocolType protocol, SocketError refusal)
        {
            Socket socket;
            try
            {
                // A system without IPv6 refuses the IPv6 socket with AddressFamilyNotSupported, and the seam refuses it the
                // same way, so tests take the very branch such a system takes.
                if (refusal != SocketError.Success) throw new SocketException((int)refusal);
                socket = new Socket(AddressFamily.InterNetworkV6, type, protocol);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressFamilyNotSupported)
            {
                return new Socket(AddressFamily.InterNetwork, type, protocol);
            }

            try
            {
                socket.DualMode = true;
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Gets where <paramref name="socket"/> binds <paramref name="address"/>, every local address when it is null. A
        /// dual-mode socket binds an IPv4 address through its mapped form. An IPv4 socket binds an address's IPv4 form, and
        /// the IPv6 any-address as the IPv4 one; an IPv6-only address, such as the IPv6 loopback, has no IPv4 form, so it
        /// is refused with <see cref="SocketError.AddressFamilyNotSupported"/> rather than replaced by another.
        /// </summary>
        internal static IPEndPoint LocalEndPoint(Socket socket, IPAddress? address, int port)
        {
            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                address ??= IPAddress.IPv6Any;
                return new IPEndPoint(address.AddressFamily == AddressFamily.InterNetwork ? address.MapToIPv6() : address, port);
            }

            if (address == null || address.Equals(IPAddress.IPv6Any)) return new IPEndPoint(IPAddress.Any, port);
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily != AddressFamily.InterNetwork) throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            return new IPEndPoint(address, port);
        }

        /// <summary>
        /// Puts <paramref name="destination"/> in the form <paramref name="socket"/> sends to: a dual-mode socket takes an
        /// IPv4 address in its mapped form, because Mono fails to send to a plain IPv4 endpoint from one, and an IPv4 socket
        /// takes a mapped address in its plain form. False when the socket cannot reach the destination at all, as an IPv4
        /// socket cannot reach an IPv6 address.
        /// </summary>
        internal static bool TryReach(Socket socket, IPEndPoint destination, out IPEndPoint reachable)
        {
            var address = destination.Address;
            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                reachable = address.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(address.MapToIPv6(), destination.Port) : destination;
                return true;
            }

            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                reachable = null!;
                return false;
            }

            reachable = ReferenceEquals(address, destination.Address) ? destination : new IPEndPoint(address, destination.Port);
            return true;
        }
    }
}
