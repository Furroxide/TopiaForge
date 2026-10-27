using System.Collections.Generic;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// One system channel between this engine and one peer, in both directions, for the life of one stream: from a join
    /// or a session replacement to the next. It counts what each side sent and what the receiving owner took, which is
    /// what the windows in <see cref="InternalSystemLimits"/> are measured in. A restarted stream is a new object, so a
    /// message or a credit from the old one can never count against the new.
    /// </summary>
    internal sealed class SystemStream
    {
        internal SystemStream(byte kind)
        {
            Kind = kind;
        }

        internal byte Kind { get; }

        /// <summary>Gets the messages this side accepted from the peer.</summary>
        internal long Received { get; private set; }

        internal long ReceivedBytes { get; private set; }

        /// <summary>Gets the received messages that left this side's channel: taken by the owner, dropped or unclaimed.</summary>
        internal long Consumed { get; private set; }

        internal long ConsumedBytes { get; private set; }

        /// <summary>Gets whether the peer has not yet heard how much this side consumed.</summary>
        internal bool CreditDue { get; set; }

        /// <summary>Gets whether the stream was replaced; a message still held from it consumes nothing that counts.</summary>
        internal bool Retired { get; set; }

        /// <summary>Gets the messages this side sent the peer.</summary>
        internal long Sent { get; private set; }

        internal long SentBytes { get; private set; }

        /// <summary>Gets what the peer last said it consumed of what this side sent.</summary>
        internal long PeerConsumed { get; private set; }

        internal long PeerConsumedBytes { get; private set; }

        /// <summary>Gets whether this side had a message for the peer that did not fit, and the peer has credited nothing since.</summary>
        internal bool IsBlocked { get; private set; }

        /// <summary>Gets when this side first had a message that did not fit, while <see cref="IsBlocked"/>.</summary>
        internal long BlockedSinceMicros { get; private set; }

        /// <summary>Returns whether one more message of <paramref name="bytes"/> fits the peer's window for this side.</summary>
        internal bool HasRoom(int bytes, int windowMessages, int windowBytes) =>
            Sent - PeerConsumed < windowMessages && SentBytes - PeerConsumedBytes + bytes <= windowBytes;

        /// <summary>Records that a message for the peer did not fit at <paramref name="nowMicros"/>; the first such moment counts.</summary>
        internal void MarkBlocked(long nowMicros)
        {
            if (IsBlocked) return;
            IsBlocked = true;
            BlockedSinceMicros = nowMicros;
        }

        internal void CountSent(int bytes)
        {
            Sent++;
            SentBytes += bytes;
        }

        /// <summary>Returns whether one more arriving message of <paramref name="bytes"/> stays within the sender's window.</summary>
        internal bool FitsWindow(int bytes, int windowMessages, int windowBytes) =>
            Received - Consumed < windowMessages && ReceivedBytes - ConsumedBytes + bytes <= windowBytes;

        internal void CountReceived(int bytes)
        {
            Received++;
            ReceivedBytes += bytes;
        }

        /// <summary>Records that one received message left the channel, so its window opens again.</summary>
        internal void Consume(int bytes)
        {
            if (Retired) return;
            Consumed++;
            ConsumedBytes += bytes;
            CreditDue = true;
        }

        /// <summary>
        /// Takes the peer's report of what it consumed. Reports only grow and never exceed what was sent; anything else
        /// breaks the protocol.
        /// </summary>
        internal void ApplyCredit(ulong consumed, ulong consumedBytes)
        {
            if (consumed < (ulong)PeerConsumed || consumed > (ulong)Sent || consumedBytes < (ulong)PeerConsumedBytes || consumedBytes > (ulong)SentBytes)
            {
                throw new ProtocolException("A peer credited system messages it was never sent.");
            }

            // Any credit is the peer's owner taking something, so a blocked sender is no longer waiting on a stalled peer.
            if (consumed > (ulong)PeerConsumed) IsBlocked = false;
            PeerConsumed = (long)consumed;
            PeerConsumedBytes = (long)consumedBytes;
        }
    }

    /// <summary>
    /// The streams of one connection, one per system channel kind, created as they are first used. Kinds this build has
    /// no channel for, which a newer peer may send, get a stream too, so their messages are credited as taken.
    /// </summary>
    internal sealed class SystemStreamSet
    {
        internal const int Slots = 7;
        private readonly SystemStream?[] streams = new SystemStream?[Slots];
        private List<SystemStream>? unknown;

        /// <summary>Gets whether any stream owes its peer a credit.</summary>
        internal bool CreditsDue
        {
            get
            {
                for (var index = 1; index < Slots; index++)
                {
                    if (streams[index]?.CreditDue == true) return true;
                }

                if (unknown == null) return false;
                foreach (var stream in unknown)
                {
                    if (stream.CreditDue) return true;
                }

                return false;
            }
        }

        internal static bool IsKind(byte kind) => kind >= 1 && kind < Slots;

        /// <summary>Gets the stream of <paramref name="kind"/>, creating it on first use.</summary>
        internal SystemStream Get(byte kind) => streams[kind] ??= new SystemStream(kind);

        /// <summary>Gets the stream of <paramref name="kind"/> if it was ever used.</summary>
        internal SystemStream? Find(byte kind) => IsKind(kind) ? streams[kind] : null;

        /// <summary>Gets the stream of a kind this build has no channel for, creating it on first use; at most 251 of them.</summary>
        internal SystemStream GetUnknown(byte kind)
        {
            unknown ??= new List<SystemStream>();
            foreach (var stream in unknown)
            {
                if (stream.Kind == kind) return stream;
            }

            var created = new SystemStream(kind);
            unknown.Add(created);
            return created;
        }

        /// <summary>Gets when the longest-blocked stream first had a message that did not fit, or null when none is blocked.</summary>
        internal long? BlockedSinceMicros()
        {
            long? earliest = null;
            for (var index = 1; index < Slots; index++)
            {
                var stream = streams[index];
                if (stream != null && stream.IsBlocked && (earliest == null || stream.BlockedSinceMicros < earliest)) earliest = stream.BlockedSinceMicros;
            }

            return earliest;
        }

        /// <summary>Starts every stream again: the session was replaced, so both sides count from zero.</summary>
        internal void Restart()
        {
            for (var index = 1; index < Slots; index++)
            {
                var stream = streams[index];
                if (stream == null) continue;
                stream.Retired = true;
                streams[index] = null;
            }

            unknown = null;
        }

        /// <summary>Writes a credit for every stream whose consumption the peer has not heard yet.</summary>
        internal void WriteCredits(ProtocolWriter frame, ProtocolWriter body, ulong epoch, ulong generation)
        {
            for (var index = 1; index < Slots; index++) WriteCredit(streams[index], frame, body, epoch, generation);
            if (unknown == null) return;
            foreach (var stream in unknown) WriteCredit(stream, frame, body, epoch, generation);
        }

        private static void WriteCredit(SystemStream? stream, ProtocolWriter frame, ProtocolWriter body, ulong epoch, ulong generation)
        {
            if (stream == null || !stream.CreditDue) return;
            stream.CreditDue = false;
            SystemMessages.WriteCredit(frame, body, epoch, generation, stream.Kind, (ulong)stream.Consumed, (ulong)stream.ConsumedBytes);
        }
    }
}
