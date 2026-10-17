using System;
using System.Threading;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>How the local talker decides what to send while transmitting.</summary>
    internal enum VoiceTransmitMode
    {
        /// <summary>
        /// Everything captured while transmitting is sent: the caller transmits while the push-to-talk key is held, and
        /// stopping ends the talk spurt.
        /// </summary>
        PushToTalk = 0,

        /// <summary>While transmitting, frames are sent while speech clears the threshold, and for 300 ms after.</summary>
        VoiceActivation = 1
    }

    /// <summary>
    /// The local talker's path on the voice worker: capture becomes frames (<see cref="VoiceCaptureFramer"/>), the
    /// transmit mode decides which are sent, the encoder codes them, and the coded frames go to the main thread with
    /// their flags. One thread owns it.
    /// </summary>
    /// <remarks>
    /// Each capture says whether the talker transmitted while it was made. Frames of capture that was not transmitted are
    /// metered and nothing more. A talk spurt's last frame carries <see cref="VoiceFrameFlags.EndOfTalk"/>: the end of the
    /// voice-activation hangover, or the frame being filled when transmission stopped, padded with silence. Under
    /// discontinuous transmission the encoder codes silence as one-byte packets; the first of a run is sent, marked
    /// <see cref="VoiceFrameFlags.Discontinuous"/>, so listeners play silence rather than conceal, and the rest of the
    /// run is not sent at all.
    /// </remarks>
    internal sealed class VoiceTransmitter : IVoiceFrameSink
    {
        private readonly VoiceCaptureFramer framer = new VoiceCaptureFramer();
        private readonly VoiceActivityDetector detector = new VoiceActivityDetector();
        private readonly OpusVoiceEncoder encoder;
        private readonly VoiceExchange<VoiceOutboundBatch> outbound;
        private readonly byte[] packet = new byte[OpusVoiceCodec.MaximumPacketLength];
        private VoiceTransmitMode mode;
        private bool transmitting;
        private bool spurtOpen;
        private bool inDiscontinuousRun;
        private int levelBits = BitConverter.SingleToInt32Bits(VoiceActivityDetector.SilenceDb);

        internal VoiceTransmitter(OpusVoiceCodec codec, VoiceExchange<VoiceOutboundBatch> outbound)
        {
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            this.outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
            encoder = codec.CreateEncoder();
        }

        /// <summary>Gets or sets whether coding is pointless for now: the host muted the talker, or nobody can hear.</summary>
        internal bool Blocked { get; set; }

        internal VoiceCaptureFramer Framer => framer;

        internal OpusVoiceEncoder Encoder => encoder;

        /// <summary>Gets the level of the last captured frame in dBFS; the main thread may read it at any time.</summary>
        internal float InputLevelDb => BitConverter.Int32BitsToSingle(Volatile.Read(ref levelBits));

        /// <summary>Gets whether a talk spurt is open: something was sent since the last end of talk.</summary>
        internal bool IsTalking => spurtOpen;

        internal long FramesCaptured { get; private set; }
        internal long FramesEncoded { get; private set; }
        internal long FramesQueued { get; private set; }
        internal long DiscontinuousSuppressed { get; private set; }
        internal long EndsOfTalk { get; private set; }
        internal long OutboundOverflow { get; private set; }

        /// <summary>Applies the main thread's settings; a change of mode ends an open talk spurt first.</summary>
        internal void Configure(VoiceTransmitMode newMode, float thresholdDb, int expectedLossPercent)
        {
            if (newMode != mode)
            {
                if (spurtOpen) framer.EndCapture(this);
                detector.Reset();
                mode = newMode;
            }

            if (detector.ThresholdDb != thresholdDb) detector.ThresholdDb = thresholdDb;
            if (encoder.ExpectedLossPercent != expectedLossPercent) encoder.ExpectedLossPercent = expectedLossPercent;
        }

        /// <summary>Takes one capture, as the main thread queued it, and whether the talker transmitted it.</summary>
        internal void Feed(Span<float> samples, int sampleRate, long endMicros, bool transmit)
        {
            transmitting = transmit;
            framer.Feed(samples, sampleRate, endMicros, this);
        }

        /// <summary>
        /// Ends transmission at <paramref name="endMicros"/>: an open talk spurt gets its last frame, padded with silence.
        /// </summary>
        internal void EndCapture(long endMicros)
        {
            if (spurtOpen) framer.EndCapture(this, endMicros);
            detector.Reset();
        }

        void IVoiceFrameSink.OnFrame(ReadOnlySpan<short> frame, ulong timestamp, long capturedMicros, bool endOfCapture)
        {
            FramesCaptured++;
            var flags = VoiceFrameFlags.None;
            if (endOfCapture)
            {
                detector.Reset();
                if (!spurtOpen) return;
                flags = VoiceFrameFlags.EndOfTalk;
            }
            else if (!transmitting)
            {
                // Captured while not transmitting: metered for the settings page, and nothing more.
                detector.Measure(frame);
                PublishLevel();
                return;
            }
            else if (mode == VoiceTransmitMode.VoiceActivation)
            {
                var gate = detector.Classify(frame);
                PublishLevel();
                if (gate == VoiceGate.Silent) return;
                if (gate == VoiceGate.EndOfTalk) flags = VoiceFrameFlags.EndOfTalk;
            }
            else
            {
                detector.Measure(frame);
                PublishLevel();
            }

            if (Blocked)
            {
                spurtOpen = (flags & VoiceFrameFlags.EndOfTalk) == 0;
                return;
            }

            var length = encoder.Encode(frame, packet);
            FramesEncoded++;
            if (OpusToc.IsDiscontinuous(packet.AsSpan(0, length)))
            {
                if (inDiscontinuousRun && (flags & VoiceFrameFlags.EndOfTalk) == 0)
                {
                    DiscontinuousSuppressed++;
                    return;
                }

                inDiscontinuousRun = true;
                flags |= VoiceFrameFlags.Discontinuous;
            }
            else
            {
                inDiscontinuousRun = false;
            }

            if ((flags & VoiceFrameFlags.EndOfTalk) != 0)
            {
                spurtOpen = false;
                inDiscontinuousRun = false;
                EndsOfTalk++;
            }
            else
            {
                spurtOpen = true;
            }

            bool queued;
            lock (outbound.Gate) queued = outbound.Writable.TryAdd(timestamp, flags, packet.AsSpan(0, length), capturedMicros);
            if (queued) FramesQueued++;
            else OutboundOverflow++;
        }

        private void PublishLevel() => Volatile.Write(ref levelBits, BitConverter.SingleToInt32Bits(detector.LastLevelDb));
    }
}
