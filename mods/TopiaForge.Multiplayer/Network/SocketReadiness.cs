using System.Net.Sockets;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// Asks a non-blocking socket, without waiting, whether a read, an accept or a send would get anywhere, so that a loop
    /// on the network thread that asks before it acts costs nothing on a turn with nothing to do. Reading an empty socket
    /// throws <see cref="SocketError.WouldBlock"/>, and the runtime builds the source's address before it finds nothing
    /// there; the network thread turns at least every 2 ms, so a loop that read first paid both about 500 times a second.
    /// </summary>
    /// <remarks>
    /// The question is <see cref="Socket.Poll"/> with no wait, which the game's Mono and .NET both answer from the
    /// operating system's own readiness, the same the network thread sleeps on. It is not <see cref="Socket.Available"/>:
    /// an empty datagram leaves nothing available, so a loop that asked that would never read past one, and anyone on the
    /// network could deafen it with a single datagram. Readiness also covers what the next call reports as a result rather
    /// than as data: a connection to accept, or a peer that closed or reset its connection. A socket that cannot answer
    /// counts as ready, so the call itself reports what went wrong, as it did before anything asked.
    /// </remarks>
    internal static class SocketReadiness
    {
        /// <summary>Gets whether a read or an accept on <paramref name="socket"/> would find something now.</summary>
        internal static bool CanRead(Socket socket) => Ready(socket, SelectMode.SelectRead);

        /// <summary>Gets whether a send on <paramref name="socket"/> would hand the operating system some bytes now.</summary>
        internal static bool CanWrite(Socket socket) => Ready(socket, SelectMode.SelectWrite);

        private static bool Ready(Socket socket, SelectMode mode)
        {
            try
            {
                return socket.Poll(0, mode);
            }
            catch (SocketException)
            {
                return true;
            }
        }
    }
}
