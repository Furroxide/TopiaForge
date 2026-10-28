extern alias provider;

using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.CommunicationRoutingTests;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class VoiceRetirementTests
{
    internal static void Engine()
    {
        var failures = new List<string>();
        foreach (var senderAcknowledged in new[] { false, true })
        {
            using var rig = new VoiceSessionHarness();
            var speaker = rig.AddClient("speaker");
            var listener = rig.AddClient("listener");
            var hostHeard = rig.Listen(rig.Host);
            var listenerHeard = rig.Listen(listener.Engine);
            using var lifetime = new FakeModLifetime();
            using var routing = rig.Host.RouteCommunications(lifetime, new CommunicationPolicy(
                new[] { Member("speaker", 1), Member("host", 2), Member("listener", 2) },
                new[] { new CommunicationRoute(1, 1), new CommunicationRoute(2, 2) })).Value!;
            // Keep an actual old-epoch packet until the replacement listener has acknowledged.
            rig.Stalled.Add(rig.Host);
            speaker.Engine.SubmitVoiceFrame(1, VoiceFrameFlags.EndOfTalk, rig.Codec.Packet(71));
            rig.Advance();
            var retired = TakeVoice(speaker.Link.Peer!);
            rig.Stalled.Clear();
            rig.Stalled.Add(listener.Engine);
            rig.Host.SubmitVoiceFrame(1, VoiceFrameFlags.EndOfTalk, rig.Codec.Packet(73));
            rig.Advance();
            var retiredRelay = TakeVoice(listener.Link);
            rig.Stalled.Clear();
            if (!senderAcknowledged) rig.Stalled.Add(speaker.Engine);
            rig.Host.ReplaceSession(new MultiplayerSessionId("replacement"), new SessionSeed(42));
            rig.Advance(10);
            That(rig.Host.IsParticipantReady(new ParticipantId("listener")), "replacement listener acknowledged");
            That(rig.Host.IsParticipantReady(new ParticipantId("speaker")) == senderAcknowledged,
                "sender acknowledgement is controlled independently");
            speaker.Link.Send(EngineChannel.Voice, new ArraySegment<byte>(retired));
            rig.Advance(5);
            if (hostHeard.Frames.Count != 0 || listenerHeard.Frames.Count != 0)
                failures.Add("retired uplink leaked with sender acknowledged=" + senderAcknowledged);
            listener.Link.Peer!.Send(EngineChannel.Voice, new ArraySegment<byte>(retiredRelay));
            rig.Advance(5);
            if (listenerHeard.Frames.Any(frame => frame.Id == 73)) failures.Add("retired downlink leaked after replacement");
            rig.Stalled.Clear();
            rig.Advance(10);
            speaker.Engine.SubmitVoiceFrame(10, VoiceFrameFlags.EndOfTalk, rig.Codec.Packet(72));
            rig.Advance(5);
            That(hostHeard.From("speaker").Any(frame => frame.Id == 72) && listenerHeard.From("speaker").Any(frame => frame.Id == 72),
                "fresh replacement speech reaches both listeners");
        }
        That(failures.Count == 0, string.Join("; ", failures));
        Console.WriteLine("Voice retirement engine: delayed uplinks rejected before and after sender acknowledgement; fresh speech passed.");
    }

    private static byte[] TakeVoice(VirtualLinkEnd link)
    {
        while (link.TryReceive(out var channel, out var bytes))
            if (channel == EngineChannel.Voice) return bytes;
        throw new InvalidOperationException("The engine did not send the retained voice fixture.");
    }

    internal static void Service()
    {
        var failures = new List<string>();
        foreach (var clientSpeaker in new[] { false, true })
        foreach (var stage in new[] { "capture", "encoded", "partial", "late-result" })
        {
            using var rig = new VoiceServiceRig();
            var host = rig.AddHost(withVoice: !clientSpeaker);
            var remote = rig.AddClient("speaker", withVoice: clientSpeaker);
            var speaker = clientSpeaker ? remote : host;
            var listener = clientSpeaker ? host : remote;
            var voice = speaker.Voice!;
            using var lifetime = new FakeModLifetime();
            using var routing = host.Engine.RouteCommunications(lifetime, new CommunicationPolicy(
                new[] { Member("host", 1), Member("speaker", 2) }, Array.Empty<CommunicationRoute>())).Value!;
            rig.Step(10);
            voice.SetTransmitting(true);
            var samples = Enumerable.Range(0, 960).Select(i => (float)(0.4 * Math.Sin(i * 0.07))).ToArray();
            voice.SubmitCapture(samples, stage is "partial" or "late-result" ? 480 : 960, 48_000);
            if (stage != "capture") voice.Worker.Step(rig.NowMicros);
            if (stage is "partial" or "late-result")
                That(voice.Worker.Transmitter.Framer.PendingSamples == 480, "retired partial frame fixture");
            var submitted = speaker.Engine.GetVoiceStatistics().Submitted;
            host.Engine.ReplaceSession(new MultiplayerSessionId("after-private"), new SessionSeed(43));
            // Pump only the engines: queued worker output remains younger than the send deadline.
            for (var step = 0; step < 10; step++)
            {
                host.Engine.Pump(rig.NowMicros);
                remote.Engine.Pump(rig.NowMicros);
                rig.Network.Now = rig.Tick + (ulong)step + 1;
                rig.Network.DeliverDue(rig.Network.Now);
            }
            That(speaker.Engine.Snapshot.State == MultiplayerSessionState.Ready, "replacement speaker ready");
            voice.Pump();
            if (stage == "late-result")
            {
                // Deterministic worker interleaving: an already-running old Feed finishes after retirement.
                voice.Worker.Transmitter.Feed(samples.AsSpan(0, 480), 48_000, rig.NowMicros, true);
                voice.Pump();
            }
            voice.Worker.Step(rig.NowMicros);
            voice.Pump();
            if (speaker.Engine.GetVoiceStatistics().Submitted != submitted || voice.Worker.Transmitter.Framer.PendingSamples != 0)
                failures.Add((clientSpeaker ? "client " : "host ") + stage + " survived retirement");
            // The same microphone remains usable after the boundary.
            voice.SubmitCapture(samples, samples.Length, 48_000);
            voice.Worker.Step(rig.NowMicros + 20_000);
            voice.Pump(rig.NowMicros + 20_000);
            That(speaker.Engine.GetVoiceStatistics().Submitted > submitted, "fresh capture resumes after retirement");
        }
        CaptureBeforeServicePump();
        RetirementPreservesTheCaptureClock();
        That(failures.Count == 0, string.Join("; ", failures));
        Console.WriteLine("Voice retirement service: eight host/client retirement cases and capture before service pump passed; fresh capture resumed.");
    }

    private static void CaptureBeforeServicePump()
    {
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        rig.AddClient("listener", withVoice: false);
        rig.Step(10);
        var voice = host.Voice!;
        var half = Enumerable.Repeat(0.4f, 480).ToArray();
        voice.SetTransmitting(true);
        voice.SubmitCapture(half, half.Length, 48_000);
        voice.Worker.Step(rig.NowMicros);
        That(voice.Worker.Transmitter.Framer.PendingSamples == 480, "old half-frame waits before replacement");
        host.Engine.ReplaceSession(new MultiplayerSessionId("capture-first"), new SessionSeed(44));
        // A microphone callback can precede the next service pump; it must observe retirement itself.
        voice.SubmitCapture(half, half.Length, 48_000);
        voice.Worker.Step(rig.NowMicros);
        That(voice.Worker.Transmitter.Framer.PendingSamples == 480 && voice.Worker.Transmitter.FramesEncoded == 0,
            "new half-frame never completes a retired half-frame before the service pump");
        voice.SubmitCapture(half, half.Length, 48_000);
        voice.Worker.Step(rig.NowMicros);
        That(voice.Worker.Transmitter.Framer.PendingSamples == 0 && voice.Worker.Transmitter.FramesEncoded == 1,
            "two fresh half-frames complete normally");
    }

    private static void RetirementPreservesTheCaptureClock()
    {
        var framer = new provider::TopiaForge.Multiplayer.Voice.VoiceCaptureFramer();
        var samples = new float[960];
        var sink = new FrameSink();
        framer.Feed(samples, 48_000, 1_000_000, sink);
        framer.Feed(samples, 48_000, 2_000_000, sink);
        That(framer.NextTimestamp == 51, "fixture establishes a long-running capture clock");
        framer.Discard();
        framer.Feed(samples, 48_000, 2_020_000, sink);
        framer.Feed(samples, 48_000, 2_220_000, sink);
        That(framer.NextTimestamp == 62, "retirement preserves monotonic numbering and later pause duration");
    }

    private sealed class FrameSink : provider::TopiaForge.Multiplayer.Voice.IVoiceFrameSink
    {
        public void OnFrame(ReadOnlySpan<short> frame, ulong timestamp, long capturedMicros, bool endOfCapture) { }
    }
}
