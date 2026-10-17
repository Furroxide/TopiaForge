using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.Voice;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// Voice chat on the provider's engine. It stays off, with no voice options, no codec and no worker thread, unless
    /// live multiplayer is switched on with <c>TOPIAFORGE_LIVE_MULTIPLAYER=1</c>, the switch that turns on live sessions
    /// (<see cref="DeveloperSessions.IsLiveEnabled"/>), or the live sessions' setup asks for it, as tests do.
    /// </summary>
    internal sealed partial class MultiplayerProvider
    {
        private readonly VoiceService? voice;
        private OpusVoiceCodec? voiceCodec;

        /// <summary>Gets the voice service, or null while live multiplayer is off.</summary>
        internal VoiceService? Voice => voice;

        private VoiceOptions? CreateVoiceOptions(LiveSessionsSetup? live)
        {
            if (!(live?.Voice ?? DeveloperSessions.IsLiveEnabled())) return null;
            voiceCodec = new OpusVoiceCodec();
            return new VoiceOptions(voiceCodec);
        }

        private VoiceService? StartVoice(IEngineDiagnostics? diagnostics, bool workerThread) =>
            voiceCodec == null ? null : new VoiceService(engine, voiceCodec, clockMicros, diagnostics, workerThread);
    }
}
