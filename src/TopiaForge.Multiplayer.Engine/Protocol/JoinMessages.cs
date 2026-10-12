using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>One generated contract a joining peer registered, as compared during admission.</summary>
    internal readonly struct ContractEntry
    {
        internal ContractEntry(string owner, string id, int wireFormatRevision, string schemaSha256)
        {
            Owner = owner;
            Id = id;
            WireFormatRevision = wireFormatRevision;
            SchemaSha256 = schemaSha256;
        }

        internal string Owner { get; }
        internal string Id { get; }
        internal int WireFormatRevision { get; }

        /// <summary>Gets the lowercase hexadecimal schema digest.</summary>
        internal string SchemaSha256 { get; }
    }

    internal sealed class JoinRequestMessage
    {
        internal JoinRequestMessage(string protocolVersion, string displayName, IReadOnlyList<ContractEntry> contracts, byte[] content)
        {
            ProtocolVersion = protocolVersion;
            DisplayName = displayName;
            Contracts = contracts;
            Content = content;
        }

        internal string ProtocolVersion { get; }
        internal string DisplayName { get; }
        internal IReadOnlyList<ContractEntry> Contracts { get; }
        internal byte[] Content { get; }
    }

    internal sealed class JoinAcceptedMessage
    {
        internal string SessionId = string.Empty;
        internal ulong Seed;
        internal ulong Epoch;
        internal string ParticipantId = string.Empty;
        internal string? HostParticipantId;
        internal ulong Tick;
        internal int StepRateHz;
        internal ulong NextSequence;
        internal byte[] Report = Array.Empty<byte>();
    }

    internal sealed class JoinRejectedMessage
    {
        internal JoinRejectedMessage(MultiplayerSessionEndReason reason, string message, byte[] report)
        {
            Reason = reason;
            Message = message;
            Report = report;
        }

        internal MultiplayerSessionEndReason Reason { get; }
        internal string Message { get; }
        internal byte[] Report { get; }
    }

    /// <summary>Encoders and decoders for joining and admission.</summary>
    internal static class JoinMessages
    {
        internal static void WriteJoinRequest(ProtocolWriter frame, ProtocolWriter body, JoinRequestMessage message)
        {
            body.Reset();
            body.WriteString(message.ProtocolVersion);
            body.WriteString(message.DisplayName);
            body.WriteVarUInt((ulong)message.Contracts.Count);
            foreach (var contract in message.Contracts)
            {
                body.WriteString(contract.Owner);
                body.WriteString(contract.Id);
                body.WriteVarUInt((ulong)contract.WireFormatRevision);
                body.WriteRaw(HexToBytes(contract.SchemaSha256), 0, 32);
            }

            body.WriteBytes(message.Content);
            frame.WriteEnvelope(MessageType.JoinRequest, body);
        }

        internal static JoinRequestMessage ReadJoinRequest(ProtocolReader reader)
        {
            var protocolVersion = reader.ReadString(32);
            var displayName = reader.ReadString(ProtocolLimits.IdentityCharacters);
            var count = (int)reader.ReadVarUInt(ProtocolLimits.MaximumContracts);
            var contracts = new List<ContractEntry>(count);
            for (var index = 0; index < count; index++)
            {
                var owner = reader.ReadIdentity();
                var id = reader.ReadIdentity();
                var revision = (int)reader.ReadVarUInt(int.MaxValue);
                if (revision < 1) throw new ProtocolException("A contract wire-format revision must be at least 1.");
                var digest = new byte[32];
                for (var offset = 0; offset < digest.Length; offset++) digest[offset] = reader.ReadByte();
                contracts.Add(new ContractEntry(owner, id, revision, BytesToHex(digest)));
            }

            var content = reader.ReadBytes(ProtocolLimits.MaximumOpaqueBytes);
            return new JoinRequestMessage(protocolVersion, displayName, contracts, content);
        }

        internal static void WriteJoinAccepted(ProtocolWriter frame, ProtocolWriter body, JoinAcceptedMessage message)
        {
            body.Reset();
            body.WriteString(message.SessionId);
            body.WriteVarUInt(message.Seed);
            body.WriteVarUInt(message.Epoch);
            body.WriteString(message.ParticipantId);
            body.WriteOptionalString(message.HostParticipantId);
            body.WriteVarUInt(message.Tick);
            body.WriteVarUInt((ulong)message.StepRateHz);
            body.WriteVarUInt(message.NextSequence);
            body.WriteBytes(message.Report);
            frame.WriteEnvelope(MessageType.JoinAccepted, body);
        }

        internal static JoinAcceptedMessage ReadJoinAccepted(ProtocolReader reader)
        {
            var message = new JoinAcceptedMessage
            {
                SessionId = reader.ReadIdentity(),
                Seed = reader.ReadVarUInt(),
                Epoch = reader.ReadVarUInt(),
                ParticipantId = reader.ReadIdentity(),
                HostParticipantId = reader.ReadOptionalIdentity(),
                Tick = reader.ReadVarUInt(),
                StepRateHz = (int)reader.ReadVarUInt(1000),
                NextSequence = reader.ReadVarUInt(),
                Report = reader.ReadBytes(ProtocolLimits.MaximumOpaqueBytes)
            };
            if (message.StepRateHz < 1) throw new ProtocolException("The host announced a step rate of zero.");
            return message;
        }

        internal static void WriteJoinRejected(ProtocolWriter frame, ProtocolWriter body, JoinRejectedMessage message)
        {
            body.Reset();
            body.WriteVarUInt((ulong)message.Reason);
            body.WriteString(ProtocolLimits.BoundMessage(message.Message));
            body.WriteBytes(message.Report);
            frame.WriteEnvelope(MessageType.JoinRejected, body);
        }

        internal static JoinRejectedMessage ReadJoinRejected(ProtocolReader reader) =>
            new JoinRejectedMessage(
                ReadEndReason(reader),
                reader.ReadString(ProtocolLimits.MessageCharacters),
                reader.ReadBytes(ProtocolLimits.MaximumOpaqueBytes));

        internal static MultiplayerSessionEndReason ReadEndReason(ProtocolReader reader)
        {
            var value = reader.ReadVarUInt(int.MaxValue);
            var reason = (MultiplayerSessionEndReason)(int)value;
            if (!Enum.IsDefined(typeof(MultiplayerSessionEndReason), reason))
            {
                throw new ProtocolException("The peer sent an unknown session end reason.");
            }

            return reason;
        }

        internal static string NormalizeDigest(string schemaSha256) => schemaSha256.ToLowerInvariant();

        private static byte[] HexToBytes(string hex)
        {
            if (hex.Length != 64) throw new ArgumentException("A SHA-256 digest needs 64 hexadecimal characters.", nameof(hex));
            var bytes = new byte[32];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)((HexValue(hex[index * 2]) << 4) | HexValue(hex[(index * 2) + 1]));
            }

            return bytes;
        }

        private static int HexValue(char character)
        {
            if (character >= '0' && character <= '9') return character - '0';
            if (character >= 'a' && character <= 'f') return character - 'a' + 10;
            if (character >= 'A' && character <= 'F') return character - 'A' + 10;
            throw new ArgumentException("A SHA-256 digest must be hexadecimal.", nameof(character));
        }

        private static string BytesToHex(byte[] bytes)
        {
            const string Digits = "0123456789abcdef";
            var characters = new char[bytes.Length * 2];
            for (var index = 0; index < bytes.Length; index++)
            {
                characters[index * 2] = Digits[bytes[index] >> 4];
                characters[(index * 2) + 1] = Digits[bytes[index] & 0xF];
            }

            return new string(characters);
        }
    }
}
