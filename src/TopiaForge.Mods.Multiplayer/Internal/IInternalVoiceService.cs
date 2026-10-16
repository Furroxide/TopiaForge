using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Internal
{
    /// <summary>How the local player's voice is sent.</summary>
    internal enum InternalVoiceTransmitMode
    {
        /// <summary>Only while the push-to-talk binding is held.</summary>
        PushToTalk = 0,

        /// <summary>Whenever the microphone level passes the activation threshold.</summary>
        VoiceActivation = 1
    }

    /// <summary>What the local player's voice is doing, for the transmit indicator.</summary>
    internal enum InternalVoiceTransmitState
    {
        Idle = 0,
        Transmitting = 1,

        /// <summary>Nobody else is connected to hear it.</summary>
        NoListeners = 2,

        /// <summary>The host muted this player.</summary>
        MutedByHost = 3,

        /// <summary>There is no ready session to talk in.</summary>
        NotConnected = 4,

        /// <summary>Voice stopped after repeated failures and stays off until the provider restarts.</summary>
        Failed = 5
    }

    /// <summary>
    /// The provider's voice chat as the runtime drives it. ModManager owns the microphone and audio output: it submits
    /// captured samples and plays each speaker's stream. The provider owns resampling, voice activation, coding, the
    /// jitter buffer and the network. Every member runs on the main thread, except a playback stream's
    /// <see cref="IVoicePlaybackStream.Read"/>.
    /// </summary>
    internal interface IInternalVoiceService
    {
        InternalVoiceTransmitMode Mode { get; set; }

        /// <summary>Gets or sets the voice-activation threshold, -96 to 0 dBFS.</summary>
        float ActivationThresholdDb { get; set; }

        /// <summary>Gets the level of the last captured frame in dBFS, for the settings page's meter.</summary>
        float InputLevelDb { get; }

        /// <summary>Gets what the local player's voice is doing, as of the last pump.</summary>
        InternalVoiceTransmitState TransmitState { get; }

        /// <summary>
        /// Takes <paramref name="count"/> captured samples in [-1, 1] at the device's <paramref name="sampleRate"/>. Only
        /// the newest 60 ms of one call is kept. In push-to-talk, a call of zero samples keeps the spurt open while the
        /// binding is held and the device delivered nothing new.
        /// </summary>
        void SubmitCapture(float[] samples, int count, int sampleRate);

        /// <summary>Gets <paramref name="speaker"/>'s playback stream, creating it the first time.</summary>
        IVoicePlaybackStream GetPlaybackStream(ParticipantId speaker);

        /// <summary>Gets whether <paramref name="speaker"/> was heard in the last 250 ms, for talking indicators.</summary>
        bool IsSpeaking(ParticipantId speaker);

        /// <summary>Sets whom the local player does not want to hear; the host stops relaying them to this player.</summary>
        void SetListenerMutes(IReadOnlyCollection<ParticipantId> muted, bool muteAll = false);

        InternalVoiceStatistics GetStatistics();
    }

    /// <summary>
    /// One speaker's decoded voice: 48 kHz mono, single producer and single consumer. The audio thread is the consumer.
    /// </summary>
    internal interface IVoicePlaybackStream
    {
        ParticipantId Speaker { get; }

        /// <summary>Gets the rate of every sample: 48,000 Hz.</summary>
        int SampleRate { get; }

        /// <summary>Gets whether the speaker left; the stream then plays silence.</summary>
        bool IsEnded { get; }

        /// <summary>
        /// Fills <paramref name="count"/> samples of <paramref name="buffer"/> from <paramref name="offset"/> with the
        /// speaker's voice in [-1, 1], and silence where none is buffered. Wait-free: it takes no lock, never blocks and
        /// allocates nothing, so the audio thread may call it. One thread only. It returns <paramref name="count"/>.
        /// </summary>
        int Read(float[] buffer, int offset, int count);
    }

    /// <summary>Voice counters for the settings page and diagnostics. They name participants, never addresses.</summary>
    internal sealed class InternalVoiceStatistics
    {
        internal InternalVoiceTransmitMode Mode { get; set; }
        internal InternalVoiceTransmitState State { get; set; }
        internal float InputLevelDb { get; set; }

        /// <summary>Gets the loss the encoder plans for, in percent, from the transport's counters.</summary>
        internal int ExpectedLossPercent { get; set; }

        internal long SubmittedSamples { get; set; }
        internal long FramesEncoded { get; set; }
        internal long FramesSent { get; set; }
        internal long FramesReceived { get; set; }

        /// <summary>Gets the frames the session refused to send: rate limits, a host mute, or no connection.</summary>
        internal long FramesRefused { get; set; }

        internal IReadOnlyList<InternalVoiceSpeakerStatistics> Speakers { get; set; } = Array.Empty<InternalVoiceSpeakerStatistics>();
    }

    /// <summary>One speaker's playback counters.</summary>
    internal sealed class InternalVoiceSpeakerStatistics
    {
        internal InternalVoiceSpeakerStatistics(ParticipantId speaker)
        {
            Speaker = speaker;
        }

        internal ParticipantId Speaker { get; }
        internal bool IsSpeaking { get; set; }
        internal long Decoded { get; set; }

        /// <summary>Gets the frames rebuilt from forward error correction.</summary>
        internal long Recovered { get; set; }

        /// <summary>Gets the frames concealed because nothing arrived in time.</summary>
        internal long Concealed { get; set; }

        internal long Late { get; set; }
        internal long Underruns { get; set; }

        /// <summary>Gets the jitter buffer's current delay target.</summary>
        internal long TargetDelayMicros { get; set; }
    }
}
