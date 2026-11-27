using System;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// Work the network thread runs beside its stack, on the same thread: the descriptor endpoint, LAN announcements and
    /// LAN browsing. A service is attached and detached through the thread, and disposed on it.
    /// </summary>
    internal interface INetworkService : IDisposable
    {
        /// <summary>Runs each turn, after waiting datagrams are read and before the stack is polled.</summary>
        void Turn(NetworkStack stack, long now);

        /// <summary>Offers an info reply the stack received; returns true when the service consumed it.</summary>
        bool TryClaim(TransportInfoReply reply, long now);

        /// <summary>Gets when the service next needs a turn, or <see cref="long.MaxValue"/>.</summary>
        long NextTimeoutMicros();
    }
}
