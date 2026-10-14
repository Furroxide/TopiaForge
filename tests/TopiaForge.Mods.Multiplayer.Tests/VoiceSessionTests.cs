using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The engine's voice path end to end over the virtual transport: who a frame is from, who may not pretend otherwise,
/// how abuse escalates, how server and listener mutes stop the relay, and how role changes start voice afresh.
/// </summary>
internal static class VoiceSessionTests
{
    internal static void SpeakersAreStampedByTheHost()
    {
        using var rig = new VoiceSessionHarness();
        var (a, _) = rig.AddClient("a");
        var (b, _) = rig.AddClient("b");
        var ears = new Dictionary<string, VoiceSessionHarness.Listener>
        {
            ["host"] = rig.Listen(rig.Host),
            ["a"] = rig.Listen(a),
            ["b"] = rig.Listen(b)
        };
        var bases = new Dictionary<string, short> { ["host"] = 1000, ["a"] = 2000, ["b"] = 3000 };
        rig.Talk(rig.Host, bases["host"], 20);
        rig.Talk(a, bases["a"], 20);
        rig.Talk(b, bases["b"], 20);
        rig.Advance(40);
        rig.Flush();

        foreach (var (listener, heard) in ears)
        {
            Assert(heard.From(listener).Count == 0, listener + " never hears itself");
            foreach (var speaker in bases.Keys.Where(name => name != listener))
            {
                var frames = heard.From(speaker);
                Assert(frames.Select(frame => frame.Id).SequenceEqual(Enumerable.Range(bases[speaker], 20).Select(id => (short)id)),
                    listener + " hears all of " + speaker + "'s frames, as " + speaker + ", in order");
                Assert(frames.Select(frame => frame.Sequence).SequenceEqual(Enumerable.Range(1, 20).Select(value => (ulong)value)) &&
                       frames.Select(frame => frame.Timestamp).SequenceEqual(Enumerable.Range(0, 20).Select(value => (ulong)value)),
                    "with the talker's sequences and timestamps");
                Assert(frames[^1].Flags == VoiceFrameFlags.EndOfTalk && frames.Take(19).All(frame => frame.Flags == VoiceFrameFlags.None),
                    "and its flags");
            }
        }

        var statistics = rig.Host.GetVoiceStatistics();
        Assert(statistics.Relayed == 20 && statistics.Sent == 40 && statistics.TotalDropped == 0,
            "the host relayed each client's 10 datagrams to the other, and sent its own 10 to both");
        Assert(statistics.Delivered == 40 && a.GetVoiceStatistics().Delivered == 40, "and played both clients' 40 frames itself");
    }

    internal static void AClientCannotSpeakAsSomeoneElse()
    {
        using var rig = new VoiceSessionHarness();
        var (a, _) = rig.AddClient("a");
        var aEars = rig.Listen(a);
        var hostEars = rig.Listen(rig.Host);
        var pair = rig.Network.Connect("mallory");
        rig.Host.Accept(pair.HostEnd);
        var mallory = new RawPeer(pair.ClientEnd);
        mallory.Join("Mallory");
        rig.Flush();
        mallory.Send((frame, body) => SessionMessages.WriteEpochMessage(frame, body, MessageType.ReadyAck, 0));
        rig.Flush();
        var roster = mallory.Receive().Where(message => message.Type == MessageType.VoiceRoster)
            .Select(message => VoiceMessages.ReadRoster(message.Reader())).Last();
        var aIndex = roster.Single(entry => entry.ParticipantId == "a").Speaker;
        var malloryIndex = roster.Single(entry => entry.ParticipantId == "mallory").Speaker;

        // A datagram in the relayed format, naming a, is refused on arrival.
        var forged = Datagram(rig.Codec, aIndex, 1, 0, 6_666);
        pair.ClientEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(forged));
        // Mallory's own voice is relayed, stamped with her own index.
        var honest = Datagram(rig.Codec, 0, 2, 1, 7_777);
        pair.ClientEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(honest));
        rig.Flush();

        Assert(aEars.Frames.Count == 1 && aEars.Frames[0] is { Speaker: "mallory", Id: 7_777 }, "a hears Mallory as Mallory, once");
        Assert(hostEars.Frames.Count == 1 && hostEars.Frames[0].Speaker == "mallory", "and so does the host");
        Assert(aEars.Frames.All(frame => frame.Id != 6_666), "the forged frame reached nobody");
        var statistics = rig.Host.GetVoiceStatistics();
        Assert(statistics.Dropped(VoiceDropReason.Malformed) == 1 && statistics.Strikes == 1, "the forgery cost Mallory a strike");
        Assert(aIndex != malloryIndex, "every participant has a speaker index of their own");
    }

    internal static void AClientRefusesFramesItsRosterDoesNotExplain()
    {
        var codec = new FakeVoiceCodec();
        var network = new VirtualEngineTransport(new VirtualLinkConditions());
        var pair = network.Connect("client");
        var client = SessionEngine.CreateClient(
            new EngineOptions { StepRateHz = 60, Voice = new VoiceOptions(codec), CallbackFailures = EngineCallbackFailureMode.Propagate },
            new MultiplayerSessionId("placeholder"),
            0);
        client.Connect(pair.ClientEnd, "Client");
        var host = new RawPeer(pair.HostEnd);
        AcceptRaw(host, (frame, body) =>
            VoiceMessages.WriteRoster(frame, body, new[] { new VoiceRosterEntry(5, "friend", false), new VoiceRosterEntry(6, "client", false) }));
        var tick = 0UL;
        void Pump()
        {
            tick++;
            network.Now = tick;
            network.DeliverDue(tick);
            client.Pump((long)(tick * 1_000_000UL / 60));
        }

        Pump();
        Pump();
        Assert(client.Snapshot.State == MultiplayerSessionState.Ready, "the client joined the raw host");
        foreach (var speaker in new ulong[] { 5, 9, 6 })
        {
            pair.HostEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(Datagram(codec, speaker, speaker, speaker, (short)(speaker * 100))));
        }

        var notRelayed = codec.Packet(42);
        var uplinkFormat = new byte[VoicePacket.MaximumBytes(codec.MaximumPacketBytes)];
        var length = VoicePacket.Write(uplinkFormat, 0, 9, 9, VoiceFrameFlags.None, notRelayed, VoiceFrameFlags.None, ReadOnlySpan<byte>.Empty);
        pair.HostEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(uplinkFormat, 0, length));
        Pump();
        Pump();
        var heard = new List<VoiceFrame>();
        while (client.TryReceiveVoiceFrame(out var frame)) heard.Add(frame);
        Assert(heard.Count == 1 && heard[0].Speaker.Value == "friend", "only the frame from a speaker in the roster is kept");
        var statistics = client.GetVoiceStatistics();
        Assert(statistics.Dropped(VoiceDropReason.UnknownSpeaker) == 2, "an unknown index and the client's own index are refused");
        Assert(statistics.Dropped(VoiceDropReason.Malformed) == 1, "an unstamped frame from the host is malformed");
        client.Shutdown(MultiplayerSessionEndReason.Left);
    }

    internal static void FloodingMutesThenRemovesTheSpeaker()
    {
        using var rig = new VoiceSessionHarness();
        var (a, aLink) = rig.AddClient("a");
        var (b, _) = rig.AddClient("b");
        var bEars = rig.Listen(b);
        var aId = new ParticipantId("a");
        ulong next = 0;

        // A modified client floods at 240 datagrams a second, where 30 are allowed; an unmodified engine paces itself and
        // cannot. The excess is dropped at once, but strikes only once the flood has outlasted the sustained allowance.
        var ticks = rig.FloodUntilMuted(aLink, ref next);
        rig.Flush();
        var statistics = rig.Host.GetVoiceStatistics();
        Assert(ticks >= 90 && ticks <= 130, "a sustained flood mutes a after about 1.7 seconds (" + ticks + " ticks)");
        Assert(statistics.AutoMutes == 1 && statistics.Strikes == 20 && statistics.Dropped(VoiceDropReason.RateLimited) > 200,
            "the excess was dropped, and only its last twenty datagrams struck");
        Assert(statistics.Relayed >= 75 && statistics.Relayed <= 90, "a second of burst and then thirty a second were relayed (" + statistics.Relayed + ")");
        Assert(bEars.From("a").Count == statistics.Relayed, "b heard exactly what was relayed");
        Assert(b.IsServerMuted(aId) && a.IsServerMuted(aId) && rig.Host.IsServerMuted(aId), "everyone learns that a is muted");
        Assert(a.SubmitVoiceFrame(0, VoiceFrameFlags.None, rig.Codec.Packet(1)) == VoiceSubmitResult.ServerMuted,
            "a muted client stops sending");
        Assert(rig.Diagnostics.Count(EngineDiagnosticKind.VoiceAbuse) == 1, "the host reports the automatic mute");

        rig.Advance(31 * 60);
        rig.Flush();
        Assert(!b.IsServerMuted(aId) && !a.IsServerMuted(aId), "after thirty seconds the mute ends, and the roster says so");

        Assert(rig.FloodUntilMuted(aLink, ref next) > 0, "a second flood mutes a again");
        rig.Flush();
        Assert(rig.Host.GetVoiceStatistics().AutoMutes == 2 && !a.IsEnded, "a stays after a second automatic mute");
        rig.Advance(31 * 60);
        rig.Flush();
        rig.FloodUntilMuted(aLink, ref next);
        rig.Flush();
        statistics = rig.Host.GetVoiceStatistics();
        Assert(statistics.Kicks == 1 && a.IsEnded && a.Snapshot.EndReason == MultiplayerSessionEndReason.Kicked,
            "a third automatic mute within ten minutes removes a from the session");
        Assert(rig.Diagnostics.Count(EngineDiagnosticKind.VoiceAbuse) == 3 && !b.IsEnded, "the host reports it, and b stays");
    }

    internal static void MutesStopTheRelay()
    {
        using var rig = new VoiceSessionHarness();
        var (a, aLink) = rig.AddClient("a");
        var (b, bLink) = rig.AddClient("b");
        var (c, _) = rig.AddClient("c");
        var bEars = rig.Listen(b);
        var cEars = rig.Listen(c);
        var hostEars = rig.Listen(rig.Host);
        var aId = new ParticipantId("a");
        ulong timestamp = 0;
        int Talk(int frames)
        {
            var sent = 0;
            for (var index = 0; index < frames; index++, timestamp++)
            {
                var result = a.SubmitVoiceFrame(timestamp, VoiceFrameFlags.None, rig.Codec.Packet((short)(2000 + (short)timestamp)));
                if (result is VoiceSubmitResult.Sent or VoiceSubmitResult.Held) sent++;
                rig.Advance();
            }

            rig.Flush();
            return sent;
        }

        // A server mute: a stops sending, and anything a sends anyway is dropped on arrival.
        Assert(rig.Host.SetServerMute(aId, true), "the host mutes a");
        rig.Flush();
        Assert(a.IsServerMuted(aId) && b.IsServerMuted(aId), "a and b learn of it");
        Assert(Talk(4) == 0, "a's engine sends nothing while muted");
        aLink.Send(EngineChannel.Voice, new ArraySegment<byte>(Datagram(rig.Codec, 0, 100, 100, 2_500)));
        rig.Flush();
        Assert(rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.ServerMuted) == 1, "a datagram sent regardless is dropped on arrival");
        Assert(bEars.From("a").Count == 0 && cEars.From("a").Count == 0 && hostEars.From("a").Count == 0, "nobody heard a");
        Assert(!rig.Host.SetServerMute(new ParticipantId("nobody"), true), "muting an unknown participant does nothing");
        rig.Host.SetServerMute(aId, false);
        rig.Flush();
        Assert(Talk(10) == 10 && bEars.From("a").Count == 10 && cEars.From("a").Count == 10, "unmuted, a is heard again");

        // Listener mutes: b no longer wants a, so the host stops relaying a to b, and only to b.
        b.SetListenerMutes(new[] { aId });
        rig.Flush();
        var before = rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted);
        Talk(10);
        Assert(bEars.From("a").Count == 10 && cEars.From("a").Count == 20, "b hears no more of a; c still does");
        Assert(rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == before + 5, "the host skipped b for a's 5 datagrams");

        rig.Host.SetListenerMutes(new[] { aId });
        c.SetListenerMutes(Array.Empty<ParticipantId>(), muteAll: true);
        rig.Flush();
        var hostHeard = hostEars.From("a").Count;
        Talk(10);
        Assert(hostEars.From("a").Count == hostHeard, "the host's own player can mute a too");
        Assert(cEars.From("a").Count == 20, "muting everyone silences everyone");

        // b reconnects over a new link; its mute set goes with it.
        rig.Network.Sever(bLink);
        rig.Flush();
        rig.Connect(b, "b");
        rig.Flush();
        Assert(b.Snapshot.State == MultiplayerSessionState.Ready, "b is back");
        before = rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted);
        Talk(10);
        Assert(bEars.From("a").Count == 10, "after reconnecting, b still hears none of a");
        Assert(rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == before + 15,
            "because the host still skips b, as well as c and its own player, for each of a's 5 datagrams");
    }

    internal static void AnEngineWithoutVoiceSendsNothingForIt()
    {
        // Without voice options a client sends neither voice messages nor voice datagrams, whatever it is asked to do,
        // and ignores what a voice-enabled host sends it.
        var codec = new FakeVoiceCodec();
        var network = new VirtualEngineTransport(new VirtualLinkConditions());
        var pair = network.Connect("client");
        var client = SessionEngine.CreateClient(
            new EngineOptions { StepRateHz = 60, CallbackFailures = EngineCallbackFailureMode.Propagate },
            new MultiplayerSessionId("placeholder"),
            0);
        client.SetListenerMutes(new[] { new ParticipantId("friend") });
        client.Connect(pair.ClientEnd, "Client");
        var host = new RawPeer(pair.HostEnd);
        AcceptRaw(host, (frame, body) => VoiceMessages.WriteRoster(frame, body, new[] { new VoiceRosterEntry(5, "friend", false) }));
        var tick = 0UL;
        void Pump()
        {
            tick++;
            network.Now = tick;
            network.DeliverDue(tick);
            client.Pump((long)(tick * 1_000_000UL / 60));
        }

        Pump();
        Pump();
        Assert(client.Snapshot.State == MultiplayerSessionState.Ready, "the client joined the raw host");
        client.SetListenerMutes(Array.Empty<ParticipantId>(), muteAll: true);
        Assert(client.SubmitVoiceFrame(0, VoiceFrameFlags.None, codec.Packet(1)) == VoiceSubmitResult.Disabled, "a talker is told voice is off");
        pair.HostEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(Datagram(codec, 5, 1, 0, 500)));
        Pump();
        Pump();
        Assert(host.Receive().All(message => message.Type != MessageType.VoiceMutes), "the mute set never leaves");
        Assert(pair.ClientEnd.Outbound!.ChannelFramesSent[(int)EngineChannel.Voice] == 0, "and no voice datagram does");
        Assert(!client.TryReceiveVoiceFrame(out _) && client.GetVoiceStatistics().TotalDropped == 0, "relayed voice is ignored, not judged");
        client.Shutdown(MultiplayerSessionEndReason.Left);
    }

    internal static void ARestartedClientIsHeardAtOnce()
    {
        // A player whose game restarts rejoins within the grace period as the same participant, from a new engine whose
        // sequences start at 1 again. The host forgets the old stream on the rejoin, so the new one is heard at once.
        using var rig = new VoiceSessionHarness();
        var (a, aLink) = rig.AddClient("a");
        var hostEars = rig.Listen(rig.Host);
        rig.Talk(a, 2000, 40);
        rig.Advance(60);
        rig.Flush();
        Assert(hostEars.From("a").Count == 40, "the host hears the first run");
        rig.Network.Sever(aLink);
        rig.Flush();
        var (restarted, _) = rig.AddClient("a");
        rig.Talk(restarted, 3000, 10);
        rig.Advance(20);
        rig.Flush();
        var second = hostEars.From("a").Skip(40).ToList();
        Assert(second.Select(frame => frame.Id).SequenceEqual(Enumerable.Range(3000, 10).Select(id => (short)id)) && second[0].Sequence == 1,
            "the restarted client is heard from its first frame, sequence 1");
        Assert(rig.Host.GetVoiceStatistics().TotalDropped == 0, "and nothing it sent was dropped as stale");
    }

    internal static void VoiceStartsAfreshAcrossRoleChanges()
    {
        // A traveller talks in solo play, joins the host, and leaves for solo play again. Each session's voice starts
        // afresh: the talker's timestamps may start again, and nothing heard in the joined session is left to play.
        using var rig = new VoiceSessionHarness();
        var hostEars = rig.Listen(rig.Host);
        var traveller = rig.Track(SessionEngine.CreateHost(rig.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("solo"),
            new SessionSeed(3), new ParticipantId("traveller"), "Traveller", rig.NowMicros));
        var solo = Enumerable.Range(0, 10).Select(ts => traveller.SubmitVoiceFrame((ulong)ts, VoiceFrameFlags.None, rig.Codec.Packet((short)(100 + ts)))).ToList();
        Assert(solo.All(result => result is VoiceSubmitResult.Sent or VoiceSubmitResult.Held), "a solo player may talk, to nobody");

        traveller.SwitchToClient(new MultiplayerSessionId("joining"));
        rig.Connect(traveller, "traveller");
        rig.Flush();
        Assert(traveller.Snapshot.State == MultiplayerSessionState.Ready, "the traveller joins the host");
        rig.Talk(rig.Host, 900, 6);
        var talker = rig.Talk(traveller, 500, 6);
        rig.Advance(12);
        rig.Flush();
        Assert(talker.Results.All(result => result is VoiceSubmitResult.Sent or VoiceSubmitResult.Held), "timestamps start again in the joined session");
        Assert(hostEars.From("traveller").Select(frame => frame.Id).SequenceEqual(Enumerable.Range(500, 6).Select(id => (short)id)),
            "and the host hears every frame");
        Assert(traveller.GetVoiceStatistics().Delivered == 6, "the host's six frames wait in the traveller's queue");

        traveller.SwitchToSolo(new SoloSessionStart(new MultiplayerSessionId("solo-again"), new SessionSeed(4), new ParticipantId("traveller"), "Traveller"));
        Assert(!traveller.TryReceiveVoiceFrame(out _), "leaving drops what the joined session left to play");
        Assert(traveller.SubmitVoiceFrame(0, VoiceFrameFlags.None, rig.Codec.Packet(1)) == VoiceSubmitResult.Held &&
               traveller.SubmitVoiceFrame(1, VoiceFrameFlags.None, rig.Codec.Packet(2)) == VoiceSubmitResult.Sent,
            "and solo play's timestamps start again too");
        rig.Flush();
        Assert(hostEars.From("traveller").Count == 6, "nothing more reaches the host");
    }

    /// <summary>Has a raw host accept the client: join, an empty snapshot, anything <paramref name="extra"/> writes, and a commit.</summary>
    private static void AcceptRaw(RawPeer host, Action<ProtocolWriter, ProtocolWriter> extra) =>
        host.Send((frame, body) =>
        {
            JoinMessages.WriteJoinAccepted(frame, body, new JoinAcceptedMessage
            {
                SessionId = "raw-session",
                Seed = 1,
                ParticipantId = "client",
                StepRateHz = 60,
                NextSequence = 1
            });
            SessionMessages.WriteEpochMessage(frame, body, MessageType.SnapshotBegin, 0);
            SessionMessages.WriteEmpty(frame, body, MessageType.SnapshotEnd);
            extra(frame, body);
            SessionMessages.WriteCommit(frame, body, 0, 0);
        });

    internal static byte[] Datagram(FakeVoiceCodec codec, ulong speaker, ulong sequence, ulong timestamp, short id)
    {
        var buffer = new byte[VoicePacket.MaximumBytes(codec.MaximumPacketBytes)];
        var length = VoicePacket.Write(buffer, speaker, sequence, timestamp, VoiceFrameFlags.None, codec.Packet(id), VoiceFrameFlags.None, ReadOnlySpan<byte>.Empty);
        return buffer.AsSpan(0, length).ToArray();
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
