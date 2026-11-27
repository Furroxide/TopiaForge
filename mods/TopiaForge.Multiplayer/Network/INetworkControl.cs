using System;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// What the host and join flows ask of a network stack's owner, from the main thread. A <see cref="NetworkThread"/>
    /// implements it in the game; tests drive the same flows over the deterministic virtual network.
    /// </summary>
    internal interface INetworkControl : IDisposable
    {
        /// <summary>Gets the local port of the game socket.</summary>
        int LocalPort { get; }

        /// <summary>Starts a connection pinned to <paramref name="expectedKey"/> when one is known.</summary>
        ConnectAttempt Connect(TransportPath path, byte[]? expectedKey);

        /// <summary>Runs <paramref name="command"/> on the stack's own thread, with the time it runs at.</summary>
        void Post(Action<NetworkStack, long> command);

        /// <summary>Takes the next accepted link, unclaimed info reply or fault.</summary>
        bool TryTakeEvent(out NetworkEvent networkEvent);

        void Attach(INetworkService service);

        void Detach(INetworkService service);

        /// <summary>Closes every link within <paramref name="gracefulMicros"/>, then releases the socket and the keys.</summary>
        void Stop(long gracefulMicros);
    }
}
