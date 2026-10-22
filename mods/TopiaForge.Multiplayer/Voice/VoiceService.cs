using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// Voice chat for one provider: the local talker's capture in, each remote speaker's playback stream out, and the
    /// session engine's voice path between them. Every member belongs to the main thread, the engine's owner. Capture,
    /// transmission, the switch, mutes, playback streams, the talking indicator and the statistics check it and refuse any
    /// other thread, so a caller hands capture over on that thread. The one exception is
    /// <see cref="VoicePlaybackStream.Read"/>, which belongs to the audio thread. Coding happens on the
    /// <see cref="VoiceWorker"/>. Mutes, a full rate and a muted talker are expected states, counted rather than reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Voice stays off until the player turns it on: until <see cref="Enabled"/> is set, nothing is captured, coded or
    /// sent. In a session that offers no voice chat nothing is coded, sent or played whatever the player turned on, and
    /// <see cref="TransmitState"/> says so (<see cref="SessionEngine.IsVoiceOffered"/>). While it is on,
    /// <see cref="SetTransmitting"/> says when the player transmits, while push-to-talk is held or
    /// while voice activation listens, and turning it off ends the talk spurt. Capture calls may come at any cadence. How
    /// much of one call is kept, and how long an encoded frame may wait to be sent, follow the main thread's frame
    /// interval, so a slow frame rate loses nothing while a stall's backlog is still dropped.
    /// </para>
    /// <para>
    /// <see cref="Pump"/> runs once per game frame after the engine's own pump. It sends the frames the worker encoded,
    /// drops any that waited too long, and hands received frames to the worker with their arrival times and stream
    /// generations. A talker who rejoins or restarts arrives with a new generation, which starts their jitter buffer
    /// again. A session that changes, or this player being Ready again after a lost or stalled link, drops every
    /// speaker's jitter buffer. A join that withholds the participant list ends every speaker's stream, since its ids may
    /// name other people from then on. Speakers are bounded: see <see cref="GetPlaybackStream"/>.
    /// </para>
    /// <para>
    /// The engine owns the listener mutes and keeps or forgets them at every join (docs/internal/MultiplayerVoice.md,
    /// section 2.5), so the service keeps no copy of them; see <see cref="SetListenerMutes"/>.
    /// </para>
    /// </remarks>
    internal sealed partial class VoiceService : IDisposable
    {
        /// <summary>The least of one capture call kept: its newest 60 ms. More arrives at once only after a stall.</summary>
        internal const long MinimumCaptureBacklogMicros = 60_000;

        /// <summary>The least time an encoded frame may wait for the main thread before it is too late to send: 120 ms.</summary>
        internal const long MinimumSendDelayMicros = 120_000;

        /// <summary>
        /// The longest interval between capture calls, or between pumps, taken for the frame rate: 250 ms, four frames a
        /// second. A longer one is a stall or a pause, and leaves the measured interval as it was.
        /// </summary>
        internal const long MaximumFrameIntervalMicros = 250_000;

        /// <summary>How recently a speaker must have been heard to count as speaking, for talking indicators.</summary>
        internal const long SpeakingMicros = 250_000;

        private const long TransmittingMicros = 100_000;

        private readonly SessionEngine engine;
        private readonly OpusVoiceCodec codec;
        private readonly Func<long> clock;
        private readonly IEngineDiagnostics? diagnostics;
        private readonly VoiceExchange<VoiceInboundBatch> inbound;
        private readonly VoiceExchange<VoiceOutboundBatch> outbound;
        private readonly VoiceWorker worker;
        private readonly VoiceLossEstimator loss = new VoiceLossEstimator();
        private VoiceTransmitMode mode = VoiceTransmitMode.PushToTalk;
        private float thresholdDb = VoiceActivityDetector.DefaultThresholdDb;
        private bool enabled;
        private bool transmitting;
        private long lastSubmitMicros = long.MinValue;
        private long submitIntervalMicros;
        private long lastPumpMicros = long.MinValue;
        private long pumpIntervalMicros;
        private long lastSentMicros = long.MinValue;
        private bool ready;
        private bool mutedByHost;
        private bool failureReported;
        private bool disposed;
        private long submittedSamples;
        private long staleSamplesDropped;
        private long captureOverflowSamples;
        private long staleFramesDropped;
        private long framesDroppedOff;
        private long sent;
        private long held;
        private long rateLimited;
        private long serverMuted;
        private long notConnected;
        private long notOffered;
        private long invalid;

        /// <summary>Creates the service for <paramref name="engine"/>, whose voice options name <paramref name="codec"/>.</summary>
        /// <param name="engine">The provider's engine, with voice on.</param>
        /// <param name="codec">The codec in the engine's voice options.</param>
        /// <param name="clock">The engine's clock, in microseconds; the worker reads it too.</param>
        /// <param name="diagnostics">Where a failed worker is reported once.</param>
        /// <param name="startWorker">False leaves the worker to the caller's <see cref="VoiceWorker.Step"/>, as tests do.</param>
        internal VoiceService(SessionEngine engine, OpusVoiceCodec codec, Func<long> clock, IEngineDiagnostics? diagnostics = null, bool startWorker = true)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (!engine.IsVoiceEnabled) throw new ArgumentException("The engine has no voice options.", nameof(engine));
            this.diagnostics = diagnostics;
            streamLimit = Math.Max(2, engine.Options.MaximumParticipants);
            inbound = new VoiceExchange<VoiceInboundBatch>(new VoiceInboundBatch(), new VoiceInboundBatch());
            outbound = new VoiceExchange<VoiceOutboundBatch>(new VoiceOutboundBatch(), new VoiceOutboundBatch());
            worker = new VoiceWorker(codec, inbound, outbound, clock);
            worker.Configure(mode, thresholdDb, loss.ExpectedLossPercent, transmitBlocked: true);
            if (startWorker) worker.Start();
        }

        internal VoiceWorker Worker => worker;

        /// <summary>Gets what the main thread hands the worker, for tests that check what waits there.</summary>
        internal VoiceExchange<VoiceInboundBatch> Inbound => inbound;

        internal OpusVoiceCodec Codec => codec;

        /// <summary>
        /// Gets or sets whether voice is on for this player. It starts off. While it is off, nothing is captured, coded or
        /// sent, and the meter reads silence; voice from other players still plays. Turning it off stops at once: the
        /// worker codes nothing more, and frames it coded that have not left yet are dropped, end of talk included.
        /// </summary>
        internal bool Enabled
        {
            get => enabled;
            set
            {
                engine.RequireOwnerThread();
                if (disposed || enabled == value) return;
                if (!value)
                {
                    worker.Configure(mode, thresholdDb, loss.ExpectedLossPercent, transmitBlocked: true);
                    EndTransmission();
                }

                enabled = value;
            }
        }

        /// <summary>Gets or sets how the talker transmits; the worker applies a change at the next pump.</summary>
        internal VoiceTransmitMode Mode
        {
            get => mode;
            set => mode = value == VoiceTransmitMode.VoiceActivation ? value : VoiceTransmitMode.PushToTalk;
        }

        /// <summary>Gets or sets the voice-activation threshold, -96 to 0 dBFS.</summary>
        internal float ActivationThresholdDb
        {
            get => thresholdDb;
            set => thresholdDb = VoiceActivityDetector.RequireThreshold(value);
        }

        /// <summary>Gets the level of the last captured frame in dBFS, for the settings page's meter.</summary>
        internal float InputLevelDb => enabled ? worker.Transmitter.InputLevelDb : VoiceActivityDetector.SilenceDb;

        /// <summary>Gets the encoder's current loss hint, in percent.</summary>
        internal int ExpectedLossPercent => loss.ExpectedLossPercent;

        /// <summary>Gets what the talker's voice is doing, as of the last pump.</summary>
        internal VoiceTransmitState TransmitState { get; private set; } = VoiceTransmitState.Idle;

        /// <summary>
        /// Takes <paramref name="count"/> captured samples, in [-1, 1], at the device's <paramref name="sampleRate"/>, on the
        /// main thread. Nothing is kept while voice is off. A call keeps its newest 60 ms, or one and a half frame
        /// intervals of the main thread when it runs slower than 25 frames a second; more than that arrived after a stall.
        /// </summary>
        internal void SubmitCapture(float[] samples, int count, int sampleRate)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (count < 0 || count > samples.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (sampleRate < 8_000 || sampleRate > 192_000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            engine.RequireOwnerThread();
            if (disposed || !enabled) return;
            var now = clock();

            // An interval longer than a frame rate can be is left out, so a stall's backlog is cut to what came before it.
            ObserveInterval(ref lastSubmitMicros, ref submitIntervalMicros, now);
            var keepMicros = CaptureKeepMicros();
            if (count == 0) return;
            submittedSamples += count;
            var keep = (int)Math.Min(VoiceInboundBatch.CaptureSampleCapacity, Math.Max(1, sampleRate * keepMicros / 1_000_000));
            var offset = 0;
            if (count > keep)
            {
                staleSamplesDropped += count - keep;
                offset = count - keep;
                count = keep;
            }

            bool added;
            lock (inbound.Gate) added = inbound.Writable.TryAddCapture(samples, offset, count, sampleRate, now, transmitting);
            if (!added) captureOverflowSamples += count;
            worker.Signal();
        }

        /// <summary>
        /// Says whether the player transmits: true while the push-to-talk key is held, or while voice activation listens;
        /// false when the key is released or the microphone closes, which ends the talk spurt at once with its last frame.
        /// Capture handed over while not transmitting is only metered. The end of talk follows this call, never the
        /// cadence of <see cref="SubmitCapture"/>.
        /// </summary>
        internal void SetTransmitting(bool value)
        {
            engine.RequireOwnerThread();
            if (disposed || transmitting == value) return;
            if (!value) EndTransmission();
            transmitting = value;
        }

        /// <summary>Mutes or unmutes <paramref name="participant"/> for everyone. Host only; see <c>SessionEngine.SetServerMute</c>.</summary>
        internal bool SetServerMute(ParticipantId participant, bool muted) => engine.SetServerMute(participant, muted);

        /// <summary>Takes the transport's running packet totals; the loss they show sets the encoder's FEC hint.</summary>
        internal void ReportTransportCounters(long packetsSent, long packetsLost) => loss.Report(packetsSent, packetsLost);

        /// <summary>Moves voice between the engine and the worker. Call once per game frame, after the engine's pump.</summary>
        internal void Pump() => Pump(clock());

        internal void Pump(long nowMicros)
        {
            if (disposed) return;
            ObserveInterval(ref lastPumpMicros, ref pumpIntervalMicros, nowMicros);
            ObserveSession();
            SweepUnlisted(nowMicros);
            SendEncoded(nowMicros);
            var queued = ReceiveFrames(nowMicros);
            UpdateTransmitter(nowMicros);
            if (worker.Failed) DropWorkForTheFailedWorker();
            else if (queued) worker.Signal();
        }

        /// <summary>Gets the statistics, on the main thread: it refuses any other.</summary>
        internal VoiceServiceStatistics GetStatistics()
        {
            engine.RequireOwnerThread();
            var transmitter = worker.Transmitter;
            var speakers = new List<VoiceSpeakerStatistics>(streams.Count);
            foreach (var stream in streams.Values) speakers.Add(new VoiceSpeakerStatistics(stream, IsSpeaking(stream.Speaker)));
            return new VoiceServiceStatistics
            {
                Enabled = enabled,
                Mode = mode,
                State = TransmitState,
                InputLevelDb = InputLevelDb,
                ExpectedLossPercent = loss.ExpectedLossPercent,
                SubmittedSamples = submittedSamples,
                StaleSamplesDropped = staleSamplesDropped,
                CaptureOverflowSamples = captureOverflowSamples,
                FramesCaptured = transmitter.FramesCaptured,
                FramesEncoded = transmitter.FramesEncoded,
                DiscontinuousSuppressed = transmitter.DiscontinuousSuppressed,
                EndsOfTalk = transmitter.EndsOfTalk,
                StaleFramesDropped = staleFramesDropped,
                FramesDroppedOff = framesDroppedOff,
                Sent = sent,
                Held = held,
                RateLimited = rateLimited,
                ServerMuted = serverMuted,
                NotConnected = notConnected,
                NotOffered = notOffered,
                Invalid = invalid,
                FramesReceived = framesReceived,
                InboundOverflow = inboundOverflow,
                FramesUnrouted = worker.FramesUnrouted,
                UnlistedFramesDropped = unlistedFramesDropped,
                UnlistedStreamsEnded = unlistedStreamsEnded,
                WorkerSteps = worker.Steps,
                WorkerFaults = worker.Faults,
                SlotsRun = worker.SlotsRun,
                SlotsDropped = worker.SlotsDropped,
                Engine = engine.GetVoiceStatistics(),
                Speakers = speakers
            };
        }

        /// <summary>Stops the worker; every playback stream plays silence from then on.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            worker.Dispose();
            foreach (var stream in streams.Values) stream.End();
            streams.Clear();
            unlisted.Clear();
        }

        /// <summary>How much of one capture call is kept: 60 ms, or one and a half of the main thread's frame intervals.</summary>
        private long CaptureKeepMicros() =>
            Math.Max(MinimumCaptureBacklogMicros, Math.Max(submitIntervalMicros, pumpIntervalMicros) * 3 / 2);

        /// <summary>How long an encoded frame may wait to be sent: 120 ms, or what one call keeps and one and a half pumps.</summary>
        private long SendDelayMicros() => Math.Max(MinimumSendDelayMicros, CaptureKeepMicros() + (pumpIntervalMicros * 3 / 2));

        /// <summary>Folds the interval since the last call into a smoothed one; a pause or a stall is left out.</summary>
        private static void ObserveInterval(ref long last, ref long smoothed, long nowMicros)
        {
            var interval = last == long.MinValue ? 0 : nowMicros - last;
            last = nowMicros;
            if (interval <= 0 || interval > MaximumFrameIntervalMicros) return;
            smoothed = smoothed == 0 ? interval : smoothed + ((interval - smoothed) / 4);
        }

        /// <summary>
        /// Empties what the main thread queued for a worker that gave up. Nothing takes it any more, so a session's events
        /// would pile up in it for as long as the session lasts, each stream it attached keeping its ring.
        /// </summary>
        private void DropWorkForTheFailedWorker()
        {
            lock (inbound.Gate) inbound.Writable.Clear();
        }

        /// <summary>Queues the end of a talk spurt after the capture queued so far, when one could be open.</summary>
        private void EndTransmission()
        {
            if (!transmitting || !enabled) return;
            bool added;
            lock (inbound.Gate) added = inbound.Writable.TryAddEndOfCapture(clock());
            if (!added) captureOverflowSamples++;
            worker.Signal();
        }

        private void SendEncoded(long nowMicros)
        {
            var batch = outbound.Take();
            if (batch == null) return;
            if (!enabled)
            {
                // Coded before voice was turned off, and it is off now: none of it leaves.
                framesDroppedOff += batch.Count;
                batch.Clear();
                outbound.Return(batch);
                return;
            }

            var deadline = SendDelayMicros();
            for (var index = 0; index < batch.Count; index++)
            {
                ref var packet = ref batch.Packet(index);
                if (nowMicros - packet.CapturedMicros > deadline)
                {
                    staleFramesDropped++;
                    continue;
                }

                switch (engine.SubmitVoiceFrame(packet.Timestamp, packet.Flags, batch.Payload(index)))
                {
                    case VoiceSubmitResult.Sent:
                        sent++;
                        lastSentMicros = nowMicros;
                        break;
                    case VoiceSubmitResult.Held:
                        held++;
                        lastSentMicros = nowMicros;
                        break;
                    case VoiceSubmitResult.RateLimited:
                        rateLimited++;
                        break;
                    case VoiceSubmitResult.ServerMuted:
                        serverMuted++;
                        break;
                    case VoiceSubmitResult.NotConnected:
                        notConnected++;
                        break;
                    case VoiceSubmitResult.NotOffered:
                        notOffered++;
                        break;
                    default:
                        invalid++;
                        break;
                }
            }

            batch.Clear();
            outbound.Return(batch);
        }

        private void UpdateTransmitter(long nowMicros)
        {
            var local = engine.LocalParticipantId;
            mutedByHost = local.HasValue && engine.IsServerMuted(local.Value);

            // A host relays only to players who are Ready, so one still joining hears nothing yet and is no listener.
            var listeners = connectedOthers - (engine.Role == EngineRole.Host ? engine.ConnectionsAwaitingReady : 0);
            var hasListeners = listeners > 0;

            // A session that offers no voice chat, as its host chose or as the join was shown, codes nothing at all.
            var offered = engine.IsVoiceOffered;
            var blocked = !enabled || !ready || !hasListeners || mutedByHost || !offered || worker.Failed;
            worker.Configure(mode, thresholdDb, loss.ExpectedLossPercent, blocked);
            if (worker.Failed && !failureReported)
            {
                failureReported = true;
                diagnostics?.Report(new EngineDiagnostic(
                    EngineDiagnosticKind.VoiceFailed,
                    "Voice chat stopped after its worker thread failed repeatedly.",
                    exception: worker.LastFault));
            }

            TransmitState = worker.Failed ? VoiceTransmitState.Failed
                : !offered ? VoiceTransmitState.NotOffered
                : !enabled ? VoiceTransmitState.Idle
                : !ready ? VoiceTransmitState.NotConnected
                : mutedByHost ? VoiceTransmitState.MutedByHost
                : !hasListeners ? VoiceTransmitState.NoListeners
                : lastSentMicros != long.MinValue && nowMicros - lastSentMicros < TransmittingMicros ? VoiceTransmitState.Transmitting
                : VoiceTransmitState.Idle;
        }
    }
}
