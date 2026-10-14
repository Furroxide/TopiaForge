using System;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>What the host does with one voice frame a client sent.</summary>
    internal enum VoiceVerdict
    {
        /// <summary>Relay it to everyone else.</summary>
        Relay = 0,

        /// <summary>Drop it; <see cref="VoiceAdmission.Reason"/> says why.</summary>
        Drop = 1,

        /// <summary>Drop it: this frame's violation muted the speaker automatically.</summary>
        AutoMute = 2,

        /// <summary>Drop it: the speaker has been muted automatically too often and must leave the session.</summary>
        Kick = 3
    }

    internal readonly struct VoiceAdmission
    {
        internal VoiceAdmission(VoiceVerdict verdict, VoiceDropReason reason, bool struck, VoicePacket packet)
        {
            Verdict = verdict;
            Reason = reason;
            Struck = struck;
            Packet = packet;
        }

        internal VoiceVerdict Verdict { get; }
        internal VoiceDropReason Reason { get; }

        /// <summary>Gets whether the datagram earned its speaker a strike.</summary>
        internal bool Struck { get; }

        /// <summary>Gets the parsed datagram, when the verdict is <see cref="VoiceVerdict.Relay"/>.</summary>
        internal VoicePacket Packet { get; }
    }

    /// <summary>
    /// The host's view of one speaker: the index it stamps on relayed frames, mutes, the stream so far, rate limits and
    /// strike history. It belongs to the participant, not the connection, so reconnecting escapes neither a mute nor
    /// the record of earlier abuse.
    /// </summary>
    internal sealed class VoiceSpeaker
    {
        private readonly long[] strikes;
        private readonly long[] mutes;
        private int strikeCount;
        private int strikeNext;
        private int muteCount;
        private int muteNext;

        internal VoiceSpeaker(ulong index, VoiceOptions options)
        {
            if (index == 0) throw new ArgumentOutOfRangeException(nameof(index));
            Index = index;
            Packets = new SlidingWindowRateLimiter(options.MaximumPacketsPerSecond, 1_000_000);
            ByteCapacity = (long)options.MaximumBitsPerSecond / 8 * 1_000_000;
            ByteTokens = ByteCapacity;
            strikes = new long[options.StrikesBeforeMute];
            mutes = new long[options.MutesBeforeKick];
        }

        /// <summary>Gets the speaker index relayed frames carry. A host never reuses one, so late frames cannot change owner.</summary>
        internal ulong Index { get; }

        /// <summary>Gets or sets whether the host muted this speaker by hand.</summary>
        internal bool ServerMuted { get; set; }

        /// <summary>Gets or sets when an automatic mute ends, in engine microseconds.</summary>
        internal long AutoMutedUntilMicros { get; set; } = long.MinValue;

        /// <summary>Gets or sets whether the roster announced an automatic mute whose end it has yet to announce.</summary>
        internal bool AutoMuteAnnounced { get; set; }

        internal bool HasStream { get; set; }
        internal ulong LastSequence { get; set; }
        internal ulong LastTimestamp { get; set; }
        internal SlidingWindowRateLimiter Packets { get; }

        /// <summary>Gets or sets the byte bucket, in millionths of a byte, so refills stay exact at any rate.</summary>
        internal long ByteTokens { get; set; }

        internal long ByteCapacity { get; }
        internal long ByteRefilledAtMicros { get; set; } = long.MinValue;

        internal bool IsMuted(long nowMicros) => ServerMuted || nowMicros < AutoMutedUntilMicros;

        internal bool IsAutoMuted(long nowMicros) => nowMicros < AutoMutedUntilMicros;

        /// <summary>Forgets the stream so far: a new connection starts its sequences again.</summary>
        internal void ResetStream()
        {
            HasStream = false;
            LastSequence = 0;
            LastTimestamp = 0;
        }

        /// <summary>Records a strike and reports whether enough have fallen within the window to mute.</summary>
        internal bool AddStrike(long nowMicros, long windowMicros) => Record(strikes, ref strikeCount, ref strikeNext, nowMicros, windowMicros);

        /// <summary>Records an automatic mute and reports whether enough have fallen within the window to remove the speaker.</summary>
        internal bool AddMute(long nowMicros, long windowMicros) => Record(mutes, ref muteCount, ref muteNext, nowMicros, windowMicros);

        internal void ClearStrikes()
        {
            strikeCount = 0;
            strikeNext = 0;
        }

        // A ring of the latest times: once it is full, its oldest entry is the Nth most recent event.
        private static bool Record(long[] ring, ref int count, ref int next, long nowMicros, long windowMicros)
        {
            ring[next] = nowMicros;
            next = (next + 1) % ring.Length;
            if (count < ring.Length) count++;
            return count == ring.Length && nowMicros - ring[next % ring.Length] < windowMicros;
        }
    }

    /// <summary>
    /// The host's rules for voice datagrams from clients: mutes first, then per-speaker datagram-rate and bitrate limits,
    /// the voice format, each frame's packet size and the codec's header check, and the stream order. Each violation is a
    /// strike; enough strikes within a window mute the speaker for a while, and enough mutes within a longer window
    /// remove them.
    /// </summary>
    internal sealed class VoiceRelayPolicy
    {
        private const long MaximumRefillMicros = 10_000_000;
        private readonly VoiceOptions options;
        private readonly long bytesPerSecond;

        internal VoiceRelayPolicy(VoiceOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            bytesPerSecond = options.MaximumBitsPerSecond / 8;
        }

        internal VoiceAdmission Admit(VoiceSpeaker speaker, ReadOnlySpan<byte> datagram, long nowMicros)
        {
            if (speaker.IsMuted(nowMicros)) return Dropped(VoiceDropReason.ServerMuted);
            if (!speaker.Packets.TryAcquire(nowMicros) || !TryConsumeBytes(speaker, datagram.Length, nowMicros))
            {
                return Strike(speaker, VoiceDropReason.RateLimited, nowMicros);
            }

            // Frames are read at any size, so an oversized one is told apart from a malformed datagram.
            if (!VoicePacket.TryReadUplink(datagram, int.MaxValue, out var packet)) return Strike(speaker, VoiceDropReason.Malformed, nowMicros);
            for (var index = 0; index < packet.Count; index++)
            {
                var frame = packet[index];
                if (frame.PayloadLength > options.MaximumPacketBytes) return Strike(speaker, VoiceDropReason.Oversized, nowMicros);
                if (!options.Codec.IsValidPacket(datagram.Slice(frame.PayloadOffset, frame.PayloadLength)))
                {
                    return Strike(speaker, VoiceDropReason.InvalidPacket, nowMicros);
                }
            }

            var last = packet[packet.Count - 1];
            if (speaker.HasStream && packet.First.Sequence <= speaker.LastSequence) return Dropped(VoiceDropReason.Stale);
            if (speaker.HasStream && packet.First.Timestamp <= speaker.LastTimestamp) return Strike(speaker, VoiceDropReason.Malformed, nowMicros);
            speaker.HasStream = true;
            speaker.LastSequence = last.Sequence;
            speaker.LastTimestamp = last.Timestamp;
            return new VoiceAdmission(VoiceVerdict.Relay, default, false, packet);
        }

        /// <summary>
        /// Spends one datagram's share of the datagram rate and the bitrate for a client pacing its own talker, exactly as
        /// the host will judge it on arrival. False means the datagram would break a limit and must not be sent.
        /// </summary>
        internal bool TrySpend(VoiceSpeaker pace, int datagramBytes, long nowMicros) =>
            pace.Packets.TryAcquire(nowMicros) && TryConsumeBytes(pace, datagramBytes, nowMicros);

        private bool TryConsumeBytes(VoiceSpeaker speaker, int frameBytes, long nowMicros)
        {
            if (speaker.ByteRefilledAtMicros != long.MinValue)
            {
                var elapsed = Math.Min(MaximumRefillMicros, Math.Max(0, nowMicros - speaker.ByteRefilledAtMicros));
                speaker.ByteTokens = Math.Min(speaker.ByteCapacity, speaker.ByteTokens + (elapsed * bytesPerSecond));
            }

            speaker.ByteRefilledAtMicros = nowMicros;
            var cost = (long)frameBytes * 1_000_000;
            if (speaker.ByteTokens < cost) return false;
            speaker.ByteTokens -= cost;
            return true;
        }

        private VoiceAdmission Strike(VoiceSpeaker speaker, VoiceDropReason reason, long nowMicros)
        {
            if (!speaker.AddStrike(nowMicros, options.StrikeWindowMicros)) return new VoiceAdmission(VoiceVerdict.Drop, reason, true, default);
            speaker.ClearStrikes();
            speaker.AutoMutedUntilMicros = nowMicros + options.AutoMuteMicros;
            var verdict = speaker.AddMute(nowMicros, options.MuteWindowMicros) ? VoiceVerdict.Kick : VoiceVerdict.AutoMute;
            return new VoiceAdmission(verdict, reason, true, default);
        }

        private static VoiceAdmission Dropped(VoiceDropReason reason) => new VoiceAdmission(VoiceVerdict.Drop, reason, false, default);
    }
}
