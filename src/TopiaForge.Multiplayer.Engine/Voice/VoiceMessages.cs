using System;
using System.Collections.Generic;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>One speaker a host announces: the index its relayed frames carry, whose they are, and whether they are muted.</summary>
    internal readonly struct VoiceRosterEntry
    {
        internal VoiceRosterEntry(ulong speaker, string participantId, bool serverMuted)
        {
            Speaker = speaker;
            ParticipantId = participantId;
            ServerMuted = serverMuted;
        }

        internal ulong Speaker { get; }
        internal string ParticipantId { get; }
        internal bool ServerMuted { get; }
    }

    /// <summary>
    /// The two voice messages on the reliable session channel. <c>VoiceRoster</c> (host to client) maps speaker indexes
    /// to participants and says who the host muted; <c>VoiceMutes</c> (client to host) names the speakers a listener
    /// does not want relayed.
    /// </summary>
    internal static class VoiceMessages
    {
        private const byte ServerMutedFlag = 1;
        private const byte MuteAllFlag = 1;

        internal static void WriteRoster(ProtocolWriter frame, ProtocolWriter body, IReadOnlyList<VoiceRosterEntry> entries)
        {
            body.Reset();
            body.WriteVarUInt((ulong)entries.Count);
            foreach (var entry in entries)
            {
                body.WriteVarUInt(entry.Speaker);
                body.WriteString(entry.ParticipantId);
                body.WriteByte(entry.ServerMuted ? ServerMutedFlag : (byte)0);
            }

            frame.WriteEnvelope(MessageType.VoiceRoster, body);
        }

        internal static List<VoiceRosterEntry> ReadRoster(ProtocolReader reader)
        {
            var count = (int)reader.ReadVarUInt(ProtocolLimits.MaximumParticipants);
            var entries = new List<VoiceRosterEntry>(count);
            var speakers = new HashSet<ulong>();
            var participants = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var speaker = reader.ReadVarUInt();
                var participant = reader.ReadIdentity();
                var flags = reader.ReadByte();
                if (speaker == 0) throw new ProtocolException("A voice roster used speaker index zero.");
                if ((flags & ~ServerMutedFlag) != 0) throw new ProtocolException("A voice roster entry held unknown flags.");
                if (!speakers.Add(speaker) || !participants.Add(participant)) throw new ProtocolException("A voice roster repeated a speaker.");
                entries.Add(new VoiceRosterEntry(speaker, participant, (flags & ServerMutedFlag) != 0));
            }

            return entries;
        }

        internal static void WriteMutes(ProtocolWriter frame, ProtocolWriter body, bool muteAll, IReadOnlyCollection<string> muted)
        {
            body.Reset();
            body.WriteByte(muteAll ? MuteAllFlag : (byte)0);
            body.WriteVarUInt((ulong)muted.Count);
            foreach (var participant in muted) body.WriteString(participant);
            frame.WriteEnvelope(MessageType.VoiceMutes, body);
        }

        internal static void ReadMutes(ProtocolReader reader, out bool muteAll, out HashSet<string> muted)
        {
            var flags = reader.ReadByte();
            if ((flags & ~MuteAllFlag) != 0) throw new ProtocolException("A voice mute set held unknown flags.");
            muteAll = (flags & MuteAllFlag) != 0;
            var count = (int)reader.ReadVarUInt(ProtocolLimits.MaximumParticipants);
            muted = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                if (!muted.Add(reader.ReadIdentity())) throw new ProtocolException("A voice mute set repeated a participant.");
            }
        }
    }

    /// <summary>A voice roster a host owes a connection, captured when queued.</summary>
    internal sealed class VoiceRosterItem : OutboundItem
    {
        private readonly List<VoiceRosterEntry> entries;

        internal VoiceRosterItem(List<VoiceRosterEntry> entries)
        {
            this.entries = entries;
        }

        internal override void Write(HostConnection connection, FlushContext context) =>
            VoiceMessages.WriteRoster(context.Frame, context.Body, entries);
    }
}
