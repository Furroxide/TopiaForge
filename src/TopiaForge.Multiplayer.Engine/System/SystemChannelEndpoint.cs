using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// One open system channel: its owner's way to send, and the queue of what peers sent it. The engine fills the queue
    /// as frames arrive, each message within its sender's window, so the queue never holds more than a window per sender;
    /// the owner takes messages on the main thread, which opens the sender's window again. Closing it drops what waits.
    /// </summary>
    internal sealed class SystemChannelEndpoint : IInternalSystemChannel
    {
        private readonly SessionEngine engine;
        private readonly Queue<Pending> inbox = new Queue<Pending>();
        private long sent;
        private long received;
        private long dropped;
        private long refused;
        private bool open = true;

        internal SystemChannelEndpoint(SessionEngine engine, InternalSystemChannelKind kind)
        {
            this.engine = engine;
            Kind = kind;
        }

        public InternalSystemChannelKind Kind { get; }

        public bool IsOpen => open;

        public ulong Generation
        {
            get
            {
                engine.RequireOwnerThread();
                return engine.SystemGeneration;
            }
        }

        /// <summary>Gets or sets a filter that drops an arriving message when it returns false; the provider's server mute.</summary>
        internal Func<ParticipantId, bool>? Admit { get; set; }

        public ulong GetGeneration(ParticipantId peer) => engine.SystemGenerationOf(peer);

        public bool CanSend(InternalSystemTarget target, int payloadBytes) => engine.CanSendSystem(this, target, payloadBytes);

        public OperationResult<int> Send(ArraySegment<byte> payload, InternalSystemTarget target, ICollection<ParticipantId>? skipped = null) =>
            engine.SendSystem(this, payload, target, skipped);

        public bool TryReceive(out InternalSystemMessage message)
        {
            engine.RequireOwnerThread();
            if (inbox.Count == 0)
            {
                message = default;
                return false;
            }

            var next = inbox.Dequeue();
            next.Stream.Consume(next.Message.Payload.Length);
            message = next.Message;
            return true;
        }

        public InternalSystemChannelStatistics GetStatistics() =>
            new InternalSystemChannelStatistics(Kind, sent, received, dropped, inbox.Count, refused);

        /// <summary>Closes the channel. Safe from any thread; the engine applies it on its own.</summary>
        public void Dispose() => engine.CloseSystemChannel(this);

        /// <summary>
        /// Takes one message that arrived on <paramref name="stream"/>, or drops and counts it when the channel is closed or
        /// refuses the sender. Either way a dropped message leaves the stream, so the sender's window opens again.
        /// </summary>
        internal void Deliver(ParticipantId sender, byte[] payload, SystemStream stream)
        {
            if (!open || (Admit != null && !Admit(sender)))
            {
                dropped++;
                stream.Consume(payload.Length);
                return;
            }

            inbox.Enqueue(new Pending(new InternalSystemMessage(sender, payload), stream));
            received++;
        }

        internal void CountSent(int recipients) => sent += recipients;

        /// <summary>Counts a send refused for a full window or the rate.</summary>
        internal void CountRefused() => refused++;

        /// <summary>Counts a message that belonged to a stream already gone, or that its link never sent.</summary>
        internal void CountDropped() => dropped++;

        /// <summary>Drops what waits, because the session or the connection it belongs to was replaced or ended.</summary>
        internal void DropPending()
        {
            foreach (var pending in inbox) pending.Stream.Consume(pending.Message.Payload.Length);
            dropped += inbox.Count;
            inbox.Clear();
        }

        /// <summary>Drops what waits from <paramref name="sender"/>, whose stream restarted, and keeps everyone else's in order.</summary>
        internal void DropFrom(ParticipantId sender)
        {
            if (inbox.Count == 0) return;
            var kept = inbox.ToArray();
            inbox.Clear();
            foreach (var pending in kept)
            {
                if (pending.Message.Sender.Equals(sender))
                {
                    pending.Stream.Consume(pending.Message.Payload.Length);
                    dropped++;
                }
                else
                {
                    inbox.Enqueue(pending);
                }
            }
        }

        internal void Close()
        {
            open = false;
            Admit = null;
            DropPending();
        }

        private readonly struct Pending
        {
            internal Pending(InternalSystemMessage message, SystemStream stream)
            {
                Message = message;
                Stream = stream;
            }

            internal InternalSystemMessage Message { get; }

            internal SystemStream Stream { get; }
        }
    }
}
