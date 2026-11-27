using System;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Testing
{
    /// <summary>The bytes and frames one node has sent and received over the rig's virtual transport.</summary>
    public sealed class MultiplayerTestTraffic
    {
        internal MultiplayerTestTraffic(long bytesSent, long framesSent, long bytesReceived, long framesReceived)
        {
            BytesSent = bytesSent;
            FramesSent = framesSent;
            BytesReceived = bytesReceived;
            FramesReceived = framesReceived;
        }

        /// <summary>Gets the session-protocol bytes the node sent.</summary>
        public long BytesSent { get; }

        /// <summary>Gets the frames the node sent.</summary>
        public long FramesSent { get; }

        /// <summary>Gets the bytes delivered to the node, not counting discarded duplicates.</summary>
        public long BytesReceived { get; }

        /// <summary>Gets the frames delivered to the node, not counting discarded duplicates.</summary>
        public long FramesReceived { get; }
    }

    /// <summary>
    /// Scripted faults on the rig's virtual links, beyond the fixed cadences of
    /// <see cref="MultiplayerNetworkConditions"/>. Every fault keeps the session channel's contract: frames on one link
    /// stay in order, and a frame lost for good breaks the connection instead of being skipped.
    /// </summary>
    public sealed class MultiplayerTestNetwork
    {
        private readonly MultiplayerTestRig rig;
        private readonly VirtualEngineTransport transport;

        internal MultiplayerTestNetwork(MultiplayerTestRig rig, VirtualEngineTransport transport)
        {
            this.rig = rig;
            this.transport = transport;
        }

        /// <summary>
        /// Holds the next <paramref name="frames"/> frames from one node to another, and every later frame on that
        /// link, until <see cref="Release"/> is called. <see cref="MultiplayerTestRig.Flush"/> does not wait for them.
        /// </summary>
        public void HoldNext(MultiplayerTestNode from, MultiplayerTestNode to, int frames = 1)
        {
            if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames));
            transport.HoldNext(Direction(from, to), frames);
        }

        /// <summary>Releases the frames held from one node to another; they arrive in order at the next tick.</summary>
        public void Release(MultiplayerTestNode from, MultiplayerTestNode to) => transport.Release(Direction(from, to));

        /// <summary>
        /// Cuts a client's link in both directions for <paramref name="ticks"/> ticks without either side noticing.
        /// Frames sent meanwhile arrive in order once the partition heals.
        /// </summary>
        public void Partition(MultiplayerTestNode client, int ticks)
        {
            if (ticks < 1) throw new ArgumentOutOfRangeException(nameof(ticks));
            var until = rig.Tick.Value + (ulong)ticks;
            Direction(client, rig.Server).HeldUntilTick = until;
            Direction(rig.Server, client).HeldUntilTick = until;
        }

        /// <summary>
        /// Freezes a node's process for <paramref name="ticks"/> ticks, as a long scene load does. Frames queue for it
        /// meanwhile, and it processes them in order when it resumes.
        /// </summary>
        public void Stall(MultiplayerTestNode node, int ticks)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (ticks < 1) throw new ArgumentOutOfRangeException(nameof(ticks));
            rig.RequireNode(node);
            node.StalledUntilTick = rig.Tick.Value + (ulong)ticks;
        }

        /// <summary>
        /// Loses the next frame from one node to another for good. The session channel cannot skip a frame, so the
        /// connection breaks when it would have arrived: the client returns to Connecting and the host releases the
        /// participant's objects.
        /// </summary>
        public void DropPermanently(MultiplayerTestNode from, MultiplayerTestNode to) =>
            transport.DropNextPermanently(Direction(from, to));

        /// <summary>Sends a byte-identical copy of the last frame from one node to another again; the channel discards it.</summary>
        public void ReplayLast(MultiplayerTestNode from, MultiplayerTestNode to)
        {
            if (!transport.ReplayLast(Direction(from, to)))
            {
                throw new InvalidOperationException("No frame has been sent on that link yet.");
            }
        }

        private VirtualDirection Direction(MultiplayerTestNode from, MultiplayerTestNode to)
        {
            if (from == null) throw new ArgumentNullException(nameof(from));
            if (to == null) throw new ArgumentNullException(nameof(to));
            rig.RequireNode(from);
            rig.RequireNode(to);
            if (ReferenceEquals(from, rig.Server) && to.Role == MultiplayerTestRole.RemoteClient)
            {
                return (to.Link ?? throw NotConnected(to)).Peer!.Outbound!;
            }

            if (ReferenceEquals(to, rig.Server) && from.Role == MultiplayerTestRole.RemoteClient)
            {
                return (from.Link ?? throw NotConnected(from)).Outbound!;
            }

            throw new ArgumentException("A link joins the rig's server and one remote client.", nameof(to));
        }

        private static InvalidOperationException NotConnected(MultiplayerTestNode client) =>
            new InvalidOperationException("Participant '" + client.ParticipantId + "' is not connected.");
    }
}
