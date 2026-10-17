extern alias provider;

using provider::TopiaForge.Multiplayer.Engine;
using provider::TopiaForge.Multiplayer.Voice;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The provider's voice service through the rig with the real codec: speech reaches every listener with bounded delay,
/// fits the transport's voice shares at the 20 kbit/s target, survives reconnects and restarts, and stops at once for mutes.
/// A worker that gives up leaves nothing queued, and the readings belong to the main thread.
/// </summary>
internal static class VoiceServiceTests
{
    // Measured at 126 ms: capture framing, pairing, two 10 ms hops, the jitter buffer's 60 ms start and the stream's cushion.
    private const int MaximumDelaySamples = 7_200;

    internal static void SpeechReachesEveryListenerWithBoundedDelay()
    {
        using var rig = new VoiceServiceRig();
        var everyone = new[] { rig.AddHost(), rig.AddClient("a"), rig.AddClient("b"), rig.AddClient("c") };
        foreach (var listener in everyone)
        {
            foreach (var speaker in everyone.Where(other => other != listener)) listener.Listen(speaker.Name);
        }

        for (var index = 0; index < everyone.Length; index++) everyone[index].Source = VoiceSource.Speech(3, seed: (uint)(index + 3));
        rig.StepFor(3_500_000);
        foreach (var listener in everyone)
        {
            var statistics = listener.Voice!.GetStatistics();
            foreach (var speaker in everyone.Where(other => other != listener))
            {
                var output = listener.Heard(speaker.Name).ToArray();
                var (delay, correlation) = VoiceAudio.FindDelay(speaker.Source!.Signal, output, 24_000, 96_000, 14_400);
                var snr = VoiceAudio.SnrDecibels(speaker.Source.Signal, output, 24_000, 96_000, delay);
                var path = listener.Name + " hearing " + speaker.Name;
                Assert(delay > 1_920 && delay <= MaximumDelaySamples, path + " is delayed by at most 150 ms (" + (delay / 48) + " ms)");
                Assert(correlation >= 0.9 && snr >= 7, path + " gets the speech, as the codec alone keeps it (correlation " +
                    correlation.ToString("F3") + ", " + snr.ToString("F1") + " dB)");
                var counters = statistics.Speakers.Single(entry => entry.Speaker.Value == speaker.Name);
                Assert(counters.Concealed == 0 && counters.Late == 0 && counters.Underruns == 0,
                    path + " conceals nothing, loses nothing late and never runs dry");
            }

            Assert(statistics.Invalid == 0 && statistics.StaleFramesDropped == 0 && statistics.RateLimited == 0, listener.Name + " sends cleanly");
        }
    }

    internal static void VoiceFitsTheTransportShares()
    {
        // Four talkers at once for five seconds at the 20 kbit/s target, counting the 40 bytes a datagram takes on the wire.
        // That is more than the class shaper charges against the shares, its DATAGRAM frame's 4 to 7, so the check is
        // stricter than the transport's own.
        using var rig = new VoiceServiceRig();
        var everyone = new[] { rig.AddHost(), rig.AddClient("a"), rig.AddClient("b"), rig.AddClient("c") };
        for (var index = 0; index < everyone.Length; index++) everyone[index].Source = VoiceSource.Speech(5, seed: (uint)(index + 7));
        rig.StepFor(5_200_000);
        foreach (var client in everyone.Skip(1))
        {
            var uplink = client.Link!.Outbound!;
            var datagrams = uplink.ChannelFramesSent[(int)EngineChannel.Voice];
            var up = Kilobits(uplink.ChannelBytesSent[(int)EngineChannel.Voice], datagrams);
            Assert(datagrams >= 125 && datagrams <= 127, client.Name + " sends its 250 frames in pairs (" + datagrams + " datagrams)");
            Assert(up <= 32, client.Name + "'s voice fits the 32 kbit/s client share even on the wire (" + up.ToString("F1") + " kbit/s)");
            var downlink = client.Link.Peer!.Outbound!;
            var down = Kilobits(downlink.ChannelBytesSent[(int)EngineChannel.Voice], downlink.ChannelFramesSent[(int)EngineChannel.Voice]);
            Assert(down <= 96, "three speakers relayed to " + client.Name + " fit the 96 kbit/s share even on the wire (" + down.ToString("F1") + " kbit/s)");
        }

        var host = everyone[0].Engine.GetVoiceStatistics();
        Assert(host.Strikes == 0 && host.TotalDropped == 0, "the host relays everything and strikes nobody");
    }

    internal static void ALateListenerHearsTheSpeakerAsTheyAreNow()
    {
        // b's stream for a exists from the start, but nothing reads it for two seconds, as before a's avatar has an
        // emitter. Meanwhile the worker keeps a's jitter buffer moving and drops the audio, so the first read is fresh.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        var stream = b.Voice!.GetPlaybackStream(new ParticipantId("a"));
        a.Source = VoiceSource.Speech(4, seed: 12);
        rig.StepFor(2_000_000);
        Assert(stream.Buffered == 0 && stream.OverflowSamples == 0, "nothing piles up in a stream nobody reads");
        b.Listen("a");
        rig.StepFor(1_500_000);
        var output = b.Heard("a").ToArray();
        Assert(VoiceAudio.LevelDecibels(output, 0, VoiceServiceRig.ReadSamples) < -90 && stream.SkippedSamples == 0,
            "the first read finds nothing stale to play or skip");
        var reference = a.Source.Signal.Skip(2 * 48_000).ToArray();
        var (delay, correlation) = VoiceAudio.FindDelay(reference, output, 9_600, 48_000, 14_400);
        Assert(correlation >= 0.9 && delay <= MaximumDelaySamples,
            "the first second b plays is what a says now, with the usual delay (" + (delay / 48) + " ms, correlation " + correlation.ToString("F3") + ")");
        var counters = Counters(b, "a");
        Assert(counters.Concealed == 0 && counters.Late == 0, "and the idle stretch cost a's stream nothing");
    }

    internal static void NobodyToHearMeansNothingIsCoded()
    {
        // A host alone in its session talks: capture is framed and metered, but nothing is encoded or sent.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        host.Source = VoiceSource.Speech(1, seed: 13);
        rig.StepFor(1_000_000);
        var statistics = host.Voice!.GetStatistics();
        Assert(statistics.FramesCaptured >= 45 && statistics.FramesEncoded == 0 && statistics.Sent + statistics.Held == 0,
            "fifty frames captured, none coded (" + statistics.FramesCaptured + " captured, " + statistics.FramesEncoded + " coded)");
        Assert(host.Voice.TransmitState == VoiceTransmitState.NoListeners && statistics.InputLevelDb > -50, "the indicator says why, and the meter still moves");
    }

    internal static void AReconnectOrRestartStartsTheSpeakerAfresh()
    {
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        b.Listen("a");
        a.Source = VoiceSource.Speech(6, seed: 5);
        rig.StepFor(1_000_000);
        var before = Counters(b, "a");
        Assert(before.Decoded > 30 && before.Restarts == 0, "b hears a");

        // a's link drops and a rejoins over a new one. The host gives a's new stream a new generation, so b's jitter
        // buffer and decoder start again at its first frame; the service resets nothing of its own.
        rig.Network.Sever(a.Link!);
        rig.StepFor(200_000);
        rig.Connect(a);
        rig.StepFor(1_000_000);
        var reconnected = Counters(b, "a");
        Assert(reconnected.Restarts == 1 && reconnected.Resets == 0,
            "b started a's new stream once, by its generation (" + reconnected.Restarts + " restarts, " + reconnected.Resets + " resets)");
        Assert(reconnected.Decoded > before.Decoded + 30 && reconnected.Concealed <= 5, "and hears a again, with at most one short gap concealed");
        Assert(a.Voice!.TransmitState == VoiceTransmitState.Transmitting, "a talks again once ready");

        // a's game restarts: a new engine whose sequences start at 1 joins under the same identity.
        var restarted = rig.Restart(a);
        restarted.Source = VoiceSource.Speech(2, seed: 9);
        rig.StepFor(1_200_000);
        var after = Counters(b, "a");
        Assert(after.Restarts == 2 && after.Resets == 0 && after.Decoded > reconnected.Decoded + 30, "b starts the new process's stream too, and hears it");
        Assert(host.Engine.GetVoiceStatistics().Dropped(VoiceDropReason.NotReady) == 0, "the host drops none of either new stream");
        var tail = b.Heard("a").Skip(b.Heard("a").Count - 24_000).ToArray();
        Assert(VoiceAudio.LevelDecibels(tail, 0, tail.Length) > -35, "the new stream plays as speech, not silence");
    }

    internal static void AReturningSpeakerIsHeardBeforeTheSessionSaysSo()
    {
        // The host relays a returning talker's datagrams at once, while the reliable stream that says the talker is back
        // can fall behind them: here it is held for a second. b plays a's new stream from its first frame, and learning of
        // the return afterwards resets nothing, so none of it is lost.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        var stream = b.Listen("a");
        a.Source = VoiceSource.Speech(5, seed: 14);
        rig.StepFor(1_000_000);
        rig.Network.Sever(a.Link!);
        for (var step = 0; step < 50 && IsConnected(b, "a"); step++) rig.Step();
        Assert(!IsConnected(b, "a"), "b learns that a's link went");
        rig.StepFor(200_000);

        var toB = b.Link!.Peer!.Outbound!;
        rig.Network.HoldNext(toB, 1_000);
        var gone = Counters(b, "a");
        rig.Connect(a);
        rig.StepFor(1_000_000);
        var early = Counters(b, "a");
        Assert(!IsConnected(b, "a") && early.Restarts == 1 && early.Decoded > gone.Decoded + 35,
            "a's new stream plays at b before the session says a is back (" + (early.Decoded - gone.Decoded) + " frames)");

        var skipped = stream.SkippedSamples;
        rig.Network.Release(toB);
        rig.StepFor(500_000);
        var later = Counters(b, "a");
        Assert(IsConnected(b, "a"), "the held session stream then tells b that a is back");
        Assert(later.Resets == 0 && later.Restarts == 1 && stream.SkippedSamples == skipped && later.Concealed == early.Concealed,
            "and b resets, drops and conceals none of a's stream for it");
        var tail = b.Heard("a").Skip(b.Heard("a").Count - 24_000).ToArray();
        Assert(VoiceAudio.LevelDecibels(tail, 0, tail.Length) > -35, "a is still heard");
    }

    internal static void MutesStopASpeakerAtOnce()
    {
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        var c = rig.AddClient("c");
        b.Listen("a");
        c.Listen("a");
        a.Source = VoiceSource.Speech(5, seed: 6);
        rig.StepFor(1_000_000);

        // b mutes a: what is buffered goes at once, and the host stops relaying a to b. c still hears a.
        var muteAt = b.Heard("a").Count;
        b.Voice!.SetListenerMutes(new[] { new ParticipantId("a") });
        rig.StepFor(500_000);
        var bTail = b.Heard("a").Skip(muteAt + 960).ToArray();
        var cTail = c.Heard("a").Skip(muteAt + 960).ToArray();
        Assert(VoiceAudio.LevelDecibels(bTail, 0, bTail.Length) < -90, "b hears silence within 20 ms of muting a");
        Assert(VoiceAudio.LevelDecibels(cTail, 0, cTail.Length) > -35, "c still hears a");
        Assert(host.Engine.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) > 0, "the host skips b when it relays a");
        b.Voice.SetListenerMutes(Array.Empty<ParticipantId>());
        rig.StepFor(500_000);
        var bBack = b.Heard("a").Skip(b.Heard("a").Count - 12_000).ToArray();
        Assert(VoiceAudio.LevelDecibels(bBack, 0, bBack.Length) > -35, "unmuted, b hears a again");

        // The host mutes a for everyone: a stops transmitting, which is a state, not an error.
        Assert(host.Voice!.SetServerMute(new ParticipantId("a"), true), "the host mutes a");
        rig.StepFor(300_000);
        var heardBy = c.Heard("a").Count;
        rig.StepFor(500_000);
        var silent = c.Heard("a").Skip(heardBy).ToArray();
        Assert(a.Voice!.TransmitState == VoiceTransmitState.MutedByHost, "a's indicator says muted by host");
        Assert(VoiceAudio.LevelDecibels(silent, 0, silent.Length) < -90, "and nobody hears a");
        var muted = a.Voice.GetStatistics();
        Assert(muted.Invalid == 0 && muted.RateLimited == 0, "a muted talker reports no errors");
        host.Voice.SetServerMute(new ParticipantId("a"), false);
        rig.StepFor(600_000);
        var resumed = c.Heard("a").Skip(c.Heard("a").Count - 12_000).ToArray();
        Assert(a.Voice.TransmitState == VoiceTransmitState.Transmitting && VoiceAudio.LevelDecibels(resumed, 0, resumed.Length) > -35,
            "unmuted, a is heard again");
    }

    internal static void NothingLeavesUntilVoiceIsTurnedOn()
    {
        // Voice stays off until the player turns it on: a talks with the key held, and nothing is captured, coded or sent.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        b.Listen("a");
        a.Voice!.Enabled = false;
        a.Source = VoiceSource.Speech(3, seed: 33);
        rig.StepFor(1_000_000);
        var off = a.Voice.GetStatistics();
        Assert(!off.Enabled && off.SubmittedSamples == 0 && off.FramesCaptured == 0 && off.FramesEncoded == 0 && off.FramesSent == 0,
            "with voice off nothing is kept, framed, coded or sent (" + off.SubmittedSamples + ", " + off.FramesCaptured + ", " + off.FramesEncoded + ")");
        Assert(off.Engine!.Submitted == 0 && host.Engine.GetVoiceStatistics().Relayed == 0, "the session never sees a frame of a's");
        Assert(a.Voice.TransmitState == VoiceTransmitState.Idle && a.Voice.InputLevelDb <= -96, "the indicator is idle and the meter reads silence");

        // Turned on, a is heard. Turned off again mid-sentence, a stops at once: nothing more is coded, and what was coded
        // but had not left is dropped, so b's playback of a fades out.
        a.Voice.Enabled = true;
        rig.StepFor(1_000_000);
        var tail = b.Heard("a").Skip(b.Heard("a").Count - 12_000).ToArray();
        Assert(VoiceAudio.LevelDecibels(tail, 0, tail.Length) > -35, "turned on, a is heard");
        a.Voice.Enabled = false;
        var stopped = a.Voice.GetStatistics();
        rig.StepFor(500_000);
        var later = a.Voice.GetStatistics();
        Assert(later.FramesEncoded == stopped.FramesEncoded && later.Engine!.Submitted == stopped.Engine!.Submitted,
            "turned off, nothing more is coded or sent (" + (later.FramesEncoded - stopped.FramesEncoded) + " coded, " +
            (later.Engine!.Submitted - stopped.Engine!.Submitted) + " sent)");
        var faded = b.Heard("a").Skip(b.Heard("a").Count - 12_000).ToArray();
        Assert(VoiceAudio.LevelDecibels(faded, 0, faded.Length) < -90, "and b hears a no more");
    }

    internal static void AHostCodesOnlyForReadyListeners()
    {
        // The host talks while a joins, and a's acknowledgement of the session is held back. The host relays only to players
        // who are Ready, so until a is, it has nobody to hear it and codes nothing.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        host.Source = VoiceSource.Speech(3, seed: 34);
        rig.StepFor(300_000);
        var a = rig.AddClient("a", untilReady: false);
        rig.Step();
        rig.Network.HoldNext(a.Link!.Outbound!, 1_000);
        rig.StepFor(200_000);
        var joining = host.Voice!.GetStatistics();
        Assert(a.Engine.Snapshot.State == MultiplayerSessionState.Ready && host.Engine.ConnectionsAwaitingReady == 1,
            "a is Ready itself, and the host still waits for it to say so");
        rig.StepFor(500_000);
        var waited = host.Voice.GetStatistics();
        Assert(waited.FramesEncoded == joining.FramesEncoded && host.Voice.TransmitState == VoiceTransmitState.NoListeners,
            "meanwhile the host codes nothing, and says it has no listeners (" + (waited.FramesEncoded - joining.FramesEncoded) + " coded)");
        rig.Network.Release(a.Link.Outbound!);
        a.Listen("host");
        rig.StepFor(1_000_000);
        var heard = a.Heard("host").ToArray();
        Assert(host.Voice.GetStatistics().FramesEncoded > waited.FramesEncoded + 40 && VoiceAudio.LevelDecibels(heard, heard.Length / 2, heard.Length / 2) > -35,
            "once a is Ready, the host codes again and a hears it");
    }

    internal static void AWorkerThatGivesUpLeavesNothingQueued()
    {
        // The worker's steps fail, here because its clock does. Nine failures, a step that works and nine more: it carries
        // on. A tenth in a row and it gives up, which voice reports once and its transmit state shows. The session goes on,
        // and so do its events: mutes change, speakers come and go, the player holds the key and talks. Nothing takes what
        // the main thread queues for the worker any more, so every pump drops it, and none of it piles up.
        var codec = new OpusVoiceCodec();
        var options = new EngineOptions
        {
            StepRateHz = 60,
            DownstreamRateHz = 0,
            ClockSyncIntervalMicros = 0,
            ParticipantIdForIdentity = identity => new ParticipantId(identity),
            Voice = new VoiceOptions(codec)
        };
        var network = new VirtualEngineTransport(new VirtualLinkConditions { LatencyTicks = 1 });
        var now = 1_000_000L;
        var failing = false;
        var reports = new Reports();
        var host = SessionEngine.CreateHost(options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("failing"), new SessionSeed(1),
            new ParticipantId("host"), "Host", now);
        var client = SessionEngine.CreateClient(options, new MultiplayerSessionId("failing"), now);
        var pair = network.Connect("a");
        host.Accept(pair.HostEnd);
        client.Connect(pair.ClientEnd, "a");
        var voice = new VoiceService(client, codec, () => failing ? throw new InvalidOperationException("The voice clock failed.") : now, reports, startWorker: false)
        {
            Enabled = true
        };
        ulong tick = 0;
        void Pump()
        {
            tick++;
            now += 10_000;
            network.Now = tick;
            network.DeliverDue(tick);
            host.Pump(now);
            client.Pump(now);
            voice.Pump(now);
        }

        bool Fail(int steps)
        {
            failing = true;
            var running = true;
            for (var step = 0; step < steps; step++) running = voice.Worker.TryStep();
            failing = false;
            return running;
        }

        try
        {
            for (var step = 0; step < 20 && client.Snapshot.State != MultiplayerSessionState.Ready; step++) Pump();
            Assert(client.Snapshot.State == MultiplayerSessionState.Ready, "a joins");
            Assert(Fail(VoiceWorker.FaultLimit - 1) && voice.Worker.TryStep() && Fail(VoiceWorker.FaultLimit - 1) && !voice.Worker.Failed,
                "nine failed steps, one that works and nine more leave the worker running");
            Assert(!Fail(1) && voice.Worker.Failed && voice.Worker.Faults == (2 * VoiceWorker.FaultLimit) - 1, "a tenth in a row and it gives up");
            Pump();
            Assert(voice.TransmitState == VoiceTransmitState.Failed && reports.Count(EngineDiagnosticKind.VoiceFailed) == 1, "voice says so, and reports it once");

            var samples = new float[480];
            var most = 0;
            for (var frame = 0; frame < 500; frame++)
            {
                voice.SetListenerMutes(Array.Empty<ParticipantId>(), muteAll: frame % 2 == 0);
                voice.GetPlaybackStream(new ParticipantId("s" + frame));
                voice.SetTransmitting(frame % 10 != 0);
                voice.SubmitCapture(samples, samples.Length, 48_000);
                Pump();
                lock (voice.Inbound.Gate)
                {
                    var batch = voice.Inbound.Writable;
                    most = Math.Max(most, batch.Controls.Count + batch.CaptureCount + batch.FrameCount);
                }
            }

            Assert(most == 0, "every pump drops what was queued for the worker that gave up (" + most + " entries waited at most)");
            Assert(reports.Count(EngineDiagnosticKind.VoiceFailed) == 1, "and voice reported its failure once");
        }
        finally
        {
            voice.Dispose();
            client.Shutdown(MultiplayerSessionEndReason.Left);
            host.Shutdown(MultiplayerSessionEndReason.HostEnded);
        }
    }

    internal static void TheIndicatorAndStatisticsBelongToTheMainThread()
    {
        // The talking indicator and the statistics read the speakers the main thread keeps, so from any other thread both
        // are refused at once, as capture is.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        b.Listen("a");
        a.Source = VoiceSource.Speech(1, seed: 44);
        rig.StepFor(500_000);
        var voice = b.Voice!;
        Assert(voice.IsSpeaking(a.Id) && voice.GetStatistics().Speakers.Count == 1, "on the main thread, b sees a speaking");
        var refused = new List<string>();
        var calls = new (string Name, Action Call)[] { ("IsSpeaking", () => voice.IsSpeaking(a.Id)), ("GetStatistics", () => voice.GetStatistics()) };
        var thread = new Thread(() =>
        {
            foreach (var (name, call) in calls)
            {
                try
                {
                    call();
                }
                catch (InvalidOperationException)
                {
                    refused.Add(name);
                }
            }
        });
        thread.Start();
        thread.Join();
        Assert(refused.Count == calls.Length, "both are refused on another thread (refused: " + string.Join(", ", refused) + ")");
    }

    internal static VoiceSpeakerStatistics Counters(VoiceServiceRig.Member listener, string speaker) =>
        listener.Voice!.GetStatistics().Speakers.Single(entry => entry.Speaker.Value == speaker);

    private static bool IsConnected(VoiceServiceRig.Member observer, string participant) =>
        observer.Engine.Snapshot.TryGetParticipant(new ParticipantId(participant), out var entry) && entry!.IsConnected;

    private static double Kilobits(long bytes, long datagrams) =>
        (bytes + (datagrams * VoiceServiceRig.TransportOverheadBytes)) * 8.0 / 5 / 1000;

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    /// <summary>The diagnostics a voice service reports, by kind.</summary>
    private sealed class Reports : IEngineDiagnostics
    {
        private readonly List<EngineDiagnostic> reports = new();

        public void Report(EngineDiagnostic diagnostic) => reports.Add(diagnostic);

        internal int Count(EngineDiagnosticKind kind) => reports.Count(report => report.Kind == kind);
    }
}
