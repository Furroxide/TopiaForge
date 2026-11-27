using System;
using System.Collections.Generic;
using System.Threading;

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

        /// <summary>The host muted this player, by hand or automatically for abusing voice chat.</summary>
        MutedByHost = 3,

        /// <summary>There is no ready session to talk in.</summary>
        NotConnected = 4,

        /// <summary>Voice stopped after repeated failures and stays off until the provider restarts.</summary>
        Failed = 5,

        /// <summary>
        /// The session offers no voice chat: its host turned "Offer voice chat" off, or the session was shown so before
        /// this player joined. Nothing is coded, sent or played, whether or not voice is on, until the player is in a
        /// session that offers it. This state outranks every other but <see cref="Failed"/>.
        /// </summary>
        NotOffered = 6
    }

    /// <summary>
    /// The provider's voice chat as the runtime drives it. ModManager owns the microphone and audio output: it submits
    /// captured samples and plays each speaker's stream. The provider owns resampling, voice activation, coding, the
    /// jitter buffer and the network. Every member runs on the main thread, which owns the session engine, and refuses any
    /// other with <see cref="InvalidOperationException"/>. The one exception is a playback stream's
    /// <see cref="IVoicePlaybackStream.Read"/>, which belongs to the audio thread. Coding runs on the provider's own voice
    /// thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Voice is off until <see cref="Enabled"/> is set, which the runtime does only once the player has consented: until
    /// then nothing is captured, coded or sent. A session that offers no voice chat carries none whatever the player turned
    /// on, and <see cref="TransmitState"/> reads <see cref="InternalVoiceTransmitState.NotOffered"/> while this game hosts
    /// or has joined one.
    /// </para>
    /// <para>
    /// Listener mutes follow two rules. <b>Names belong to the host that listed them.</b> A participant id names a player
    /// only on the host that gave it, so names stay with that host: through an in-place replacement, and through a
    /// reconnect to a host that kept this player's record. Leaving for solo play forgets them, whether a joined player
    /// leaves or a host stops hosting, and so does a join that finds another host or a host without the record; names given
    /// after such a join, before its host lists its players, name nobody and are dropped. <b>A wish to hear nobody holds
    /// across joins</b> and across hosting again: it names no one, so it follows the player to every host until the player
    /// changes it. Read the set back with <see cref="GetListenerMutes"/> rather than keeping a copy that could disagree.
    /// </para>
    /// <para>
    /// A playback stream plays the one speaker the session listed under its id when it was handed out, and never anybody
    /// else. It lasts while the session lists that speaker, through an in-place replacement and a rejoin within the grace
    /// that keeps their record. It ends, playing silence, when the session stops listing them, when a join lets go of the
    /// list that named them (the same id may name someone else from then on), and when the bound on streams leaves no
    /// room. Ask again with
    /// <see cref="TryGetPlaybackStream"/>, which makes nothing and allocates nothing when there is no stream to give, so it
    /// may run every frame. <see cref="GetPlaybackStream"/> always gives a stream: for a speaker the session does not list
    /// it makes one, a 64 KiB ring and a decoder kept for 2 s, and when the bound leaves no room it makes a new 64 KiB ring
    /// at every call, so calling it every frame for an ended stream costs that much each time.
    /// </para>
    /// </remarks>
    internal interface IInternalVoiceService
    {
        /// <summary>
        /// Gets the engine lifetime that accepts capture. Before reading an open microphone, the runtime compares this
        /// token with the lifetime of its recording and discards unread device audio when it changes. No callback or
        /// codec wait is required; a new recording continues under the player's existing consent and input state.
        /// </summary>
        CancellationToken CaptureSessionToken { get; }

        /// <summary>
        /// Gets or sets whether voice is on for this player. It starts off. While it is off, nothing is captured, coded or
        /// sent, and the meter reads silence; voice from other players still plays. Turning it off stops at once: coded
        /// frames that have not left are dropped, the end of talk included.
        /// </summary>
        bool Enabled { get; set; }

        /// <summary>Gets or sets how the player transmits; an undefined value is refused with <see cref="ArgumentOutOfRangeException"/>.</summary>
        InternalVoiceTransmitMode Mode { get; set; }

        /// <summary>Gets or sets the voice-activation threshold, -96 to 0 dBFS; any other value is refused.</summary>
        float ActivationThresholdDb { get; set; }

        /// <summary>Gets the level of the last captured frame in dBFS, for the settings page's meter; silence while voice is off.</summary>
        float InputLevelDb { get; }

        /// <summary>Gets what the local player's voice is doing, as of the last pump.</summary>
        InternalVoiceTransmitState TransmitState { get; }

        /// <summary>
        /// Takes <paramref name="count"/> captured samples in [-1, 1] at the device's <paramref name="sampleRate"/>, 8 to
        /// 192 kHz. Nothing is kept while voice is off. One call keeps its newest 60 ms, or one and a half of the main
        /// thread's frame intervals when it runs slower than 25 frames a second; more than that arrived at once after a
        /// stall. Capture handed over while not transmitting is only metered: <see cref="SetTransmitting"/> starts and ends
        /// a talk spurt, never the cadence of these calls. Capture taken on an audio callback is handed to the main thread
        /// first.
        /// </summary>
        void SubmitCapture(float[] samples, int count, int sampleRate);

        /// <summary>
        /// Says whether the player transmits: true while the push-to-talk binding is held, or while voice activation
        /// listens; false at the release, which ends the talk spurt at once with its last frame.
        /// </summary>
        void SetTransmitting(bool transmitting);

        /// <summary>
        /// Gets <paramref name="speaker"/>'s playback stream, making it the first time; see the remarks for what a stream
        /// for a speaker the session does not list, or past the bound, costs. Past the bound it returns an ended stream
        /// that plays silence.
        /// </summary>
        IVoicePlaybackStream GetPlaybackStream(ParticipantId speaker);

        /// <summary>
        /// Gets <paramref name="speaker"/>'s playback stream without making one the session has no use for: the stream the
        /// speaker has, or a new one for a speaker the session listed at the last pump while the bound on streams has room.
        /// Otherwise it returns false, and makes and allocates nothing, so a caller whose stream ended may ask again every
        /// frame.
        /// </summary>
        bool TryGetPlaybackStream(ParticipantId speaker, out IVoicePlaybackStream? stream);

        /// <summary>Gets whether <paramref name="speaker"/> was heard in the last 250 ms, for talking indicators.</summary>
        bool IsSpeaking(ParticipantId speaker);

        /// <summary>
        /// Sets whom the local player does not want to hear; the host stops relaying them to this player, and what of
        /// theirs is buffered here goes at once. The names follow the rules in the remarks. It refuses more than 256 names,
        /// the protocol's bound, or an invalid id, with <see cref="ArgumentException"/>.
        /// </summary>
        void SetListenerMutes(IReadOnlyCollection<ParticipantId> muted, bool muteAll = false);

        /// <summary>
        /// Copies whom the local player does not want to hear into <paramref name="into"/>, after clearing it, and returns
        /// whether they want to hear nobody: the set as the rules in the remarks kept or forgot it, which is what a list of
        /// muted players should show.
        /// </summary>
        bool GetListenerMutes(ICollection<ParticipantId> into);

        /// <summary>
        /// Gets the counters, for the settings page and diagnostics. It allocates, so read it a few times a second at most;
        /// the meter that moves every frame is <see cref="InputLevelDb"/>.
        /// </summary>
        InternalVoiceStatistics GetStatistics();
    }

    /// <summary>
    /// One speaker's decoded voice: 48 kHz mono, single producer and single consumer. The audio thread is the consumer.
    /// Any thread may read <see cref="Speaker"/>, <see cref="SampleRate"/> and <see cref="IsEnded"/>.
    /// </summary>
    internal interface IVoicePlaybackStream
    {
        ParticipantId Speaker { get; }

        /// <summary>Gets the rate of every sample: 48,000 Hz.</summary>
        int SampleRate { get; }

        /// <summary>
        /// Gets whether the stream has ended: the session no longer lists its speaker, a join let go of the list that named
        /// them, or the bound on streams left no room. It then plays silence for good, and never anybody else.
        /// </summary>
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

        /// <summary>Gets the capture samples handed in while voice was on, at their own rates.</summary>
        internal long SubmittedSamples { get; set; }

        /// <summary>Gets the frames coded for sending.</summary>
        internal long FramesEncoded { get; set; }

        /// <summary>
        /// Gets the coded frames the session took to send: at once, or held to share a datagram with the next. Each frame
        /// counts once, when the session takes it, so a frame still held when the link goes counts though it never leaves.
        /// </summary>
        internal long FramesSent { get; set; }

        /// <summary>Gets the frames the session delivered for playback.</summary>
        internal long FramesReceived { get; set; }

        /// <summary>
        /// Gets the frames the session refused to send when it was handed them: rate limits, a host mute, no connection, or a
        /// session that offers no voice chat.
        /// </summary>
        internal long FramesRefused { get; set; }

        internal IReadOnlyList<InternalVoiceSpeakerStatistics> Speakers { get; set; } = Array.Empty<InternalVoiceSpeakerStatistics>();
    }

    /// <summary>One speaker's playback counters, totals for the life of the speaker's stream.</summary>
    internal sealed class InternalVoiceSpeakerStatistics
    {
        internal InternalVoiceSpeakerStatistics(ParticipantId speaker)
        {
            Speaker = speaker;
        }

        internal ParticipantId Speaker { get; }
        internal bool IsSpeaking { get; set; }

        /// <summary>Gets the frames decoded from their own packets.</summary>
        internal long Decoded { get; set; }

        /// <summary>Gets the frames rebuilt from forward error correction.</summary>
        internal long Recovered { get; set; }

        /// <summary>Gets the frames concealed because nothing usable arrived in time, those faded to silence included.</summary>
        internal long Concealed { get; set; }

        /// <summary>Gets the frames that arrived after their turn to play, and were dropped.</summary>
        internal long Late { get; set; }

        /// <summary>Gets the reads that found too little audio and played silence for the rest.</summary>
        internal long Underruns { get; set; }

        /// <summary>Gets the jitter buffer's current delay target.</summary>
        internal long TargetDelayMicros { get; set; }
    }
}
