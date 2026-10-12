using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal readonly struct ParticipantEntry
    {
        internal ParticipantEntry(
            string id,
            string displayName,
            bool isConnected,
            bool isHost,
            MultiplayerConnectionQuality quality,
            int? pingMilliseconds,
            string? playerKey)
        {
            Id = id;
            DisplayName = displayName;
            IsConnected = isConnected;
            IsHost = isHost;
            Quality = quality;
            PingMilliseconds = pingMilliseconds;
            PlayerKey = playerKey;
        }

        internal string Id { get; }
        internal string DisplayName { get; }
        internal bool IsConnected { get; }
        internal bool IsHost { get; }
        internal MultiplayerConnectionQuality Quality { get; }
        internal int? PingMilliseconds { get; }
        internal string? PlayerKey { get; }
    }

    internal sealed class SessionReplacedMessage
    {
        internal SessionReplacedMessage(string sessionId, ulong seed, ulong epoch)
        {
            SessionId = sessionId;
            Seed = seed;
            Epoch = epoch;
        }

        internal string SessionId { get; }
        internal ulong Seed { get; }
        internal ulong Epoch { get; }
    }

    /// <summary>Encoders and decoders for session lifecycle, participant and clock messages.</summary>
    internal static class SessionMessages
    {
        private const byte ConnectedFlag = 1;
        private const byte HostFlag = 2;

        internal static void WriteEpochMessage(ProtocolWriter frame, ProtocolWriter body, byte type, ulong epoch)
        {
            body.Reset();
            body.WriteVarUInt(epoch);
            frame.WriteEnvelope(type, body);
        }

        internal static void WriteEmpty(ProtocolWriter frame, ProtocolWriter body, byte type)
        {
            body.Reset();
            frame.WriteEnvelope(type, body);
        }

        internal static void WriteParticipants(ProtocolWriter frame, ProtocolWriter body, IReadOnlyList<ParticipantEntry> participants)
        {
            body.Reset();
            body.WriteVarUInt((ulong)participants.Count);
            foreach (var participant in participants)
            {
                body.WriteString(participant.Id);
                body.WriteString(participant.DisplayName);
                body.WriteByte((byte)((participant.IsConnected ? ConnectedFlag : 0) | (participant.IsHost ? HostFlag : 0)));
                body.WriteByte((byte)participant.Quality);
                body.WriteVarUInt(participant.PingMilliseconds.HasValue ? (ulong)participant.PingMilliseconds.Value + 1 : 0);
                body.WriteOptionalString(participant.PlayerKey);
            }

            frame.WriteEnvelope(MessageType.ParticipantsChanged, body);
        }

        internal static List<ParticipantEntry> ReadParticipants(ProtocolReader reader)
        {
            var count = (int)reader.ReadVarUInt(ProtocolLimits.MaximumParticipants);
            var participants = new List<ParticipantEntry>(count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var hosts = 0;
            for (var index = 0; index < count; index++)
            {
                var id = reader.ReadIdentity();
                var displayName = reader.ReadIdentity();
                var flags = reader.ReadByte();
                if ((flags & ~(ConnectedFlag | HostFlag)) != 0) throw new ProtocolException("A participant record held unknown flags.");
                var quality = (MultiplayerConnectionQuality)reader.ReadByte();
                if (!Enum.IsDefined(typeof(MultiplayerConnectionQuality), quality))
                {
                    throw new ProtocolException("A participant record held an unknown quality bucket.");
                }

                var ping = reader.ReadVarUInt((ulong)MultiplayerParticipant.MaximumPingMilliseconds + 1);
                var playerKey = reader.ReadOptionalIdentity();
                if (!seen.Add(id)) throw new ProtocolException("A participant list repeated an identity.");
                if ((flags & HostFlag) != 0) hosts++;
                participants.Add(new ParticipantEntry(
                    id,
                    displayName,
                    (flags & ConnectedFlag) != 0,
                    (flags & HostFlag) != 0,
                    quality,
                    ping == 0 ? (int?)null : (int)(ping - 1),
                    playerKey));
            }

            if (hosts > 1) throw new ProtocolException("A participant list marked more than one host.");
            return participants;
        }

        internal static void WriteSessionEnded(ProtocolWriter frame, ProtocolWriter body, MultiplayerSessionEndReason reason, string message)
        {
            body.Reset();
            body.WriteVarUInt((ulong)reason);
            body.WriteString(ProtocolLimits.BoundMessage(message));
            frame.WriteEnvelope(MessageType.SessionEnded, body);
        }

        internal static void WriteSessionReplaced(ProtocolWriter frame, ProtocolWriter body, SessionReplacedMessage message)
        {
            body.Reset();
            body.WriteString(message.SessionId);
            body.WriteVarUInt(message.Seed);
            body.WriteVarUInt(message.Epoch);
            frame.WriteEnvelope(MessageType.SessionReplaced, body);
        }

        internal static SessionReplacedMessage ReadSessionReplaced(ProtocolReader reader) =>
            new SessionReplacedMessage(reader.ReadIdentity(), reader.ReadVarUInt(), reader.ReadVarUInt());

        internal static void WriteCommit(ProtocolWriter frame, ProtocolWriter body, ulong tick, ulong acknowledgedSequence)
        {
            body.Reset();
            body.WriteVarUInt(tick);
            body.WriteVarUInt(acknowledgedSequence);
            frame.WriteEnvelope(MessageType.Commit, body);
        }

        internal static void WriteClockSync(ProtocolWriter frame, ProtocolWriter body, long hostMicros, ulong tick, long rttMicros)
        {
            body.Reset();
            body.WriteVarUInt((ulong)Math.Max(0, hostMicros));
            body.WriteVarUInt(tick);
            body.WriteVarUInt((ulong)Math.Max(0, rttMicros));
            frame.WriteEnvelope(MessageType.ClockSync, body);
        }

        internal static void WriteClockSyncReply(ProtocolWriter frame, ProtocolWriter body, ulong echoedHostMicros)
        {
            body.Reset();
            body.WriteVarUInt(echoedHostMicros);
            frame.WriteEnvelope(MessageType.ClockSyncReply, body);
        }
    }
}
