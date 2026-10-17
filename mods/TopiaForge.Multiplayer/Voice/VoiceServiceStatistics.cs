using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>What the local talker's voice is doing, for the transmit indicator.</summary>
    internal enum VoiceTransmitState
    {
        /// <summary>Nothing is being sent.</summary>
        Idle = 0,

        /// <summary>Frames are leaving.</summary>
        Transmitting = 1,

        /// <summary>The session has nobody else connected to hear the talker.</summary>
        NoListeners = 2,

        /// <summary>The host muted the local player.</summary>
        MutedByHost = 3,

        /// <summary>There is no ready session to talk in.</summary>
        NotConnected = 4,

        /// <summary>The voice worker stopped after repeated failures; voice is off until the provider restarts.</summary>
        Failed = 5
    }

    /// <summary>
    /// One speaker's playback counters, which the voice worker publishes every slot and any thread may read. They are
    /// totals for the stream's lifetime.
    /// </summary>
    internal sealed class VoiceSpeakerCounters
    {
        private long decoded;
        private long recovered;
        private long concealed;
        private long faded;
        private long late;
        private long restarts;
        private long talkSpurts;
        private long resets;
        private long skips;
        private long extras;
        private long topUps;
        private long trimmed;
        private long targetDelayMicros;

        internal long Decoded => Volatile.Read(ref decoded);
        internal long Recovered => Volatile.Read(ref recovered);
        internal long Concealed => Volatile.Read(ref concealed);
        internal long Faded => Volatile.Read(ref faded);
        internal long Late => Volatile.Read(ref late);
        internal long Restarts => Volatile.Read(ref restarts);
        internal long TalkSpurts => Volatile.Read(ref talkSpurts);
        internal long Resets => Volatile.Read(ref resets);
        internal long Skips => Volatile.Read(ref skips);
        internal long Extras => Volatile.Read(ref extras);
        internal long TopUps => Volatile.Read(ref topUps);

        /// <summary>Gets the frames dropped from a stall's backlog so playout keeps its target delay.</summary>
        internal long Trimmed => Volatile.Read(ref trimmed);

        internal long TargetDelayMicros => Volatile.Read(ref targetDelayMicros);

        /// <summary>Copies the channel's counters. The voice worker only.</summary>
        internal void Publish(VoiceSpeakerChannel channel)
        {
            var statistics = channel.Buffer.Statistics;
            Volatile.Write(ref decoded, statistics.Decoded);
            Volatile.Write(ref recovered, statistics.Recovered);
            Volatile.Write(ref concealed, statistics.Concealed);
            Volatile.Write(ref faded, statistics.Faded);
            Volatile.Write(ref late, statistics.Late);
            Volatile.Write(ref restarts, statistics.Restarts);
            Volatile.Write(ref talkSpurts, statistics.TalkSpurts);
            Volatile.Write(ref resets, channel.Resets);
            Volatile.Write(ref skips, channel.Skips);
            Volatile.Write(ref extras, channel.Extras);
            Volatile.Write(ref topUps, channel.TopUps);
            Volatile.Write(ref trimmed, channel.Trimmed);
            Volatile.Write(ref targetDelayMicros, channel.Buffer.TargetDelayMicros);
        }
    }

    /// <summary>One speaker as the settings page and diagnostics see them. It names a participant, never an address.</summary>
    internal sealed class VoiceSpeakerStatistics
    {
        internal VoiceSpeakerStatistics(VoicePlaybackStream stream, bool isSpeaking)
        {
            var counters = stream.Counters;
            Speaker = stream.Speaker;
            IsSpeaking = isSpeaking;
            Decoded = counters.Decoded;
            Recovered = counters.Recovered;
            Concealed = counters.Concealed;
            Faded = counters.Faded;
            Late = counters.Late;
            Restarts = counters.Restarts;
            TalkSpurts = counters.TalkSpurts;
            Resets = counters.Resets;
            Skips = counters.Skips;
            Extras = counters.Extras;
            TopUps = counters.TopUps;
            Trimmed = counters.Trimmed;
            TargetDelayMicros = counters.TargetDelayMicros;
            Underruns = stream.Underruns;
            UnderrunSamples = stream.UnderrunSamples;
            SkippedSamples = stream.SkippedSamples;
            OverflowSamples = stream.OverflowSamples;
        }

        internal ParticipantId Speaker { get; }
        internal bool IsSpeaking { get; }
        internal long Decoded { get; }
        internal long Recovered { get; }
        internal long Concealed { get; }
        internal long Faded { get; }
        internal long Late { get; }
        internal long Restarts { get; }
        internal long TalkSpurts { get; }
        internal long Resets { get; }
        internal long Skips { get; }
        internal long Extras { get; }
        internal long TopUps { get; }
        internal long Trimmed { get; }
        internal long TargetDelayMicros { get; }
        internal long Underruns { get; }
        internal long UnderrunSamples { get; }
        internal long SkippedSamples { get; }
        internal long OverflowSamples { get; }
    }

    /// <summary>
    /// A snapshot of the voice service for the settings page and diagnostics. Counters are totals since the service
    /// started; the worker's are read while it runs, so they may trail by a step. Nothing in it names an address.
    /// </summary>
    internal sealed class VoiceServiceStatistics
    {
        /// <summary>Gets whether voice was on: until it is, nothing is captured, coded or sent.</summary>
        internal bool Enabled { get; set; }

        internal VoiceTransmitMode Mode { get; set; }
        internal VoiceTransmitState State { get; set; }
        internal float InputLevelDb { get; set; }
        internal int ExpectedLossPercent { get; set; }

        /// <summary>Gets the capture samples handed in, at their own rates.</summary>
        internal long SubmittedSamples { get; set; }

        /// <summary>
        /// Gets the capture samples dropped as stale: what arrived at once after a stall, beyond what one call keeps at the
        /// main thread's frame rate.
        /// </summary>
        internal long StaleSamplesDropped { get; set; }

        /// <summary>Gets the capture samples dropped because the worker fell a second behind.</summary>
        internal long CaptureOverflowSamples { get; set; }

        internal long FramesCaptured { get; set; }
        internal long FramesEncoded { get; set; }
        internal long DiscontinuousSuppressed { get; set; }
        internal long EndsOfTalk { get; set; }

        /// <summary>Gets the encoded frames dropped because the main thread reached them too late to send.</summary>
        internal long StaleFramesDropped { get; set; }

        /// <summary>Gets the encoded frames dropped because voice was turned off before they left.</summary>
        internal long FramesDroppedOff { get; set; }

        /// <summary>Gets the frames the session sent at once: alone, or with the frame it held before them.</summary>
        internal long Sent { get; set; }

        /// <summary>Gets the frames the session held, to leave paired with the next one or alone soon after.</summary>
        internal long Held { get; set; }

        internal long RateLimited { get; set; }
        internal long ServerMuted { get; set; }
        internal long NotConnected { get; set; }
        internal long Invalid { get; set; }

        /// <summary>Gets the frames the session took to send: <see cref="Sent"/> and <see cref="Held"/>.</summary>
        internal long FramesSent => Sent + Held;

        /// <summary>Gets the frames the session refused to send: rate limits, a host mute, or no connection.</summary>
        internal long FramesRefused => RateLimited + ServerMuted + NotConnected;

        internal long FramesReceived { get; set; }
        internal long InboundOverflow { get; set; }
        internal long FramesUnrouted { get; set; }

        /// <summary>
        /// Gets the frames dropped because their speaker had no stream and the bound on streams left no room for one: a
        /// speaker the session's participants do not list.
        /// </summary>
        internal long UnlistedFramesDropped { get; set; }

        /// <summary>
        /// Gets the streams ended because the participants still did not list their speaker after the grace, or because a
        /// speaker they list needed the room.
        /// </summary>
        internal long UnlistedStreamsEnded { get; set; }

        internal long WorkerSteps { get; set; }
        internal long WorkerFaults { get; set; }
        internal long SlotsRun { get; set; }
        internal long SlotsDropped { get; set; }

        internal VoiceStatistics? Engine { get; set; }

        internal IReadOnlyList<VoiceSpeakerStatistics> Speakers { get; set; } = new List<VoiceSpeakerStatistics>();
    }
}
