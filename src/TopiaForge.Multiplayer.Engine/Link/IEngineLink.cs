using System;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>Logical channels a link carries. The engine uses only the reliable-ordered session channel today.</summary>
    internal enum EngineChannel : byte
    {
        /// <summary>Reliable and ordered: every frame arrives once, in send order, or the link closes.</summary>
        Session = 0,

        /// <summary>Unreliable and sequenced: stale frames are dropped. Reserved for high-rate presence.</summary>
        Realtime = 1,

        /// <summary>Unreliable and sequenced voice frames.</summary>
        Voice = 2
    }

    internal enum EngineLinkState
    {
        /// <summary>Frames flow in both directions.</summary>
        Open = 0,

        /// <summary>The path stopped delivering but the connection has not timed out; reliable frames are held.</summary>
        Stalled = 1,

        /// <summary>The connection is gone. Frames already received can still be read.</summary>
        Closed = 2
    }

    /// <summary>
    /// One authenticated connection to a peer: the seam between the engine and a transport. The real secure transport
    /// and the rig's virtual transport both implement it. A link never exposes a network address.
    /// </summary>
    internal interface IEngineLink
    {
        /// <summary>Gets the transport-authenticated identity of the remote peer. It is opaque and stable per peer.</summary>
        string PeerIdentity { get; }

        /// <summary>Gets the per-host player key the transport derived for the remote peer, or the default value.</summary>
        MultiplayerPlayerKey PlayerKey { get; }

        /// <summary>Gets the current path state.</summary>
        EngineLinkState State { get; }

        /// <summary>Queues one frame. The link copies the bytes before returning.</summary>
        void Send(EngineChannel channel, ArraySegment<byte> frame);

        /// <summary>Dequeues the next received frame, if any.</summary>
        bool TryReceive(out EngineChannel channel, out byte[] frame);

        /// <summary>Closes the link after frames already sent are delivered.</summary>
        void Close();
    }
}
