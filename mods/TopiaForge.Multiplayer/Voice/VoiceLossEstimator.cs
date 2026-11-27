using System;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// Turns the transport's packet counters into the encoder's loss hint. Each window of about five seconds of voice
    /// datagrams yields a loss rate, rounded up to whole percent and capped at 25. A rise applies at once, a fall halves
    /// the gap, so in-band FEC grows quickly when a link gets worse and gives the bits back slowly. Until the first
    /// window completes, the codec's default hint stands.
    /// </summary>
    internal sealed class VoiceLossEstimator
    {
        /// <summary>The datagrams one estimate needs: about five seconds of voice at 50 a second.</summary>
        internal const long WindowPackets = 250;

        /// <summary>The highest hint the estimator sets; beyond it FEC only costs speech quality.</summary>
        internal const int MaximumHintPercent = 25;

        private long baseSent = -1;
        private long baseLost;

        /// <summary>Gets the loss hint for the encoder, in percent.</summary>
        internal int ExpectedLossPercent { get; private set; } = OpusVoiceCodec.DefaultExpectedLossPercent;

        /// <summary>Gets whether any window has completed.</summary>
        internal bool HasMeasurement { get; private set; }

        /// <summary>Takes the transport's running totals of packets sent and lost.</summary>
        internal void Report(long packetsSent, long packetsLost)
        {
            if (packetsSent < 0) throw new ArgumentOutOfRangeException(nameof(packetsSent));
            if (packetsLost < 0) throw new ArgumentOutOfRangeException(nameof(packetsLost));
            if (baseSent < 0 || packetsSent < baseSent || packetsLost < baseLost)
            {
                // The first report, or a new connection whose counters started again.
                baseSent = packetsSent;
                baseLost = packetsLost;
                return;
            }

            var sent = packetsSent - baseSent;
            if (sent < WindowPackets) return;
            var lost = Math.Min(sent, packetsLost - baseLost);
            var measured = (int)Math.Min(MaximumHintPercent, ((lost * 100) + sent - 1) / sent);
            ExpectedLossPercent = HasMeasurement && measured < ExpectedLossPercent ? (ExpectedLossPercent + measured) / 2 : measured;
            HasMeasurement = true;
            baseSent = packetsSent;
            baseLost = packetsLost;
        }
    }
}
