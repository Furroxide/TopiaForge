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
    /// The host's moderation of one authenticated player for the whole session: a server mute, an automatic mute, and
    /// the strike and mute history behind them. The host keeps it by identity rather than on the participant record,
    /// so it outlives the record's 30-second grace: a player who leaves and rejoins later is still muted, and still on
    /// the same rung of the ladder from strikes to removal.
    /// </summary>
    internal sealed class VoiceModeration
    {
        private readonly long[] strikes;
        private readonly long[] mutes;
        private int strikeCount;
        private int strikeNext;
        private int muteCount;
        private int muteNext;

        internal VoiceModeration(VoiceOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            strikes = new long[options.StrikesBeforeMute];
            mutes = new long[options.MutesBeforeKick];
        }

        /// <summary>Gets or sets whether the host muted this player by hand.</summary>
        internal bool ServerMuted { get; set; }

        /// <summary>Gets or sets when an automatic mute ends, in engine microseconds.</summary>
        internal long AutoMutedUntilMicros { get; set; } = long.MinValue;

        /// <summary>Gets or sets whether the roster announced an automatic mute whose end it has yet to announce.</summary>
        internal bool AutoMuteAnnounced { get; set; }

        /// <summary>
        /// Gets or sets when the player was last seen in the session: admitted, departing, or their record expiring. A full
        /// table forgets the absent player seen longest ago first.
        /// </summary>
        internal long LastSeenMicros { get; set; }

        internal bool IsMuted(long nowMicros) => ServerMuted || nowMicros < AutoMutedUntilMicros;

        internal bool IsAutoMuted(long nowMicros) => nowMicros < AutoMutedUntilMicros;

        /// <summary>Gets whether there is anything to remember: a mute, or strikes or mutes on the record.</summary>
        internal bool HasHistory(long nowMicros) => IsMuted(nowMicros) || strikeCount > 0 || muteCount > 0;

        /// <summary>Records a strike and reports whether enough have fallen within the window to mute.</summary>
        internal bool AddStrike(long nowMicros, long windowMicros) => Record(strikes, ref strikeCount, ref strikeNext, nowMicros, windowMicros);

        /// <summary>Records an automatic mute and reports whether enough have fallen within the window to remove the player.</summary>
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

    /// <summary>A token bucket counted in millionths of a token, so refills stay exact at any rate.</summary>
    internal sealed class VoiceTokenBucket
    {
        private const long MaximumRefillMicros = 10_000_000;
        private readonly long ratePerSecond;
        private long refilledAtMicros = long.MinValue;

        /// <summary>Creates a full bucket that refills at <paramref name="ratePerSecond"/> and holds <paramref name="burstMicros"/> of it.</summary>
        internal VoiceTokenBucket(long ratePerSecond, long burstMicros)
        {
            if (ratePerSecond < 1) throw new ArgumentOutOfRangeException(nameof(ratePerSecond));
            if (burstMicros < 1) throw new ArgumentOutOfRangeException(nameof(burstMicros));
            this.ratePerSecond = ratePerSecond;
            Capacity = ratePerSecond * burstMicros;
            Tokens = Capacity;
        }

        internal long Capacity { get; }

        internal long Tokens { get; private set; }

        /// <summary>Refills for the time since the last call, then takes <paramref name="amount"/> tokens if they are there.</summary>
        internal bool TryTake(long amount, long nowMicros)
        {
            if (!CanTake(amount, nowMicros)) return false;
            Take(amount);
            return true;
        }

        /// <summary>Refills for the time since the last call and says whether <paramref name="amount"/> tokens are there, taking none.</summary>
        internal bool CanTake(long amount, long nowMicros)
        {
            if (refilledAtMicros != long.MinValue)
            {
                var elapsed = Math.Min(MaximumRefillMicros, Math.Max(0, nowMicros - refilledAtMicros));
                Tokens = Math.Min(Capacity, Tokens + (elapsed * ratePerSecond));
            }

            refilledAtMicros = nowMicros;
            return Tokens >= amount * 1_000_000;
        }

        /// <summary>Takes <paramref name="amount"/> tokens that <see cref="CanTake"/> has just found.</summary>
        internal void Take(long amount) => Tokens -= amount * 1_000_000;
    }

    /// <summary>
    /// The host's view of one participant's voice: the index it stamps on relayed frames, the stream so far and the rate
    /// limits, with the player's <see cref="VoiceModeration"/>. A rejoin within the grace period keeps it all but the
    /// stream; a later one gets a new speaker with the same moderation.
    /// </summary>
    internal sealed class VoiceSpeaker
    {
        internal VoiceSpeaker(ulong index, VoiceOptions options, VoiceModeration? moderation = null)
        {
            if (index == 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (options == null) throw new ArgumentNullException(nameof(options));
            Index = index;
            Moderation = moderation ?? new VoiceModeration(options);
            var bytesPerSecond = options.MaximumBitsPerSecond / 8;
            Packets = new VoiceTokenBucket(options.MaximumPacketsPerSecond, VoiceRelayPolicy.BurstMicros);
            Bytes = new VoiceTokenBucket(bytesPerSecond, VoiceRelayPolicy.BurstMicros);
            SustainedPackets = new VoiceTokenBucket(
                (long)options.MaximumPacketsPerSecond * VoiceRelayPolicy.SustainedFactor,
                VoiceRelayPolicy.SustainedWindowMicros);
            SustainedBytes = new VoiceTokenBucket((long)bytesPerSecond * VoiceRelayPolicy.SustainedFactor, VoiceRelayPolicy.SustainedWindowMicros);
        }

        /// <summary>Gets the speaker index relayed frames carry. A host never reuses one, so late frames cannot change owner.</summary>
        internal ulong Index { get; }

        /// <summary>
        /// Gets the generation of the speaker's stream that relayed frames carry. The host gives every new stream one it
        /// has never used for anyone, so a listener tells a restarted stream from the old one whatever its numbers.
        /// </summary>
        internal ulong Stream { get; private set; }

        internal VoiceModeration Moderation { get; }

        internal bool ServerMuted
        {
            get => Moderation.ServerMuted;
            set => Moderation.ServerMuted = value;
        }

        internal long AutoMutedUntilMicros
        {
            get => Moderation.AutoMutedUntilMicros;
            set => Moderation.AutoMutedUntilMicros = value;
        }

        internal bool AutoMuteAnnounced
        {
            get => Moderation.AutoMuteAnnounced;
            set => Moderation.AutoMuteAnnounced = value;
        }

        internal bool HasStream { get; set; }
        internal ulong LastSequence { get; set; }
        internal ulong LastTimestamp { get; set; }

        /// <summary>Gets the datagram rate: one second's worth of burst, refilled at the limit.</summary>
        internal VoiceTokenBucket Packets { get; }

        /// <summary>Gets the bitrate, in bytes: one second's worth of burst, refilled at the limit.</summary>
        internal VoiceTokenBucket Bytes { get; }

        /// <summary>
        /// Gets the sustained datagram rate: twice the limit, five seconds deep. Only a speaker who empties it earns strikes
        /// for breaking the datagram rate.
        /// </summary>
        internal VoiceTokenBucket SustainedPackets { get; }

        /// <summary>
        /// Gets the sustained bitrate, in bytes: twice the limit, five seconds deep. Only a speaker who empties it earns
        /// strikes for breaking the bitrate.
        /// </summary>
        internal VoiceTokenBucket SustainedBytes { get; }

        internal bool IsMuted(long nowMicros) => Moderation.IsMuted(nowMicros);

        internal bool IsAutoMuted(long nowMicros) => Moderation.IsAutoMuted(nowMicros);

        /// <summary>
        /// Starts a new stream, generation <paramref name="stream"/>: a new connection numbers its frames from the start
        /// again, and listeners learn of it from the generation even when its first datagram is lost.
        /// </summary>
        internal void ResetStream(ulong stream)
        {
            HasStream = false;
            LastSequence = 0;
            LastTimestamp = 0;
            Stream = stream;
        }

        internal bool AddStrike(long nowMicros, long windowMicros) => Moderation.AddStrike(nowMicros, windowMicros);

        internal bool AddMute(long nowMicros, long windowMicros) => Moderation.AddMute(nowMicros, windowMicros);

        internal void ClearStrikes() => Moderation.ClearStrikes();
    }

    /// <summary>
    /// The host's rules for voice datagrams from clients: mutes first, then per-speaker datagram-rate and bitrate limits,
    /// the voice format, each frame's packet size and the codec's header check, and the stream order. Each violation of
    /// the format is a strike; enough strikes within a window mute the speaker for a while, and enough mutes within a
    /// longer window remove them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rate limits are token buckets holding one second of burst, judged when the host's pump reads the datagram. A
    /// stall on the host, or on the path, delivers a backlog all at once; the bucket absorbs a second of it and drops the
    /// rest without a strike, and fresh speech passes again as the bucket refills. A datagram over a limit spends none of
    /// either bucket.
    /// </para>
    /// <para>
    /// Breaking a limit is struck only when it is sustained. Each limit has a second bucket that refills at twice the
    /// limit and holds five seconds of that: 300 datagrams at 60 a second, and 50,000 bytes at 10,000 a second, by
    /// default. Every datagram draws on both, relayed or not, and a datagram over a limit strikes only when that limit's
    /// sustained bucket is empty too. So a flood of r datagrams a second is first struck after 300 / (r - 60) seconds, a
    /// flood of b bytes a second after 50,000 / (b - 10,000), and a flood under twice a limit is dropped down to the
    /// limit but never struck. An honest talker's backlog never empties either: a link holds at most 128 datagrams, and
    /// 128 of the largest datagrams the format allows come to under 50,000 bytes.
    /// </para>
    /// </remarks>
    internal sealed class VoiceRelayPolicy
    {
        /// <summary>The burst the rate and bitrate buckets hold: one second.</summary>
        internal const long BurstMicros = 1_000_000;

        /// <summary>How many times its limit a speaker must keep up before datagrams over that limit strike.</summary>
        internal const int SustainedFactor = 2;

        /// <summary>How long the sustained buckets hold at that rate: five seconds.</summary>
        internal const long SustainedWindowMicros = 5_000_000;

        private readonly VoiceOptions options;

        internal VoiceRelayPolicy(VoiceOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        internal VoiceAdmission Admit(VoiceSpeaker speaker, ReadOnlySpan<byte> datagram, long nowMicros)
        {
            if (speaker.IsMuted(nowMicros)) return Dropped(VoiceDropReason.ServerMuted);
            var packetsSustained = speaker.SustainedPackets.TryTake(1, nowMicros);
            var bytesSustained = speaker.SustainedBytes.TryTake(datagram.Length, nowMicros);
            var packetsOver = !speaker.Packets.CanTake(1, nowMicros);
            var bytesOver = !speaker.Bytes.CanTake(datagram.Length, nowMicros);
            if (packetsOver || bytesOver)
            {
                var sustained = (packetsOver && !packetsSustained) || (bytesOver && !bytesSustained);
                return sustained ? Strike(speaker, VoiceDropReason.RateLimited, nowMicros) : Dropped(VoiceDropReason.RateLimited);
            }

            speaker.Packets.Take(1);
            speaker.Bytes.Take(datagram.Length);

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
        internal bool TrySpend(VoiceSpeaker pace, int datagramBytes, long nowMicros)
        {
            // Both are refilled before either is judged, and a datagram that breaks either spends neither, as on the host.
            var packets = pace.Packets.CanTake(1, nowMicros);
            var bytes = pace.Bytes.CanTake(datagramBytes, nowMicros);
            if (!packets || !bytes) return false;
            pace.Packets.Take(1);
            pace.Bytes.Take(datagramBytes);
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
