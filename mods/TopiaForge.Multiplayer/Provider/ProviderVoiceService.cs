using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Voice;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// The provider's voice service as the runtime's SPI sees it. Every member forwards to <see cref="VoiceService"/> on the
    /// main thread, the engine's owner, and refuses any other thread. The playback streams it hands out are the service's
    /// own, never wrappers, so each one plays the speaker it was named for and, once it ends, stays ended. Statistics are
    /// mapped by what each counter means. See docs/internal/MultiplayerRuntimeIntegration.md, "Voice".
    /// </summary>
    internal sealed class ProviderVoiceService : IInternalVoiceService
    {
        private readonly SessionEngine engine;
        private readonly VoiceService voice;

        internal ProviderVoiceService(SessionEngine engine, VoiceService voice)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.voice = voice ?? throw new ArgumentNullException(nameof(voice));
        }

        public System.Threading.CancellationToken CaptureSessionToken
        {
            get
            {
                engine.RequireOwnerThread();
                return engine.CurrentSessionToken;
            }
        }

        public bool Enabled
        {
            get
            {
                engine.RequireOwnerThread();
                return voice.Enabled;
            }

            set => voice.Enabled = value;
        }

        public InternalVoiceTransmitMode Mode
        {
            get
            {
                engine.RequireOwnerThread();
                return ToMode(voice.Mode);
            }

            set
            {
                engine.RequireOwnerThread();
                voice.Mode = FromMode(value);
            }
        }

        public float ActivationThresholdDb
        {
            get
            {
                engine.RequireOwnerThread();
                return voice.ActivationThresholdDb;
            }

            set
            {
                engine.RequireOwnerThread();
                voice.ActivationThresholdDb = value;
            }
        }

        public float InputLevelDb
        {
            get
            {
                engine.RequireOwnerThread();
                return voice.InputLevelDb;
            }
        }

        public InternalVoiceTransmitState TransmitState
        {
            get
            {
                engine.RequireOwnerThread();
                return ToState(voice.TransmitState);
            }
        }

        public void SubmitCapture(float[] samples, int count, int sampleRate) => voice.SubmitCapture(samples, count, sampleRate);

        public void SetTransmitting(bool transmitting) => voice.SetTransmitting(transmitting);

        public IVoicePlaybackStream GetPlaybackStream(ParticipantId speaker) => voice.GetPlaybackStream(speaker);

        public bool TryGetPlaybackStream(ParticipantId speaker, out IVoicePlaybackStream? stream)
        {
            var found = voice.TryGetPlaybackStream(speaker, out var playback);
            stream = playback;
            return found;
        }

        public bool IsSpeaking(ParticipantId speaker) => voice.IsSpeaking(speaker);

        public void SetListenerMutes(IReadOnlyCollection<ParticipantId> muted, bool muteAll = false) => voice.SetListenerMutes(muted, muteAll);

        public bool GetListenerMutes(ICollection<ParticipantId> into) => voice.GetListenerMutes(into);

        public InternalVoiceStatistics GetStatistics() => ToStatistics(voice.GetStatistics());

        /// <summary>
        /// Maps the service's statistics onto the SPI's by meaning; docs/internal/MultiplayerRuntimeIntegration.md, "Voice",
        /// tables each field.
        /// </summary>
        internal static InternalVoiceStatistics ToStatistics(VoiceServiceStatistics statistics)
        {
            if (statistics == null) throw new ArgumentNullException(nameof(statistics));
            var speakers = new InternalVoiceSpeakerStatistics[statistics.Speakers.Count];
            for (var index = 0; index < speakers.Length; index++) speakers[index] = ToSpeaker(statistics.Speakers[index]);
            return new InternalVoiceStatistics
            {
                Mode = ToMode(statistics.Mode),
                State = ToState(statistics.State),
                InputLevelDb = statistics.InputLevelDb,
                ExpectedLossPercent = statistics.ExpectedLossPercent,
                SubmittedSamples = statistics.SubmittedSamples,
                FramesEncoded = statistics.FramesEncoded,

                // Taken to send: a frame that left at once, and one held to share the next frame's datagram, which leaves with
                // it or alone soon after. Sent alone would count about half of a steady talker's frames.
                FramesSent = statistics.Sent + statistics.Held,
                FramesReceived = statistics.FramesReceived,

                // Refused: rate limits, a mute by the host, no session to talk in, or one that offers no voice chat. Invalid
                // frames are a fault, not a refusal.
                FramesRefused = statistics.RateLimited + statistics.ServerMuted + statistics.NotConnected + statistics.NotOffered,
                Speakers = speakers
            };
        }

        internal static InternalVoiceTransmitMode ToMode(VoiceTransmitMode mode) =>
            mode == VoiceTransmitMode.VoiceActivation ? InternalVoiceTransmitMode.VoiceActivation : InternalVoiceTransmitMode.PushToTalk;

        internal static VoiceTransmitMode FromMode(InternalVoiceTransmitMode mode) => mode switch
        {
            InternalVoiceTransmitMode.PushToTalk => VoiceTransmitMode.PushToTalk,
            InternalVoiceTransmitMode.VoiceActivation => VoiceTransmitMode.VoiceActivation,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "A transmit mode is push-to-talk or voice activation.")
        };

        internal static InternalVoiceTransmitState ToState(VoiceTransmitState state) => state switch
        {
            VoiceTransmitState.Transmitting => InternalVoiceTransmitState.Transmitting,
            VoiceTransmitState.NoListeners => InternalVoiceTransmitState.NoListeners,
            VoiceTransmitState.MutedByHost => InternalVoiceTransmitState.MutedByHost,
            VoiceTransmitState.NotConnected => InternalVoiceTransmitState.NotConnected,
            VoiceTransmitState.Failed => InternalVoiceTransmitState.Failed,
            VoiceTransmitState.NotOffered => InternalVoiceTransmitState.NotOffered,
            _ => InternalVoiceTransmitState.Idle
        };

        private static InternalVoiceSpeakerStatistics ToSpeaker(VoiceSpeakerStatistics speaker) =>
            new InternalVoiceSpeakerStatistics(speaker.Speaker)
            {
                IsSpeaking = speaker.IsSpeaking,
                Decoded = speaker.Decoded,
                Recovered = speaker.Recovered,

                // Concealment that fades to silence after the third missing frame is concealment too.
                Concealed = speaker.Concealed + speaker.Faded,
                Late = speaker.Late,
                Underruns = speaker.Underruns,
                TargetDelayMicros = speaker.TargetDelayMicros
            };
    }
}
