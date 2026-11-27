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
        // after those frames, and so does b's stream for the first host's a, since an id names a player only on the host
        // that gave it. So b plays none of the session it left, and hears the second host's a as usual, on a new stream.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var second = rig.AddHost("host-2", session: SecondSession.Value);
        var farA = rig.AddClient("a", host: second);
        farA.Source = VoiceSource.Speech(2, seed: 21);
        rig.StepFor(2_100_000);
        var nearA = rig.AddClient("a");
        var b = rig.AddClient("b");
        var nearStream = b.Listen("a");
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
        Assert(nearStream.IsEnded && VoiceAudio.LevelDecibels(quiet, 0, quiet.Length) < -90, "b plays none of the session it left");

        // The stalled worker caught up five slots at once when it resumed, which leaves up to 100 ms more in the stream
        // until drift control trims it, a frame a second; hence a looser bound on the delay here than elsewhere.
        var listenAt = rig.NowMicros;
        var farStream = b.Listen("a");
        Assert(!ReferenceEquals(farStream, nearStream) && !farStream.IsEnded, "the second host's a is on a stream of its own");
        var talkAt = rig.NowMicros;
        farA.Source = VoiceSource.Speech(3, seed: 23);
        rig.StepFor(2_000_000);
        var output = b.Heard("a").Skip((int)((talkAt - listenAt) * 48 / 1_000)).ToArray();
        var (delay, correlation) = VoiceAudio.FindDelay(farA.Source.Signal, output, 24_000, 48_000, 19_200);
        Assert(correlation >= 0.9 && delay <= MaximumDelaySamples + 4_800,
            "b hears the second host's a (" + (delay / 48) + " ms, correlation " + correlation.ToString("F3") + ")");
        var after = VoiceServiceTests.Counters(b, "a");
        Assert(after.TargetDelayMicros <= 80_000 && after.Late == 0 && before.Late == 0,
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

    internal static void AClientWhoseSessionIsReplacedInPlaceDropsItsJitterBuffersButKeepsItsStreams()
    {
        // The host replaces its session in place and keeps every record, so its ids still name the same players: b keeps
        // its stream for a, and only the jitter buffers go, at the new session.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        var stream = b.Listen("a");
        a.Source = VoiceSource.Speech(3, seed: 26);
        rig.StepFor(1_000_000);
        Assert(VoiceServiceTests.Counters(b, "a").Resets == 0, "b hears a");
        var replaced = new MultiplayerSessionId("voice-service-replaced");
        host.Engine.ReplaceSession(replaced, new SessionSeed(13));
        rig.StepFor(1_000_000);
        Assert(b.Engine.Snapshot.Id.Equals(replaced) && b.Engine.Snapshot.State == MultiplayerSessionState.Ready, "b is Ready in the new session");
        Assert(!stream.IsEnded && ReferenceEquals(b.Voice!.GetPlaybackStream(a.Id), stream), "b kept its stream for a");
        var counters = VoiceServiceTests.Counters(b, "a");
        Assert(counters.Resets >= 1 && counters.Decoded > 80, "b dropped a's jitter buffer at the new session (" + counters.Resets + " resets) and hears a on");
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
        for (var step = 0; step < 100 && !new[] { b, c }.All(member => member.Engine.Snapshot.State == MultiplayerSessionState.Ready); step++) rig.Step();
        Assert(new[] { b, c }.All(member => member.Engine.Snapshot.State == MultiplayerSessionState.Ready && member.Engine.Snapshot.Id.Equals(SecondSession)),
            "b and c moved to the second host");

        // An id names a player only on the host that gave it, so each listens to the second host's a from here.
        b.Listen("a");
        c.Listen("a");
        rig.StepFor(1_500_000);

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

    internal static void NamesMutedWhileJoiningStayWithTheirHost()
    {
        // b's link moves to a second host. While it joins, b's scoreboard still lists the first host's players, and the
        // player mutes the first host's a. The name belongs to the first host, and the join found another, so it is
        // forgotten: b hears the second host's a, whom it never muted, and that host never learns the name.
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

        // b's link to the second host drops, and the player mutes its a while it is down. b comes back to the same host,
        // which kept b's record, so the name holds there, and b hears no more of that a.
        rig.Network.Sever(b.Link!);
        for (var step = 0; step < 50 && b.Engine.Snapshot.State == MultiplayerSessionState.Ready; step++) rig.Step();
        b.Voice.SetListenerMutes(new[] { a });
        rig.Connect(b, second);
        StepUntilReady(rig, b);
        var mark = b.Heard("a").Count;
        farA.Source = VoiceSource.Speech(2, seed: 36);
        rig.StepFor(1_500_000);
        Assert(!b.Voice.GetListenerMutes(mutes) && mutes.SetEquals(new[] { a }), "back with the host it was given for, the name applies");
        var silent = b.Heard("a").Skip(mark).ToArray();
        Assert(VoiceAudio.LevelDecibels(silent, 0, silent.Length) < -90 && second.Engine.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) > 0,
            "b hears none of a, whom the host now leaves out");
    }

    internal static void MutesMadeWhileTheLinkIsDownSurviveAnInPlaceReplacement()
    {
        // b mutes a. b's link drops, and while it is down the player mutes c as well. Before b is back the host replaces
        // its session in place: same host, same records, same ids, so b comes back into the new session with both mutes,
        // as a client that stayed connected would, and the mute it made long before is not lost either.
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        var c = rig.AddClient("c");
        var aId = new ParticipantId("a");
        var cId = new ParticipantId("c");
        b.Listen("a");
        b.Listen("c");
        b.Voice!.SetListenerMutes(new[] { aId });
        rig.Step(5);
        rig.Network.Sever(b.Link!);
        for (var step = 0; step < 50 && b.Engine.Snapshot.State == MultiplayerSessionState.Ready; step++) rig.Step();
        b.Voice.SetListenerMutes(new[] { aId, cId });
        var mutes = new HashSet<ParticipantId>();
        Assert(b.Engine.Snapshot.State == MultiplayerSessionState.Connecting && !b.Voice.GetListenerMutes(mutes) && mutes.SetEquals(new[] { aId, cId }),
            "while b's link is down, its set names a and c");
        var first = new MultiplayerSessionId("voice-service-replaced");
        host.Engine.ReplaceSession(first, new SessionSeed(12));
        rig.Step(3);
        rig.Connect(b);
        StepUntilReady(rig, b);
        Assert(b.Engine.Snapshot.Id.Equals(first) && !b.Voice.GetListenerMutes(mutes) && mutes.SetEquals(new[] { aId, cId }),
            "back in the replaced session, b's set still names a and c");
        var mark = b.Heard("a").Count;
        var dropped = host.Engine.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted);
        a.Source = VoiceSource.Speech(1.5, seed: 37);
        c.Source = VoiceSource.Speech(1.5, seed: 38);
        rig.StepFor(1_200_000);
        foreach (var speaker in new[] { "a", "c" })
        {
            var heard = b.Heard(speaker).Skip(mark).ToArray();
            Assert(VoiceAudio.LevelDecibels(heard, 0, heard.Length) < -90, "b hears none of " + speaker);
        }

        Assert(host.Engine.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) > dropped, "whom the host leaves out for b");

        // b's link stalls; the player unmutes c while it is down, and the host replaces the session again. The replacement
        // reaches b before its link reports open again, so b is never Ready in the session it stalled in. Its set still
        // names a alone, the people are still the same, and b hears c again but none of a.
        Stall(b, stalled: true);
        for (var step = 0; step < 20 && b.Engine.Snapshot.State != MultiplayerSessionState.Reconnecting; step++) rig.Step();
        Assert(b.Engine.Snapshot.State == MultiplayerSessionState.Reconnecting, "b's link stalls");
        b.Voice.SetListenerMutes(new[] { aId });
        var second = new MultiplayerSessionId("voice-service-replaced-again");
        host.Engine.ReplaceSession(second, new SessionSeed(13));
        rig.Step(3);
        foreach (var end in new[] { b.Link!, b.Link!.Peer! }) end.Outbound!.Held = false;
        for (var step = 0; step < 50 && !b.Engine.Snapshot.Id.Equals(second); step++) rig.Step();
        Stall(b, stalled: false);
        StepUntilReady(rig, b);
        Assert(b.Engine.Snapshot.Id.Equals(second) && !b.Voice.GetListenerMutes(mutes) && mutes.SetEquals(new[] { aId }),
            "in the session replaced while b's link stalled, b's set names a alone");
        mark = b.Heard("a").Count;
        a.Source = VoiceSource.Speech(1.5, seed: 39);
        c.Source = VoiceSource.Speech(1.5, seed: 40);
        rig.StepFor(1_200_000);
        var fromA = b.Heard("a").Skip(mark).ToArray();
        var fromC = b.Heard("c").Skip(mark).ToArray();
        Assert(VoiceAudio.LevelDecibels(fromA, 0, fromA.Length) < -90 && VoiceAudio.LevelDecibels(fromC, fromC.Length / 2, fromC.Length / 2) > -35,
            "b hears c again, and none of a");
    }

    internal static void MutesMadeWhileTheLinkIsDownNeverReachAnotherHost()
    {
        // b's link stalls, and the player mutes the first host's a while it is down. Then b's link moves to a second host,
        // which has an a of its own: the name stays behind with the first host, and b hears the second host's a. d's link
        // drops, and d asks to hear nobody while it is down. That names nobody, so it holds in whatever session d joins
        // next, from its first message: the second host, where four players talk a tick apart so it has a datagram to
        // relay in every tick, never sends d one.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var second = rig.AddHost("host-2", session: SecondSession.Value);
        var talkers = new[] { "a", "e", "f", "g" }.Select(name => rig.AddClient(name, host: second)).ToArray();
        rig.AddClient("a");
        var b = rig.AddClient("b");
        var d = rig.AddClient("d");
        Stall(b, stalled: true);
        for (var step = 0; step < 20 && b.Engine.Snapshot.State != MultiplayerSessionState.Reconnecting; step++) rig.Step();
        b.Voice!.SetListenerMutes(new[] { new ParticipantId("a") });
        rig.Network.Sever(d.Link!);
        for (var step = 0; step < 50 && d.Engine.Snapshot.State == MultiplayerSessionState.Ready; step++) rig.Step();
        d.Voice!.SetListenerMutes(Array.Empty<ParticipantId>(), muteAll: true);
        for (var index = 0; index < talkers.Length; index++)
        {
            talkers[index].Source = VoiceSource.Speech(3, seed: (uint)(41 + index));
            rig.Step();
        }

        rig.Step(10);
        rig.Connect(b, second);
        rig.Connect(d, second);
        StepUntilReady(rig, b);
        StepUntilReady(rig, d);
        b.Listen("a");
        rig.StepFor(1_500_000);
        var mutes = new HashSet<ParticipantId>();
        Assert(b.Engine.Snapshot.Id.Equals(SecondSession) && !b.Voice.GetListenerMutes(mutes) && mutes.Count == 0,
            "with the second host, b's set names nobody");
        var heard = b.Heard("a").ToArray();
        Assert(VoiceAudio.LevelDecibels(heard, heard.Length / 2, heard.Length / 2) > -35, "and b hears the second host's a");
        Assert(d.Voice.GetListenerMutes(mutes) && mutes.Count == 0, "d's set holds the wish alone");
        Assert(d.Link!.Peer!.Outbound!.ChannelFramesSent[(int)EngineChannel.Voice] == 0, "and the second host never sends d a voice datagram");
    }

    internal static void NamesGivenWithNoHostJoinedNameNobody()
    {
        // t names a while it joins for the first time, before it has seen anyone. b's link drops, b goes back to solo
        // play and sets out to join again, and names a, a name its solo session never showed. Both names belong to no
        // host: each join forgets them, even b's, which finds the host that kept its record, so both hear a.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var a = rig.AddClient("a");
        var b = rig.AddClient("b");
        var aId = new ParticipantId("a");
        var t = rig.AddClient("t", untilReady: false);
        t.Voice!.SetListenerMutes(new[] { aId });
        StepUntilReady(rig, t);
        rig.Network.Sever(b.Link!);
        for (var step = 0; step < 50 && b.Engine.Snapshot.State == MultiplayerSessionState.Ready; step++) rig.Step();
        b.Engine.SwitchToSolo(new SoloSessionStart(new MultiplayerSessionId("voice-solo"), new SessionSeed(5), new ParticipantId("b"), "B"));
        rig.Step();
        b.Engine.SwitchToClient(new MultiplayerSessionId("voice-joining"));
        b.Voice!.SetListenerMutes(new[] { aId });
        rig.Connect(b);
        StepUntilReady(rig, b);
        t.Listen("a");
        b.Listen("a");
        a.Source = VoiceSource.Speech(2, seed: 42);
        rig.StepFor(1_500_000);
        var mutes = new HashSet<ParticipantId>();
        foreach (var member in new[] { t, b })
        {
            Assert(!member.Voice!.GetListenerMutes(mutes) && mutes.Count == 0, member.Name + "'s set names nobody");
            var heard = member.Heard("a").ToArray();
            Assert(VoiceAudio.LevelDecibels(heard, heard.Length / 2, heard.Length / 2) > -35, "and " + member.Name + " hears a");
        }
    }

    internal static void NamesGivenBeforeTheNewHostListsItsPlayersNameNobodyThere()
    {
        // The second host's world is large, so over a 64 kB/s link its answer to b's join spans two frames, with its
        // participant list in the second. In between, b's session names the second host's session and lists nobody, and
        // the player mutes the a it saw with the first host. That a is nobody with the second host: the name goes at once,
        // b hears the second host's a, and that host never learns the name.
        using var rig = new VoiceServiceRig(new VirtualLinkConditions { LatencyTicks = 1, TicksPerSecond = 100, BandwidthBytesPerSecond = 64_000 });
        rig.AddHost();
        rig.AddClient("a");
        var b = rig.AddClient("b");
        var second = rig.AddHost("host-2", session: SecondSession.Value);
        VoiceMuteTests.FillWorld(second.Engine.CreateFacade("world"));
        var farA = rig.AddClient("a", host: second, untilReady: false);
        for (var step = 0; step < 400 && farA.Engine.Snapshot.State != MultiplayerSessionState.Ready; step++) rig.Step();
        rig.Connect(b, second);
        for (var step = 0; step < 400 && !b.Engine.Snapshot.Id.Equals(SecondSession); step++) rig.Step();
        Assert(b.Engine.Snapshot.State == MultiplayerSessionState.Synchronizing && b.Engine.Snapshot.Participants.Count == 0,
            "b is joining the second host, and its session lists nobody yet");
        b.Voice!.SetListenerMutes(new[] { new ParticipantId("a") });
        var mutes = new HashSet<ParticipantId>();
        Assert(!b.Voice.GetListenerMutes(mutes) && mutes.Count == 0, "the name goes at once");
        StepUntilReady(rig, b);
        b.Listen("a");
        farA.Source = VoiceSource.Speech(2, seed: 43);
        rig.StepFor(1_500_000);
        var heard = b.Heard("a").ToArray();
        Assert(VoiceAudio.LevelDecibels(heard, heard.Length / 2, heard.Length / 2) > -35, "b hears the second host's a");
        Assert(second.Engine.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 0, "which never learned the name");
    }

    /// <summary>Stalls or restores a member's link both ways, holding what is sent meanwhile, as the rig's Interrupt does.</summary>
    private static void Stall(VoiceServiceRig.Member member, bool stalled)
    {
        foreach (var end in new[] { member.Link!, member.Link!.Peer! })
        {
            if (end.State != EngineLinkState.Closed) end.State = stalled ? EngineLinkState.Stalled : EngineLinkState.Open;
            end.Outbound!.Held = stalled;
        }
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
