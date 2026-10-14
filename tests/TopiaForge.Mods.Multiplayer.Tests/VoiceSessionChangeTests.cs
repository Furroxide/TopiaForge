extern alias provider;

using provider::TopiaForge.Multiplayer.Engine;
using provider::TopiaForge.Multiplayer.Voice;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The voice service across session changes, through the rig with the real codec: every jitter buffer goes when the
/// session changes, in order with the frames queued for the worker, and the listener mutes are the engine's, kept or
/// forgotten at each join, with only the player's wish to hear nobody given back.
/// </summary>
internal static class VoiceSessionChangeTests
{
    private const int MaximumDelaySamples = 7_200;
    private static readonly MultiplayerSessionId SecondSession = new("voice-service-2");

    internal static void ASessionChangeDropsEveryJitterBuffer()
    {
        // Two hosts number their streams alike. On each, "a" is the first player to join, so both a's streams are
        // generation 2, and the second host's a has talked longer, so its numbers run on past the first one's. b hears the
        // first host's a, then moves to the second host, whose a is quiet for now, while b's voice worker is stalled: the
        // first session's last frames are still queued for the worker when b arrives. The jitter buffers go at the change,
        // after those frames, so b plays none of the session it left, and hears the second host's a as usual.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var second = rig.AddHost("host-2", session: SecondSession.Value);
        var farA = rig.AddClient("a", host: second);
        farA.Source = VoiceSource.Speech(2, seed: 21);
        rig.StepFor(2_100_000);
        var nearA = rig.AddClient("a");
        var b = rig.AddClient("b");
        var listenAt = rig.NowMicros;
        b.Listen("a");
        nearA.Source = VoiceSource.Speech(4, seed: 22);
        rig.StepFor(1_000_000);
        var before = VoiceServiceTests.Counters(b, "a");
        Assert(before.Decoded > 30, "b hears the first host's a");

        b.WorkerStalled = true;
        var stalledAt = b.Voice!.GetStatistics().FramesReceived;
        rig.StepFor(100_000);
        var queued = b.Voice.GetStatistics().FramesReceived - stalledAt;
        rig.Connect(b, second);
        for (var step = 0; step < 100 && b.Engine.Snapshot.State != MultiplayerSessionState.Ready; step++) rig.Step();
        Assert(b.Engine.Snapshot.Id.Equals(SecondSession) && b.Engine.Snapshot.State == MultiplayerSessionState.Ready && queued >= 4,
            "b moved to the second host with the first one's last " + queued + " frames still queued");
        var resumedAt = b.Heard("a").Count;
        b.WorkerStalled = false;
        rig.StepFor(500_000);
        var quiet = b.Heard("a").Skip(resumedAt).ToArray();
        Assert(VoiceAudio.LevelDecibels(quiet, 0, quiet.Length) < -90, "b plays none of the session it left");

        // The stalled worker caught up five slots at once when it resumed, which leaves up to 100 ms more in the stream
        // until drift control trims it, a frame a second; hence a looser bound on the delay here than elsewhere.
        var talkAt = rig.NowMicros;
        farA.Source = VoiceSource.Speech(3, seed: 23);
        rig.StepFor(2_000_000);
        var output = b.Heard("a").Skip((int)((talkAt - listenAt) * 48 / 1_000)).ToArray();
        var (delay, correlation) = VoiceAudio.FindDelay(farA.Source.Signal, output, 24_000, 48_000, 19_200);
        Assert(correlation >= 0.9 && delay <= MaximumDelaySamples + 4_800,
            "b hears the second host's a (" + (delay / 48) + " ms, correlation " + correlation.ToString("F3") + ")");
        var after = VoiceServiceTests.Counters(b, "a");
        Assert(after.TargetDelayMicros <= 80_000 && after.Late == before.Late,
            "with a delay target of that stream's own (" + (after.TargetDelayMicros / 1_000) + " ms) and nothing late");
    }

    internal static void AHostWhoseSessionIsReplacedDropsItsJitterBuffers()
    {
        // A host's state never leaves Ready, so only the new session id says the session changed. Its buffers go too.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        var a = rig.AddClient("a");
        host.Listen("a");
        a.Source = VoiceSource.Speech(3, seed: 25);
        rig.StepFor(1_000_000);
        Assert(VoiceServiceTests.Counters(host, "a").Resets == 0, "the host hears a");
        host.Engine.ReplaceSession(new MultiplayerSessionId("voice-service-replaced"), new SessionSeed(12));
        rig.StepFor(1_000_000);
        var counters = VoiceServiceTests.Counters(host, "a");
        Assert(counters.Resets == 1 && counters.Decoded > 80, "the host dropped a's jitter buffer once, at the new session, and hears a on");
    }

    internal static void TheWorkerAppliesControlInTheOrderItWasQueued()
    {
        // One batch holds the last frames of a stream, a reset, and the first frames of another stream that carries the
        // same generation and numbers on exactly where the first left off, so nothing in the frames tells the two apart.
        // The reset drops only what came before it, so the stream plays what a fresh decoder makes of the second stream.
        var codec = new OpusVoiceCodec();
        var inbound = new VoiceExchange<VoiceInboundBatch>(new VoiceInboundBatch(), new VoiceInboundBatch());
        var outbound = new VoiceExchange<VoiceOutboundBatch>(new VoiceOutboundBatch(), new VoiceOutboundBatch());
        var now = 1_000_000L;
        var worker = new VoiceWorker(codec, inbound, outbound, () => now);
        var speaker = new ParticipantId("a");
        var stream = new VoicePlaybackStream(speaker);
        var old = Encode(codec, seed: 26);
        var fresh = Encode(codec, seed: 27);
        var read = new float[960];

        // The speaker is attached and read before the batch arrives, so no slot plays to nobody.
        lock (inbound.Gate) inbound.Writable.AddControl(VoiceControlKind.Attach, speaker, stream);
        worker.Step(now);
        stream.Read(read, 0, read.Length);
        now += VoiceWorker.SlotMicros;
        lock (inbound.Gate)
        {
            var batch = inbound.Writable;
            for (var index = 0; index < old.Count; index++) Add(batch, speaker, (ulong)(index + 1), (ulong)index, old[index], now - 400_000 + (index * 20_000));
            batch.AddControl(VoiceControlKind.ResetAll);
            for (var index = 0; index < fresh.Count; index++) Add(batch, speaker, (ulong)(index + 11), (ulong)(index + 10), fresh[index], now);
        }

        var output = new List<float>();
        for (var slot = 0; slot < 40; slot++)
        {
            worker.Step(now);
            stream.Read(read, 0, read.Length);
            output.AddRange(read);
            now += VoiceWorker.SlotMicros;
        }

        var expected = Decode(codec, fresh);
        var start = output.FindIndex(sample => sample != 0);
        var first = Array.FindIndex(expected, sample => sample != 0);
        Assert(start >= 0 && output.Count - start >= expected.Length - first, "the stream plays a whole stream");
        Assert(Enumerable.Range(0, expected.Length - first).All(index => output[start + index] == expected[first + index]),
            "and it is the second stream, sample for sample: nothing before the reset reached the jitter buffer after it");
        Assert(stream.Counters.Decoded == fresh.Count && stream.Counters.Restarts == 0, "ten frames decoded, and no restart was needed");
        worker.Dispose();
    }

    internal static void TheWishToHearNobodyFollowsThePlayerAndNamesNobody()
    {
        // b mutes the first host's a by name and asks to hear nobody; c only asks to hear nobody. Both move to a second
        // host where four players talk, a tick apart, so it has a datagram to relay in every tick. The engine forgets b's
        // set at a join that finds another host, and the service gives back the wish alone, which names nobody. c's set
        // names nobody, so the service makes it again while the join is under way, and the engine applies it to the
        // session the join finds from its first message: the second host never sends c a datagram.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var second = rig.AddHost("host-2", session: SecondSession.Value);
        var talkers = new[] { "a", "d", "e", "f" }.Select(name => rig.AddClient(name, host: second)).ToArray();
        rig.AddClient("a");
        var b = rig.AddClient("b");
        var c = rig.AddClient("c");
        b.Voice!.SetListenerMutes(new[] { new ParticipantId("a") }, muteAll: true);
        c.Voice!.SetListenerMutes(Array.Empty<ParticipantId>(), muteAll: true);
        for (var index = 0; index < talkers.Length; index++)
        {
            talkers[index].Source = VoiceSource.Speech(4, seed: (uint)(23 + index));
            rig.Step();
        }

        rig.StepFor(500_000);
        rig.Connect(b, second);
        rig.Connect(c, second);
        b.Listen("a");
        c.Listen("a");
        rig.StepFor(1_500_000);
        Assert(new[] { b, c }.All(member => member.Engine.Snapshot.State == MultiplayerSessionState.Ready && member.Engine.Snapshot.Id.Equals(SecondSession)),
            "b and c moved to the second host");

        var mutes = new HashSet<ParticipantId>();
        Assert(b.Voice.GetListenerMutes(mutes) && mutes.Count == 0, "b's set holds the wish alone, naming nobody");
        Assert(c.Voice.GetListenerMutes(mutes) && mutes.Count == 0, "and so does c's");
        Assert(c.Link!.Peer!.Outbound!.ChannelFramesSent[(int)EngineChannel.Voice] == 0, "the second host never sends c a voice datagram");
        Assert(second.Engine.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) > 0, "it skips them when it relays its a");
        foreach (var member in new[] { b, c })
        {
            var heard = member.Heard("a").ToArray();
            Assert(VoiceAudio.LevelDecibels(heard, 0, heard.Length) < -90, member.Name + " hears nobody");
        }

        // b no longer wants that: b hears the second host's a, whom b never muted there.
        b.Voice.SetListenerMutes(Array.Empty<ParticipantId>());
        rig.StepFor(600_000);
        var tail = b.Heard("a").Skip(b.Heard("a").Count - 12_000).ToArray();
        Assert(VoiceAudio.LevelDecibels(tail, 0, tail.Length) > -35, "unmuted, b hears the second host's a");
    }

    internal static void MutesTheEngineKeepsOutliveAnInPlaceReplacement()
    {
        // b mutes a. The host replaces its session in place: same host, same records, same participant ids, so the engine
        // keeps b's set across the new session id, and the service, which keeps no copy of its own, honours and reports it.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        var c = rig.AddClient("c");
        b.Listen("a");
        c.Listen("a");
        b.Voice!.SetListenerMutes(new[] { new ParticipantId("a") });
        rig.Step(5);
        var replaced = new MultiplayerSessionId("voice-service-replaced");
        host.Engine.ReplaceSession(replaced, new SessionSeed(12));
        rig.StepFor(300_000);
        Assert(b.Engine.Snapshot.State == MultiplayerSessionState.Ready && b.Engine.Snapshot.Id.Equals(replaced), "b is in the replacement session");

        a.Source = VoiceSource.Speech(2, seed: 24);
        rig.StepFor(1_200_000);
        var mutes = new HashSet<ParticipantId>();
        Assert(!b.Voice.GetListenerMutes(mutes) && mutes.SetEquals(new[] { new ParticipantId("a") }), "b's set still names a");
        var bHeard = b.Heard("a").ToArray();
        var cTail = c.Heard("a").Skip(c.Heard("a").Count - 24_000).ToArray();
        Assert(VoiceAudio.LevelDecibels(bHeard, 0, bHeard.Length) < -90, "b still hears none of a");
        Assert(VoiceAudio.LevelDecibels(cTail, 0, cTail.Length) > -35, "while c hears a");
        b.Voice.SetListenerMutes(Array.Empty<ParticipantId>());
        rig.StepFor(600_000);
        var bTail = b.Heard("a").Skip(b.Heard("a").Count - 12_000).ToArray();
        Assert(VoiceAudio.LevelDecibels(bTail, 0, bTail.Length) > -35, "unmuted, b hears a");
    }

    internal static void NamesMutedWhileJoiningStayWithTheirSession()
    {
        // b's link moves to a second host. While it joins, b's scoreboard still lists the first host's players, and the
        // player mutes the first host's a. The name is held back until b is Ready, and the join found another session, so
        // it is forgotten: b hears the second host's a, whom it never muted, and that host never learns the name.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var second = rig.AddHost("host-2", session: SecondSession.Value);
        var farA = rig.AddClient("a", host: second);
        rig.AddClient("a");
        var b = rig.AddClient("b");
        var a = new ParticipantId("a");
        rig.Connect(b, second);
        Assert(b.Engine.Snapshot.State == MultiplayerSessionState.Connecting, "b is joining the second host");
        b.Voice!.SetListenerMutes(new[] { a });
        var mutes = new HashSet<ParticipantId>();
        Assert(!b.Voice.GetListenerMutes(mutes) && mutes.SetEquals(new[] { a }), "meanwhile the name shows as muted");
        StepUntilReady(rig, b);
        b.Listen("a");
        farA.Source = VoiceSource.Speech(2, seed: 35);
        rig.StepFor(1_500_000);
        Assert(b.Engine.Snapshot.Id.Equals(SecondSession) && !b.Voice.GetListenerMutes(mutes) && mutes.Count == 0,
            "in the session the join found, the name is forgotten");
        var heard = b.Heard("a").ToArray();
        Assert(VoiceAudio.LevelDecibels(heard, heard.Length / 2, heard.Length / 2) > -35, "and b hears the second host's a");
        Assert(second.Engine.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 0, "which never learned whom b had muted");

        // b's link to the second host drops, and the player mutes its a while it is down. b comes back to the same session,
        // so the held name is given to the engine there, and b hears no more of that a.
        rig.Network.Sever(b.Link!);
        for (var step = 0; step < 50 && b.Engine.Snapshot.State == MultiplayerSessionState.Ready; step++) rig.Step();
        b.Voice.SetListenerMutes(new[] { a });
        rig.Connect(b, second);
        StepUntilReady(rig, b);
        var mark = b.Heard("a").Count;
        farA.Source = VoiceSource.Speech(2, seed: 36);
        rig.StepFor(1_500_000);
        Assert(!b.Voice.GetListenerMutes(mutes) && mutes.SetEquals(new[] { a }), "back in the session it was given in, the name applies");
        var silent = b.Heard("a").Skip(mark).ToArray();
        Assert(VoiceAudio.LevelDecibels(silent, 0, silent.Length) < -90 && second.Engine.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) > 0,
            "b hears none of a, whom the host now leaves out");
    }

    private static void StepUntilReady(VoiceServiceRig rig, VoiceServiceRig.Member member)
    {
        for (var step = 0; step < 100 && member.Engine.Snapshot.State != MultiplayerSessionState.Ready; step++) rig.Step();
        Assert(member.Engine.Snapshot.State == MultiplayerSessionState.Ready, member.Name + " is Ready");
    }

    private static List<byte[]> Encode(OpusVoiceCodec codec, uint seed)
    {
        var encoder = codec.CreateEncoder();
        var signal = VoiceTestSignal.Voiced(10 * 960, seed);
        var packet = new byte[OpusVoiceCodec.MaximumPacketLength];
        var packets = new List<byte[]>();
        for (var frame = 0; frame < 10; frame++)
        {
            var length = encoder.Encode(signal.AsSpan(frame * 960, 960), packet);
            packets.Add(packet.AsSpan(0, length).ToArray());
        }

        return packets;
    }

    private static float[] Decode(OpusVoiceCodec codec, List<byte[]> packets)
    {
        var decoder = codec.CreateDecoder();
        var pcm = new short[960];
        var output = new List<float>();
        foreach (var packet in packets)
        {
            decoder.Decode(packet, pcm);
            output.AddRange(pcm.Select(sample => sample * (1f / 32_768f)));
        }

        return output.ToArray();
    }

    private static void Add(VoiceInboundBatch batch, ParticipantId speaker, ulong sequence, ulong timestamp, byte[] payload, long arrival)
    {
        var frame = new VoiceFrame(speaker, 2, sequence, timestamp, VoiceFrameFlags.None, new ArraySegment<byte>(payload), arrival);
        if (!batch.TryAddFrame(frame)) throw new InvalidOperationException("The batch is full.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
