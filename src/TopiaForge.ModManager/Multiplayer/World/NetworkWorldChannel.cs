using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>Takes the world messages of the types it registered for. Main thread.</summary>
    internal interface IWorldMessageHandler
    {
        /// <summary>
        /// Handles one message of <paramref name="type"/> from <paramref name="sender"/>. The reader stands after the type;
        /// the handler returns false when the message is malformed, and must not keep the reader.
        /// </summary>
        bool Handle(byte type, ParticipantId sender, WorldWireReader reader);
    }

    /// <summary>
    /// The network world's end of the runtime's <c>WorldReplication</c> system channel: it takes every message, hands each
    /// to the handler registered for its type, and sends what world replication writes. A broadcast goes to every joined
    /// player whose window has room, and the players it skipped are handed back so their owner can send each a new
    /// baseline; it is never gated on <c>CanSend</c> for everyone, which is false while any one window is full. Authority
    /// routing (WS3.7) registers its own types, 64 to 127, on the same channel. Main thread only.
    /// </summary>
    internal sealed class NetworkWorldChannel
    {
        private readonly IWorldMessageHandler?[] handlers = new IWorldMessageHandler?[128];
        private readonly WorldWireReader reader = new WorldWireReader();
        private readonly List<ParticipantId> skipped = new List<ParticipantId>();
        private IInternalSystemChannel? channel;

        /// <summary>Gets whether the channel is open.</summary>
        internal bool IsOpen => channel != null && channel.IsOpen;

        /// <summary>Gets the channel's generation: it changes whenever a stream through it starts, restarts or ends.</summary>
        internal ulong Generation => channel?.Generation ?? 0;

        /// <summary>Gets the messages taken that were malformed, of a version this build does not read, or of a type nobody handles.</summary>
        internal long Discarded { get; private set; }

        /// <summary>Gets the messages handed to a handler.</summary>
        internal long Handled { get; private set; }

        /// <summary>Gets the messages sent, counted once per send, whatever the number of recipients.</summary>
        internal long Sent { get; private set; }

        /// <summary>Gets the sends refused because a window was full or the channel unavailable.</summary>
        internal long Refused { get; private set; }

        /// <summary>Uses <paramref name="opened"/>, the channel the provider opened; disposing the old one first.</summary>
        internal void Open(IInternalSystemChannel opened)
        {
            if (opened == null) throw new ArgumentNullException(nameof(opened));
            Close();
            channel = opened;
        }

        /// <summary>Closes the channel; its stream state goes with it.</summary>
        internal void Close()
        {
            var current = channel;
            channel = null;
            current?.Dispose();
        }

        /// <summary>Registers the handler of one message type.</summary>
        internal void Register(byte type, IWorldMessageHandler handler)
        {
            if (type >= handlers.Length) throw new ArgumentOutOfRangeException(nameof(type));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (handlers[type] != null && !ReferenceEquals(handlers[type], handler))
            {
                throw new InvalidOperationException("World message type " + type + " already has a handler.");
            }

            handlers[type] = handler;
        }

        /// <summary>Gets the generation of the stream with <paramref name="peer"/>; zero when there is none.</summary>
        internal ulong GetGeneration(ParticipantId peer) => channel?.GetGeneration(peer) ?? 0;

        /// <summary>Takes every waiting message and hands it to its handler. Messages are dropped once the channel closes.</summary>
        internal void Receive()
        {
            var current = channel;
            if (current == null) return;
            while (current.IsOpen && current.TryReceive(out var message))
            {
                reader.Reset(message.Payload);
                if (!reader.TryReadHeader(out var type) || type >= handlers.Length || handlers[type] == null)
                {
                    Discarded++;
                    continue;
                }

                bool handled;
                try
                {
                    handled = handlers[type]!.Handle(type, message.Sender, reader);
                }
                catch (Exception)
                {
                    // A handler fault is the handler's to report; one message never stops the rest.
                    handled = false;
                }

                if (handled) Handled++;
                else Discarded++;
            }
        }

        /// <summary>
        /// Host: sends what <paramref name="writer"/> holds to every joined player whose window has room, and appends the
        /// players it skipped to <paramref name="skippedPlayers"/>. Returns how many players it reached.
        /// </summary>
        internal int Broadcast(WorldWireWriter writer, List<ParticipantId> skippedPlayers)
        {
            if (skippedPlayers == null) throw new ArgumentNullException(nameof(skippedPlayers));
            var current = channel;
            if (current == null || !current.IsOpen) return 0;
            skipped.Clear();
            var result = current.Send(writer.Segment, InternalSystemTarget.Everyone, skipped);
            Sent++;
            if (!result.TryGetValue(out var reached))
            {
                Refused++;
                return 0;
            }

            if (skipped.Count > 0)
            {
                Refused += skipped.Count;
                skippedPlayers.AddRange(skipped);
            }

            return reached;
        }

        /// <summary>
        /// Host: sends what <paramref name="writer"/> holds to one player, only while its window would also take a message of
        /// <paramref name="probeBytes"/>, so a paced send never takes the last of the room the broadcasts behind it need.
        /// </summary>
        internal bool TrySendTo(ParticipantId player, WorldWireWriter writer, int probeBytes)
        {
            var current = channel;
            if (current == null || !current.IsOpen) return false;
            var target = InternalSystemTarget.Only(player);
            if (!current.CanSend(target, Math.Max(writer.Length, probeBytes)))
            {
                Refused++;
                return false;
            }

            return Count(current.Send(writer.Segment, target));
        }

        /// <summary>Host: whether a message of <paramref name="bytes"/> would fit one player's window now.</summary>
        internal bool CanSendTo(ParticipantId player, int bytes)
        {
            var current = channel;
            return current != null && current.IsOpen && current.CanSend(InternalSystemTarget.Only(player), bytes);
        }

        /// <summary>Player: sends what <paramref name="writer"/> holds to the host.</summary>
        internal bool TrySendToHost(WorldWireWriter writer)
        {
            var current = channel;
            if (current == null || !current.IsOpen) return false;
            if (!current.CanSend(InternalSystemTarget.Host, writer.Length))
            {
                Refused++;
                return false;
            }

            return Count(current.Send(writer.Segment, InternalSystemTarget.Host));
        }

        private bool Count(OperationResult<int> result)
        {
            Sent++;
            if (result.Succeeded && result.Value > 0) return true;
            Refused++;
            return false;
        }
    }
}
