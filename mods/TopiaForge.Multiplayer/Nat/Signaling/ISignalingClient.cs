using System;

namespace TopiaForge.Multiplayer.NatTraversal.Signaling
{
    internal enum SignalingState
    {
        Connecting = 0,
        Open = 1,
        Closed = 2
    }

    /// <summary>Why a signaling socket closed, from the service's close code or the client's own failure.</summary>
    internal enum SignalingCloseReason
    {
        None = 0,

        /// <summary>A normal close after a goodbye (1000), or this side closed it.</summary>
        Normal = 1,

        /// <summary>A newer socket for the same host or ticket replaced this one (4409).</summary>
        Replaced = 2,

        /// <summary>The host's session ended (4410).</summary>
        SessionEnded = 3,

        /// <summary>A peer socket reached its lifetime (4411); the peer opens a new one to keep signaling.</summary>
        Expired = 4,

        /// <summary>The socket sent frames too fast (4408).</summary>
        RateLimited = 5,

        /// <summary>The service closed for invalid frames, a binary frame or an oversized one (4400, 1003, 1009).</summary>
        Invalid = 6,

        /// <summary>The upgrade was refused: an unknown server, a bad credential, a full room, a limit or a disabled feature.</summary>
        Refused = 7,

        /// <summary>The service could not be reached, or the connection broke.</summary>
        Unreachable = 8,

        /// <summary>The peer's ticket stopped holding (4403): its invite was withdrawn or expired, or the server stopped being public.</summary>
        Revoked = 9
    }

    /// <summary>
    /// One signaling socket to the control plane (ControlPlaneApi.md, section 8). Every member is non-blocking and safe
    /// from any thread: the NAT agent polls it on the network thread while the socket runs its own I/O. Frames queue in
    /// both directions with fixed bounds.
    /// </summary>
    internal interface ISignalingClient : IDisposable
    {
        SignalingState State { get; }

        SignalingCloseReason CloseReason { get; }

        /// <summary>Queues a frame to send; false when the socket is closed or its queue is full.</summary>
        bool TrySend(SignalMessage message);

        /// <summary>Takes the next frame the service sent, if any.</summary>
        bool TryReceive(out SignalMessage message);

        /// <summary>
        /// Closes the socket once the frames already queued have gone, then releases it; nothing more need be called.
        /// <see cref="IDisposable.Dispose"/> closes at once instead, dropping what is queued.
        /// </summary>
        void Close();
    }
}
