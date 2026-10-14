using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>What <see cref="SessionEngine.SubmitVoiceFrame"/> did with a frame.</summary>
    internal enum VoiceSubmitResult
    {
        /// <summary>The frame left in a datagram, alone or with the frame before it.</summary>
        Sent = 0,

        /// <summary>The frame waits, for at most two frames' time, to share a datagram with the next.</summary>
        Held = 1,

        /// <summary>The engine has no voice options.</summary>
        Disabled = 2,

        /// <summary>There is no ready session to talk in.</summary>
        NotConnected = 3,

        /// <summary>The host muted the local player, so the frame was not sent.</summary>
        ServerMuted = 4,

        /// <summary>The frame is not a valid packet, or its timestamp does not move forward.</summary>
        Invalid = 5,

        /// <summary>
        /// The frame, and the one waiting for it, were dropped: sending them would break the host's voice limits, as
        /// when frames bunch up after a stall.
        /// </summary>
        RateLimited = 6
    }

    /// <summary>
    /// Voice: the local talker's frames out, a host's relay between its clients, and frames in for playback. Voice rides
    /// the unreliable sequenced Voice channel, never the session stream; only the speaker roster and listener mute sets
    /// travel reliably. See docs/internal/MultiplayerVoice.md, part 2.
    /// </summary>
    internal sealed partial class SessionEngine
    {
        private const string VoiceKickMessage = "The host removed this player for abusing voice chat.";
        private readonly long[] voiceDropped = new long[(int)VoiceDropReason.InboxOverflow + 1];
        private readonly HashSet<string> localVoiceMutes = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, VoiceRosterEntry> voiceRoster = new Dictionary<ulong, VoiceRosterEntry>();
        private VoiceRelayPolicy? voicePolicy;
        private VoiceSpeaker? voicePace;
        private VoiceInbox? voiceInbox;
        private byte[] voiceScratch = Array.Empty<byte>();
        private byte[] heldVoice = Array.Empty<byte>();
        private long voiceFrameMicros;
        private ulong nextSpeakerIndex;
        private bool voiceRosterDirty;
        private bool localVoiceMuteAll;
        private ulong voiceSequence;
        private ulong lastVoiceTimestamp;
        private bool hasVoiceTimestamp;
        private bool voiceHeld;
        private ulong heldSequence;
        private ulong heldTimestamp;
        private VoiceFrameFlags heldFlags;
        private int heldLength;
        private long heldAtMicros;
        private long voiceSubmitted;
        private long voiceSent;
        private long voiceRelayed;
        private long voiceDelivered;
        private long voiceStrikes;
        private long voiceAutoMutes;
        private long voiceKicks;

        internal bool IsVoiceEnabled => options.Voice != null;

        /// <summary>
        /// Hands the engine one encoded frame of the local talker. Frames whose timestamps follow on share a datagram in
        /// pairs, so a lone frame waits for the next one, for at most two frames' time: frames reach the engine up to a
        /// pump late, so one frame's time would split pairs. An end-of-talk frame never waits. Sequences are assigned here.
        /// Timestamps must move forward within a session; after a role change they may start again. A client holds itself
        /// to the host's datagram rate and bitrate, dropping what would break them, so an honest talker is never struck.
        /// </summary>
        internal VoiceSubmitResult SubmitVoiceFrame(ulong timestamp, VoiceFrameFlags flags, ReadOnlySpan<byte> payload)
        {
            RequireOwnerThread();
            var voice = options.Voice;
            if (voice == null) return VoiceSubmitResult.Disabled;
            if ((flags & ~VoicePacket.KnownFlags) != 0 || payload.IsEmpty || payload.Length > voice.MaximumPacketBytes ||
                !voice.Codec.IsValidPacket(payload) || timestamp == ulong.MaxValue || (hasVoiceTimestamp && timestamp <= lastVoiceTimestamp))
            {
                return VoiceSubmitResult.Invalid;
            }

            if (!CanTalk(out var serverMuted)) return serverMuted ? VoiceSubmitResult.ServerMuted : VoiceSubmitResult.NotConnected;
            voiceSubmitted++;
            hasVoiceTimestamp = true;
            lastVoiceTimestamp = timestamp;
            var sequence = ++voiceSequence;
            if (voiceHeld)
            {
                if (timestamp == heldTimestamp + 1)
                {
                    voiceHeld = false;
                    return SendVoice(heldSequence, heldTimestamp, heldFlags, heldVoice.AsSpan(0, heldLength), flags, payload);
                }

                FlushHeldVoice();
            }

            if ((flags & VoiceFrameFlags.EndOfTalk) != 0)
            {
                return SendVoice(sequence, timestamp, flags, payload, VoiceFrameFlags.None, ReadOnlySpan<byte>.Empty);
            }

            voiceHeld = true;
            heldSequence = sequence;
            heldTimestamp = timestamp;
            heldFlags = flags;
            heldLength = payload.Length;
            heldAtMicros = nowMicros;
            payload.CopyTo(heldVoice);
            return VoiceSubmitResult.Held;
        }

        /// <summary>Dequeues the oldest received frame waiting for playback.</summary>
        internal bool TryReceiveVoiceFrame(out VoiceFrame frame)
        {
            RequireOwnerThread();
            if (voiceInbox != null) return voiceInbox.TryDequeue(out frame);
            frame = default;
            return false;
        }

        /// <summary>
        /// Mutes or unmutes a participant for everyone; a host drops their voice the moment it arrives. Unmuting also
        /// ends an automatic mute. Returns false when the participant is unknown or voice is off.
        /// </summary>
        internal bool SetServerMute(ParticipantId participant, bool muted)
        {
            RequireOwnerThread();
            if (Role != EngineRole.Host) throw new InvalidOperationException("Only a host mutes participants for everyone.");
            var speaker = FindParticipant(participant)?.Voice;
            if (speaker == null) return false;
            speaker.ServerMuted = muted;
            if (!muted) speaker.AutoMutedUntilMicros = long.MinValue;
            MarkVoiceRosterDirty();
            EndOperation();
            return true;
        }

        /// <summary>
        /// Sets whom the local player does not want to hear. A client also sends the set to its host, reliably, so muted
        /// speakers are never relayed to it; <paramref name="muteAll"/> stops all relayed voice.
        /// </summary>
        internal void SetListenerMutes(IReadOnlyCollection<ParticipantId> muted, bool muteAll = false)
        {
            if (muted == null) throw new ArgumentNullException(nameof(muted));
            RequireOwnerThread();
            if (muted.Count > ProtocolLimits.MaximumParticipants) throw new ArgumentException("Too many participants to mute.", nameof(muted));
            localVoiceMutes.Clear();
            foreach (var participant in muted)
            {
                if (!participant.IsValid) throw new ArgumentException("A muted participant id is invalid.", nameof(muted));
                localVoiceMutes.Add(participant.Value);
            }

            localVoiceMuteAll = muteAll;
            if (options.Voice != null && channel != null && channel.Phase != ClientPhase.AwaitingJoin) channel.VoiceMutesPending = true;
            EndOperation();
        }

        /// <summary>Gets whether the host has muted <paramref name="participant"/>, by hand or automatically.</summary>
        internal bool IsServerMuted(ParticipantId participant)
        {
            RequireOwnerThread();
            if (Role == EngineRole.Host) return FindParticipant(participant)?.Voice?.IsMuted(nowMicros) ?? false;
            foreach (var entry in voiceRoster.Values)
            {
                if (string.Equals(entry.ParticipantId, participant.Value, StringComparison.Ordinal)) return entry.ServerMuted;
            }

            return false;
        }

        internal VoiceStatistics GetVoiceStatistics()
        {
            RequireOwnerThread();
            return new VoiceStatistics(
                voiceSubmitted,
                voiceSent,
                voiceRelayed,
                voiceDelivered,
                (long[])voiceDropped.Clone(),
                voiceStrikes,
                voiceAutoMutes,
                voiceKicks);
        }

        private void InitializeVoice()
        {
            var voice = options.Voice;
            if (voice == null) return;
            voicePolicy = new VoiceRelayPolicy(voice);
            voicePace = new VoiceSpeaker(1, voice);
            voiceInbox = new VoiceInbox(voice.InboxCapacity);
            voiceScratch = new byte[VoicePacket.MaximumBytes(voice.MaximumPacketBytes)];
            heldVoice = new byte[voice.MaximumPacketBytes];
            voiceFrameMicros = Math.Max(1, (long)voice.Codec.FrameSamples * 1_000_000 / voice.Codec.SampleRate);
        }

        private VoiceSpeaker? CreateVoiceSpeaker() => options.Voice == null ? null : new VoiceSpeaker(++nextSpeakerIndex, options.Voice);

        private void MarkVoiceRosterDirty()
        {
            if (options.Voice != null) voiceRosterDirty = true;
        }

        private bool CanTalk(out bool serverMuted)
        {
            serverMuted = false;
            if (ended) return false;
            if (Role == EngineRole.Host)
            {
                var speaker = localParticipantId.HasValue ? FindParticipant(localParticipantId.Value)?.Voice : null;
                if (speaker == null) return false;
                serverMuted = speaker.IsMuted(nowMicros);
                return !serverMuted;
            }

            if (channel == null || channel.Phase != ClientPhase.Ready || channel.Link.State == EngineLinkState.Closed) return false;
            foreach (var entry in voiceRoster.Values)
            {
                if (localParticipantId.HasValue && string.Equals(entry.ParticipantId, localParticipantId.Value.Value, StringComparison.Ordinal))
                {
                    serverMuted = entry.ServerMuted;
                }
            }

            return !serverMuted;
        }

        /// <summary>Sends a held frame on its own once it has waited two frames' time; the talker's next one is late or absent.</summary>
        private void FlushVoiceHold()
        {
            if (voiceHeld && nowMicros - heldAtMicros >= 2 * voiceFrameMicros) FlushHeldVoice();
        }

        private void FlushHeldVoice()
        {
            if (!voiceHeld) return;
            voiceHeld = false;
            SendVoice(heldSequence, heldTimestamp, heldFlags, heldVoice.AsSpan(0, heldLength), VoiceFrameFlags.None, ReadOnlySpan<byte>.Empty);
        }

        private void DiscardHeldVoice() => voiceHeld = false;

        private VoiceSubmitResult SendVoice(
            ulong sequence,
            ulong timestamp,
            VoiceFrameFlags firstFlags,
            ReadOnlySpan<byte> first,
            VoiceFrameFlags secondFlags,
            ReadOnlySpan<byte> second)
        {
            if (Role == EngineRole.Client)
            {
                if (channel == null || channel.Link.State == EngineLinkState.Closed) return VoiceSubmitResult.NotConnected;
                var length = VoicePacket.Write(voiceScratch, 0, sequence, timestamp, firstFlags, first, secondFlags, second);
                if (!voicePolicy!.TrySpend(voicePace!, length, nowMicros))
                {
                    voiceDropped[(int)VoiceDropReason.RateLimited]++;
                    return VoiceSubmitResult.RateLimited;
                }

                channel.Link.Send(EngineChannel.Voice, new ArraySegment<byte>(voiceScratch, 0, length));
                voiceSent++;
                return VoiceSubmitResult.Sent;
            }

            var speaker = localParticipantId.HasValue ? FindParticipant(localParticipantId.Value) : null;
            if (speaker?.Voice == null) return VoiceSubmitResult.NotConnected;
            var relayed = VoicePacket.Write(voiceScratch, speaker.Voice.Index, sequence, timestamp, firstFlags, first, secondFlags, second);
            RelayVoice(speaker.Id.Value, new ArraySegment<byte>(voiceScratch, 0, relayed), null);
            return VoiceSubmitResult.Sent;
        }

        /// <summary>Handles a voice datagram from a client: the relay policy, then the relay and the host's own playback.</summary>
        private void HandleUplinkVoice(HostConnection connection, byte[] datagram)
        {
            var policy = voicePolicy;
            if (policy == null) return;
            var record = connection.Participant;
            if (connection.Phase != HostConnectionPhase.Ready || record?.Voice == null)
            {
                voiceDropped[(int)VoiceDropReason.NotReady]++;
                return;
            }

            var admission = policy.Admit(record.Voice, datagram, nowMicros);
            if (admission.Struck) voiceStrikes++;
            switch (admission.Verdict)
            {
                case VoiceVerdict.Relay:
                    voiceRelayed++;
                    var packet = admission.Packet;
                    var length = VoicePacket.Write(
                        voiceScratch,
                        record.Voice.Index,
                        packet.First.Sequence,
                        packet.First.Timestamp,
                        packet.First.Flags,
                        datagram.AsSpan(packet.First.PayloadOffset, packet.First.PayloadLength),
                        packet.Second.Flags,
                        packet.Count == 2 ? datagram.AsSpan(packet.Second.PayloadOffset, packet.Second.PayloadLength) : ReadOnlySpan<byte>.Empty);
                    RelayVoice(record.Id.Value, new ArraySegment<byte>(voiceScratch, 0, length), connection);
                    DeliverLocal(record.Id, packet, datagram);
                    return;
                case VoiceVerdict.Drop:
                    voiceDropped[(int)admission.Reason]++;
                    return;
                case VoiceVerdict.AutoMute:
                    voiceDropped[(int)admission.Reason]++;
                    voiceAutoMutes++;
                    record.Voice.AutoMuteAnnounced = true;
                    MarkVoiceRosterDirty();
                    Report(new EngineDiagnostic(EngineDiagnosticKind.VoiceAbuse, "A participant was muted automatically for abusing voice chat.", record.Id));
                    return;
                default:
                    voiceDropped[(int)admission.Reason]++;
                    voiceAutoMutes++;
                    voiceKicks++;
                    record.Voice.AutoMuteAnnounced = true;
                    MarkVoiceRosterDirty();
                    Report(new EngineDiagnostic(EngineDiagnosticKind.VoiceAbuse, "A participant was removed for abusing voice chat.", record.Id));
                    EndConnection(connection, MultiplayerSessionEndReason.Kicked, VoiceKickMessage);
                    return;
            }
        }

        private void RelayVoice(string speaker, ArraySegment<byte> datagram, HostConnection? sender)
        {
            foreach (var connection in connections)
            {
                if (ReferenceEquals(connection, sender) || connection.Phase != HostConnectionPhase.Ready) continue;
                if (connection.VoiceMuteAll || connection.VoiceMutes.Contains(speaker))
                {
                    voiceDropped[(int)VoiceDropReason.ListenerMuted]++;
                    continue;
                }

                if (connection.Link.State == EngineLinkState.Closed) continue;
                connection.Link.Send(EngineChannel.Voice, datagram);
                voiceSent++;
            }
        }

        /// <summary>Handles a relayed voice datagram on a client: the speaker must be in the roster and the packets valid.</summary>
        private void HandleRelayedVoice(ClientChannel current, byte[] datagram)
        {
            var voice = options.Voice;
            if (voice == null) return;
            if (current.Phase != ClientPhase.Ready)
            {
                voiceDropped[(int)VoiceDropReason.NotReady]++;
                return;
            }

            if (!VoicePacket.TryReadRelayed(datagram, voice.MaximumPacketBytes, out var packet))
            {
                voiceDropped[(int)VoiceDropReason.Malformed]++;
                return;
            }

            if (!voiceRoster.TryGetValue(packet.First.Speaker, out var entry) ||
                (localParticipantId.HasValue && string.Equals(entry.ParticipantId, localParticipantId.Value.Value, StringComparison.Ordinal)))
            {
                voiceDropped[(int)VoiceDropReason.UnknownSpeaker]++;
                return;
            }

            for (var index = 0; index < packet.Count; index++)
            {
                if (!voice.Codec.IsValidPacket(datagram.AsSpan(packet[index].PayloadOffset, packet[index].PayloadLength)))
                {
                    voiceDropped[(int)VoiceDropReason.InvalidPacket]++;
                    return;
                }
            }

            DeliverLocal(new ParticipantId(entry.ParticipantId), packet, datagram);
        }

        private void DeliverLocal(ParticipantId speaker, VoicePacket packet, byte[] datagram)
        {
            var inbox = voiceInbox;
            if (inbox == null || ProcessKind != MultiplayerProcessKind.Interactive || !localParticipantId.HasValue) return;
            if (localVoiceMuteAll || localVoiceMutes.Contains(speaker.Value))
            {
                voiceDropped[(int)VoiceDropReason.ListenerMuted]++;
                return;
            }

            for (var index = 0; index < packet.Count; index++)
            {
                var frame = packet[index];
                var queued = inbox.Enqueue(new VoiceFrame(
                    speaker,
                    frame.Sequence,
                    frame.Timestamp,
                    frame.Flags,
                    new ArraySegment<byte>(datagram, frame.PayloadOffset, frame.PayloadLength),
                    nowMicros));
                voiceDelivered++;
                if (!queued) voiceDropped[(int)VoiceDropReason.InboxOverflow]++;
            }
        }

        /// <summary>Applies a client's listener mute set: the host stops relaying those speakers to it.</summary>
        private static void ApplyVoiceMutes(HostConnection connection, ProtocolReader body)
        {
            VoiceMessages.ReadMutes(body, out var muteAll, out var muted);
            connection.VoiceMuteAll = muteAll;
            connection.VoiceMutes = muted;
        }

        private void ApplyVoiceRoster(ProtocolReader body)
        {
            var entries = VoiceMessages.ReadRoster(body);
            voiceRoster.Clear();
            foreach (var entry in entries) voiceRoster[entry.Speaker] = entry;
        }

        /// <summary>Queues the roster for every joined connection once per flush, and announces expired automatic mutes.</summary>
        private void FlushVoiceRoster()
        {
            if (options.Voice == null) return;
            foreach (var record in participantRecords)
            {
                var speaker = record.Voice;
                if (speaker == null || !speaker.AutoMuteAnnounced || speaker.IsAutoMuted(nowMicros)) continue;
                speaker.AutoMuteAnnounced = false;
                voiceRosterDirty = true;
            }

            if (!voiceRosterDirty) return;
            voiceRosterDirty = false;
            List<VoiceRosterEntry>? entries = null;
            foreach (var connection in connections)
            {
                if (!connection.IsJoined) continue;
                entries ??= BuildVoiceRoster();
                connection.Enqueue(new VoiceRosterItem(entries));
            }
        }

        private List<VoiceRosterEntry> BuildVoiceRoster()
        {
            var entries = new List<VoiceRosterEntry>(participantRecords.Count);
            foreach (var record in participantRecords)
            {
                if (record.Voice != null) entries.Add(new VoiceRosterEntry(record.Voice.Index, record.Id.Value, record.Voice.IsMuted(nowMicros)));
            }

            return entries;
        }

        private void ResetClientVoice()
        {
            DiscardHeldVoice();
            voiceRoster.Clear();
            voiceInbox?.Clear();
        }

        /// <summary>Forgets the voice of a session the process leaves for another role: what it held, heard and expected.</summary>
        private void RetireVoice()
        {
            ResetClientVoice();
            if (options.Voice != null) voicePace = new VoiceSpeaker(1, options.Voice);
            voiceRosterDirty = false;
            voiceSequence = 0;
            lastVoiceTimestamp = 0;
            hasVoiceTimestamp = false;
        }
    }
}
