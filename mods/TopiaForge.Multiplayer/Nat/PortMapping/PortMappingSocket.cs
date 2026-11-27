using System;
using System.Net;
using System.Net.Sockets;

namespace TopiaForge.Multiplayer.NatTraversal.PortMapping
{
    /// <summary>
    /// The short-lived IPv4 UDP socket the port-mapping clients use. Like the game socket, it turns off Windows'
    /// connection-reset reports, so a gateway answering with ICMP port unreachable cannot turn a quiet wait into a busy
    /// one. It binds the given address; tests pass loopback so no firewall prompt appears.
    /// </summary>
    internal static class PortMappingSocket
    {
        private const int SioUdpConnectionReset = -1744830452;

        internal static Socket? Open(IPAddress bindAddress)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT) socket.IOControl(SioUdpConnectionReset, new byte[] { 0, 0, 0, 0 }, null);
                socket.Bind(new IPEndPoint(bindAddress, 0));
                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();
                return null;
            }
        }
    }
}
