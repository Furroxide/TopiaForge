using System;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// Turns voice on for an engine and sets the host's limits. Defaults follow the live-multiplayer plan; see
    /// docs/internal/MultiplayerVoice.md, part 2, for why each value is what it is.
    /// </summary>
    internal sealed class VoiceOptions
    {
        internal VoiceOptions(IVoiceCodec codec)
        {
            Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }

        /// <summary>Gets the codec every voice packet must satisfy.</summary>
        internal IVoiceCodec Codec { get; }

        /// <summary>
        /// Gets or sets how many voice datagrams a speaker may send a second, the refill of a token bucket that holds one
        /// second of burst. Two 20 ms frames per datagram make 25 a second, so the default leaves 20% headroom, and the
        /// burst absorbs a second's backlog after a stall. Datagrams over it are dropped. They strike only once a speaker
        /// has sent more than twice this rate for long enough to spend a five-second allowance at twice the rate: 300
        /// datagrams by default, so a flood of r a second that this limit drops is first struck after 300 / (r - 60)
        /// seconds. See docs/internal/MultiplayerVoice.md, section 2.4, for which floods go on to be muted.
        /// </summary>
        internal int MaximumPacketsPerSecond { get; set; } = 30;

        /// <summary>
        /// Gets or sets a speaker's voice bitrate, counting whole datagrams; a token bucket holds one second. Datagrams over
        /// it are dropped, and strike only once a speaker has sent more than twice it for long enough to spend a five-second
        /// allowance at twice the bitrate: 50,000 bytes by default. So a flood of b bytes a second is first struck after
        /// 50,000 / (b - 10,000) seconds, but only when this limit is what drops its datagrams: when the datagram limit
        /// drops enough of them first, this bucket never empties and they strike for neither. Floods only just past twice
        /// the bitrate strike too seldom to be muted; section 2.4 of docs/internal/MultiplayerVoice.md has the measures.
        /// </summary>
        internal int MaximumBitsPerSecond { get; set; } = 40_000;

        /// <summary>Gets or sets the largest codec packet one frame may carry.</summary>
        internal int MaximumPacketBytes { get; set; } = 160;

        /// <summary>Gets or sets how many violations within <see cref="StrikeWindowMicros"/> mute a speaker automatically.</summary>
        internal int StrikesBeforeMute { get; set; } = 20;

        internal long StrikeWindowMicros { get; set; } = 10_000_000;

        /// <summary>Gets or sets how long an automatic mute lasts.</summary>
        internal long AutoMuteMicros { get; set; } = 30_000_000;

        /// <summary>Gets or sets how many automatic mutes within <see cref="MuteWindowMicros"/> remove the speaker.</summary>
        internal int MutesBeforeKick { get; set; } = 3;

        internal long MuteWindowMicros { get; set; } = 600_000_000;

        /// <summary>Gets or sets how many received frames wait for the provider before the oldest is dropped.</summary>
        internal int InboxCapacity { get; set; } = 256;

        /// <summary>
        /// Gets or sets how many players' moderation a host remembers in one session. Beyond it, the absent player seen
        /// longest ago is forgotten first, preferring one with no mute or history; players still present are never forgotten.
        /// </summary>
        internal int ModerationCapacity { get; set; } = 1_024;

        internal void Validate()
        {
            if (MaximumPacketsPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(MaximumPacketsPerSecond));
            if (MaximumBitsPerSecond < 8) throw new ArgumentOutOfRangeException(nameof(MaximumBitsPerSecond));
            if (MaximumPacketBytes < 1 || MaximumPacketBytes > Codec.MaximumPacketBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumPacketBytes), "Voice packets are bounded by the codec's own maximum.");
            }

            if (StrikesBeforeMute < 1) throw new ArgumentOutOfRangeException(nameof(StrikesBeforeMute));
            if (StrikeWindowMicros < 1) throw new ArgumentOutOfRangeException(nameof(StrikeWindowMicros));
            if (AutoMuteMicros < 1) throw new ArgumentOutOfRangeException(nameof(AutoMuteMicros));
            if (MutesBeforeKick < 1) throw new ArgumentOutOfRangeException(nameof(MutesBeforeKick));
            if (MuteWindowMicros < 1) throw new ArgumentOutOfRangeException(nameof(MuteWindowMicros));
            if (InboxCapacity < 1 || InboxCapacity > 65_536) throw new ArgumentOutOfRangeException(nameof(InboxCapacity));
            if (ModerationCapacity < 1) throw new ArgumentOutOfRangeException(nameof(ModerationCapacity));
            if (Codec.FrameSamples < 1 || Codec.SampleRate < 1) throw new ArgumentException("The voice codec reports no frame format.", nameof(Codec));
        }
    }

    /// <summary>Why a voice frame was not relayed or delivered.</summary>
    internal enum VoiceDropReason
    {
        /// <summary>The sender's connection has not been admitted, or the receiver has not finished joining.</summary>
        NotReady = 0,

        /// <summary>The host muted the speaker, by hand or automatically.</summary>
        ServerMuted = 1,

        /// <summary>The listener muted the speaker.</summary>
        ListenerMuted = 2,

        /// <summary>The speaker exceeded the datagram rate or the bitrate; on a client, it dropped the datagram rather than exceed them.</summary>
        RateLimited = 3,

        /// <summary>The codec packet was empty or longer than the limit.</summary>
        Oversized = 4,

        /// <summary>The codec refused the packet's header.</summary>
        InvalidPacket = 5,

        /// <summary>The datagram broke the voice format, or its timestamps did not move forward.</summary>
        Malformed = 6,

        /// <summary>The datagram repeated or preceded one already accepted from the same speaker.</summary>
        Stale = 7,

        /// <summary>A relayed frame named a speaker the receiver's roster does not hold, or the receiver itself.</summary>
        UnknownSpeaker = 8,

        /// <summary>The receive queue was full, so its oldest frame made room.</summary>
        InboxOverflow = 9
    }

    /// <summary>
    /// A snapshot of one engine's voice counters, totals since the engine was created. Drops count datagrams, except that
    /// a listener mute counts once for each listener skipped and an overflow once for each frame.
    /// </summary>
    internal sealed class VoiceStatistics
    {
        private readonly long[] dropped;

        internal VoiceStatistics(long submitted, long sent, long relayed, long delivered, long[] dropped, long strikes, long autoMutes, long kicks)
        {
            Submitted = submitted;
            Sent = sent;
            Relayed = relayed;
            Delivered = delivered;
            this.dropped = dropped;
            Strikes = strikes;
            AutoMutes = autoMutes;
            Kicks = kicks;
        }

        /// <summary>Gets the local talker's frames handed to the engine.</summary>
        internal long Submitted { get; }

        /// <summary>Gets the voice datagrams this engine sent: a client's to its host, a host's to its clients.</summary>
        internal long Sent { get; }

        /// <summary>Gets the datagrams a host accepted from its clients and relayed.</summary>
        internal long Relayed { get; }

        /// <summary>Gets the frames queued for local playback.</summary>
        internal long Delivered { get; }

        internal long Strikes { get; }

        internal long AutoMutes { get; }

        internal long Kicks { get; }

        internal long Dropped(VoiceDropReason reason) => dropped[(int)reason];

        internal long TotalDropped
        {
            get
            {
                long total = 0;
                foreach (var count in dropped) total += count;
                return total;
            }
        }
    }
}
