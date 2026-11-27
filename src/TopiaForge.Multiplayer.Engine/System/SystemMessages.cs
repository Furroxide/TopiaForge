using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// One system message as it travels: the session epoch and the connection's join generation it belongs to, its
    /// channel and its payload.
    /// </summary>
    internal readonly struct SystemWireMessage
    {
        internal SystemWireMessage(ulong epoch, ulong generation, byte kind, byte[] payload)
        {
            Epoch = epoch;
            Generation = generation;
            Kind = kind;
            Payload = payload;
        }

        internal ulong Epoch { get; }

        /// <summary>Gets the join generation the host gave the connection in its <c>JoinAccepted</c>.</summary>
        internal ulong Generation { get; }

        /// <summary>Gets the channel, as an <see cref="InternalSystemChannelKind"/> value. A peer ignores kinds it does not know.</summary>
        internal byte Kind { get; }

        internal byte[] Payload { get; }
    }

    /// <summary>One receiver's report of how much of a stream its owner consumed, cumulative since the stream began.</summary>
    internal readonly struct SystemCreditMessage
    {
        internal SystemCreditMessage(ulong epoch, ulong generation, byte kind, ulong consumed, ulong consumedBytes)
        {
            Epoch = epoch;
            Generation = generation;
            Kind = kind;
            Consumed = consumed;
            ConsumedBytes = consumedBytes;
        }

        internal ulong Epoch { get; }
        internal ulong Generation { get; }
        internal byte Kind { get; }
        internal ulong Consumed { get; }
        internal ulong ConsumedBytes { get; }
    }

    /// <summary>
    /// The bodies of <see cref="MessageType.SystemMessage"/>, <c>[epoch varint][generation varint][kind byte][payload
    /// bytes]</c>, and <see cref="MessageType.SystemCredit"/>, <c>[epoch varint][generation varint][kind byte][consumed
    /// varint][consumed bytes varint]</c>. The epoch and the generation let a peer drop what belongs to a session it left
    /// or a connection it replaced; the payload is bounded by <see cref="InternalSystemLimits.MaximumPayloadBytes"/>.
    /// </summary>
    internal static class SystemMessages
    {
        internal static void Write(ProtocolWriter frame, ProtocolWriter body, ulong epoch, ulong generation, byte kind, byte[] payload)
        {
            body.Reset();
            body.WriteVarUInt(epoch);
            body.WriteVarUInt(generation);
            body.WriteByte(kind);
            body.WriteBytes(payload);
            frame.WriteEnvelope(MessageType.SystemMessage, body);
        }

        /// <summary>Reads a system message; a malformed or oversized one throws <see cref="ProtocolException"/>.</summary>
        internal static SystemWireMessage Read(ProtocolReader body)
        {
            ReadHeader(body, out var epoch, out var generation, out var kind);
            return new SystemWireMessage(epoch, generation, kind, ReadPayload(body));
        }

        /// <summary>Reads what a system message belongs to, leaving its payload, so a stale one costs no copy.</summary>
        internal static void ReadHeader(ProtocolReader body, out ulong epoch, out ulong generation, out byte kind)
        {
            epoch = body.ReadVarUInt();
            generation = body.ReadVarUInt();
            kind = body.ReadByte();
        }

        /// <summary>Reads the payload after <see cref="ReadHeader"/>; an oversized one throws <see cref="ProtocolException"/>.</summary>
        internal static byte[] ReadPayload(ProtocolReader body) => body.ReadBytes(InternalSystemLimits.MaximumPayloadBytes);

        /// <summary>
        /// Checks and skips the payload after <see cref="ReadHeader"/> without copying it, and returns its length; an oversized
        /// one throws <see cref="ProtocolException"/>.
        /// </summary>
        internal static int SkipPayload(ProtocolReader body)
        {
            var length = body.ReadLength(InternalSystemLimits.MaximumPayloadBytes);
            body.Skip(length);
            return length;
        }

        internal static void WriteCredit(ProtocolWriter frame, ProtocolWriter body, ulong epoch, ulong generation, byte kind, ulong consumed, ulong consumedBytes)
        {
            body.Reset();
            body.WriteVarUInt(epoch);
            body.WriteVarUInt(generation);
            body.WriteByte(kind);
            body.WriteVarUInt(consumed);
            body.WriteVarUInt(consumedBytes);
            frame.WriteEnvelope(MessageType.SystemCredit, body);
        }

        internal static SystemCreditMessage ReadCredit(ProtocolReader body)
        {
            var epoch = body.ReadVarUInt();
            var generation = body.ReadVarUInt();
            var kind = body.ReadByte();
            var consumed = body.ReadVarUInt();
            var consumedBytes = body.ReadVarUInt();
            return new SystemCreditMessage(epoch, generation, kind, consumed, consumedBytes);
        }
    }

    /// <summary>A system message a host owes one connection, captured when it was sent.</summary>
    internal sealed class SystemItem : OutboundItem
    {
        private readonly ulong epoch;
        private readonly ulong generation;
        private readonly byte[] payload;

        internal SystemItem(ulong epoch, ulong generation, byte kind, byte[] payload)
        {
            this.epoch = epoch;
            this.generation = generation;
            Kind = kind;
            this.payload = payload;
        }

        internal byte Kind { get; }

        internal override void Write(HostConnection connection, FlushContext context) =>
            SystemMessages.Write(context.Frame, context.Body, epoch, generation, Kind, payload);
    }
}
