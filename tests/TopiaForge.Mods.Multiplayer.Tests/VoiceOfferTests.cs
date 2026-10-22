using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A session that offers no voice chat carries none, over the rig's virtual transport. Its host drops every voice datagram
/// as it arrives, with no strike, relays nothing, and its own player sends nothing. Each joining player hears the setting
/// in JoinAccepted before it could send, then sends and plays nothing, whatever a hostile host relays. A join shown the
/// session without voice stays without it, whatever the host says in-band. Each hosting run applies its own setting.
/// </summary>
internal static class VoiceOfferTests
{
    internal static void ASessionWithoutVoiceCarriesNone()
    {
        using var rig = new VoiceSessionHarness();
        rig.Host.OfferVoice(false);
        var (a, aLink) = rig.AddClient("a");
        var (b, bLink) = rig.AddClient("b");
        Assert(!rig.Host.IsVoiceOffered && !a.IsVoiceOffered && !b.IsVoiceOffered, "the host offers no voice, and both players heard so as they joined");
        var ears = new[] { rig.Listen(rig.Host), rig.Listen(a), rig.Listen(b) };
        var talkers = new[] { rig.Talk(rig.Host, 1000, 20), rig.Talk(a, 2000, 20), rig.Talk(b, 3000, 20) };
        rig.Advance(40);
        rig.Flush();

        Assert(talkers.All(talker => talker.Results.Count == 20 && talker.Results.All(result => result == VoiceSubmitResult.NotOffered)),
            "every frame the host's player and both players handed their engines was refused as not offered");
        Assert(aLink.Outbound!.ChannelFramesSent[(int)EngineChannel.Voice] == 0 && bLink.Outbound!.ChannelFramesSent[(int)EngineChannel.Voice] == 0,
            "no voice datagram left either player");
        Assert(aLink.Peer!.Outbound!.ChannelFramesSent[(int)EngineChannel.Voice] == 0 && bLink.Peer!.Outbound!.ChannelFramesSent[(int)EngineChannel.Voice] == 0,
            "and none left the host for them");
        var host = rig.Host.GetVoiceStatistics();
        Assert(host.Submitted == 0 && host.Sent == 0 && host.Relayed == 0 && host.Delivered == 0 && host.TotalDropped == 0,
            "the host relayed nothing and played nothing, and had nothing to drop, since nobody sent anything");
        Assert(ears.All(ear => ear.Frames.Count == 0) && a.GetVoiceStatistics().Delivered == 0 && b.GetVoiceStatistics().Delivered == 0,
            "nobody heard anything");

        // Moderation is not voice: a server mute still reaches every player, through the roster.
        Assert(rig.Host.SetServerMute(new ParticipantId("a"), true), "the host can still mute a player for everyone");
        rig.Flush();
        Assert(a.IsServerMuted(new ParticipantId("a")) && b.IsServerMuted(new ParticipantId("a")), "and every player learns it");
    }

    internal static void ATalkerPastItsOwnGateIsDroppedOnArrivalWithoutAStrike()
    {
        using var rig = new VoiceSessionHarness();
        rig.Host.OfferVoice(false);
        var (a, aLink) = rig.AddClient("a");
        var (b, _) = rig.AddClient("b");
        var bEars = rig.Listen(b);
        var hostEars = rig.Listen(rig.Host);
        var remembered = rig.Host.VoiceModerationCount;

        // A modified client writes voice straight onto its link, past its own engine: 240 datagrams a second for 20 s. In a
        // session that offers voice the relay policy mutes such a flood within two seconds and removes it within a minute.
        ulong next = 0;
        for (var tick = 0; tick < 20 * 60; tick++)
        {
            for (var index = 0; index < 4; index++, next++)
            {
                aLink.Send(EngineChannel.Voice, new ArraySegment<byte>(VoiceSessionTests.Datagram(rig.Codec, 0, next + 1, next, (short)next)));
            }

            rig.Advance();
        }

        rig.Flush();
        var statistics = rig.Host.GetVoiceStatistics();
        Assert(statistics.Dropped(VoiceDropReason.NotOffered) == (long)next && statistics.TotalDropped == (long)next,
            "each of the " + next + " datagrams was dropped as it arrived, under a reason of its own (" + statistics.Dropped(VoiceDropReason.NotOffered) + ")");
        Assert(statistics.Strikes == 0 && statistics.AutoMutes == 0 && statistics.Kicks == 0 && statistics.Relayed == 0,
            "with no strike, no mute and no kick, and nothing relayed: the relay policy never judged one of them");
        Assert(!a.IsEnded && a.Snapshot.State == MultiplayerSessionState.Ready && !rig.Host.IsServerMuted(new ParticipantId("a")) && rig.Host.ServerMuteCount == 0,
            "the flooder stays in the session, unmuted");
        Assert(bEars.Frames.Count == 0 && hostEars.Frames.Count == 0 && rig.Diagnostics.Count(EngineDiagnosticKind.VoiceAbuse) == 0,
            "nobody heard any of it, and nothing was reported");
        Assert(rig.Host.VoiceModerationCount == remembered, "and the host remembers nothing more about anyone");

        // A burst far past what a link holds is bounded by the link: the host takes what it holds, and drops each of those.
        var before = statistics.Dropped(VoiceDropReason.NotOffered);
        var hostEnd = aLink.Peer!;
        var overflowed = hostEnd.DatagramsOverflowed;
        for (var index = 0; index < 1_000; index++, next++)
        {
            aLink.Send(EngineChannel.Voice, new ArraySegment<byte>(VoiceSessionTests.Datagram(rig.Codec, 0, next + 1, next, (short)next)));
        }

        rig.Flush();
        var taken = rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.NotOffered) - before;
        Assert(taken == VirtualLinkEnd.MaximumInboundDatagrams && hostEnd.DatagramsOverflowed - overflowed == 1_000 - taken,
            "of a burst of 1,000 the host took the " + taken + " its link held and dropped each; the link let the rest go");
        Assert(rig.Host.GetVoiceStatistics().Strikes == 0 && !a.IsEnded, "still with no strike");
    }

    internal static void AHostileHostIsNeverPlayed()
    {
        // A host says in JoinAccepted that its session offers no voice, then relays voice anyway from a speaker its roster
        // lists. The player plays none of it, counts each datagram, and sends nothing of its own.
        var codec = new FakeVoiceCodec();
        var network = new VirtualEngineTransport(new VirtualLinkConditions());
        var pair = network.Connect("client");
        var client = SessionEngine.CreateClient(
            new EngineOptions { StepRateHz = 60, Voice = new VoiceOptions(codec), CallbackFailures = EngineCallbackFailureMode.Propagate },
            new MultiplayerSessionId("placeholder"),
            0);
        client.Connect(pair.ClientEnd, "Client");
        var host = new RawPeer(pair.HostEnd);
        AcceptRaw(host, voiceOffered: false, new VoiceRosterEntry(5, "friend", false), new VoiceRosterEntry(6, "client", false));
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
        Assert(client.Snapshot.State == MultiplayerSessionState.Ready && !client.IsVoiceOffered, "the client joined the raw host and heard it offers no voice");
        for (ulong sequence = 1; sequence <= 12; sequence++)
        {
            pair.HostEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(VoiceSessionTests.Datagram(codec, 5, sequence, sequence, (short)(500 + sequence))));
            Pump();
        }

        Pump();
        Assert(!client.TryReceiveVoiceFrame(out _), "none of the friend's voice is queued to play");
        var statistics = client.GetVoiceStatistics();
        Assert(statistics.Dropped(VoiceDropReason.NotOffered) == 12 && statistics.Delivered == 0, "each of the 12 datagrams was dropped and counted as not offered");
        Assert(client.SubmitVoiceFrame(0, VoiceFrameFlags.None, codec.Packet(1)) == VoiceSubmitResult.NotOffered &&
               pair.ClientEnd.Outbound!.ChannelFramesSent[(int)EngineChannel.Voice] == 0, "and the client's own talker sends nothing");
        Assert(client.Snapshot.State == MultiplayerSessionState.Ready, "the client stays in the session: a host's voice is no protocol fault");
        client.Shutdown(MultiplayerSessionEndReason.Left);
    }

    internal static void AJoinShownWithoutVoiceStaysWithoutIt()
    {
        // The rig's host offers voice and says so in JoinAccepted. Player c joins over a link it was shown no voice for, as
        // a descriptor or a listing would have shown it: the stricter setting wins, so c sends and plays nothing, while a,
        // shown nothing, is heard.
        using var rig = new VoiceSessionHarness();
        var (a, _) = rig.AddClient("a");
        var c = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("voice-session"), rig.NowMicros));
        var cLink = ConnectShownWithoutVoice(rig, c, "c");
        rig.Flush();
        Assert(c.Snapshot.State == MultiplayerSessionState.Ready && rig.Host.IsVoiceOffered && a.IsVoiceOffered, "c joins a host that offers voice");
        Assert(!c.IsVoiceOffered, "but c was shown the session without voice, and that holds");
        var hostEars = rig.Listen(rig.Host);
        var cEars = rig.Listen(c);
        var cTalker = rig.Talk(c, 3000, 20);
        rig.Talk(a, 2000, 20);
        rig.Advance(40);
        rig.Flush();
        Assert(cTalker.Results.Count == 20 && cTalker.Results.All(result => result == VoiceSubmitResult.NotOffered) &&
               cLink.Outbound!.ChannelFramesSent[(int)EngineChannel.Voice] == 0, "every frame c handed over was refused, and no voice datagram left c");
        Assert(hostEars.From("a").Count == 20 && hostEars.From("c").Count == 0, "the host heard all of a and nothing of c");
        Assert(cEars.Frames.Count == 0 && c.GetVoiceStatistics().Dropped(VoiceDropReason.NotOffered) == 10,
            "c played none of the 10 datagrams the host relayed it from a, and counted each");

        // A reconnect within the same join is shown the same way, so it stays without voice.
        rig.Network.Sever(cLink);
        rig.Flush();
        cLink = ConnectShownWithoutVoice(rig, c, "c");
        rig.Flush();
        Assert(c.Snapshot.State == MultiplayerSessionState.Ready && !c.IsVoiceOffered, "c reconnects, still without voice");
        Assert(c.SubmitVoiceFrame(500, VoiceFrameFlags.None, rig.Codec.Packet(1)) == VoiceSubmitResult.NotOffered &&
               cLink.Outbound!.ChannelFramesSent[(int)EngineChannel.Voice] == 0, "and sends nothing over the new link");

        // Leaving the role clears it.
        c.SwitchToSolo(new SoloSessionStart(new MultiplayerSessionId("solo-c"), new SessionSeed(5), new ParticipantId("c"), "C"));
        Assert(c.IsVoiceOffered, "back in solo play, nothing of the join's setting is left");
    }

    internal static void EachHostingRunAppliesItsOwnSetting()
    {
        using var rig = new VoiceSessionHarness();
        var hostEars = rig.Listen(rig.Host);
        Assert(rig.Host.IsVoiceOffered, "a host offers voice until it says otherwise");
        foreach (var (offered, name) in new[] { (false, "a"), (true, "b"), (false, "c") })
        {
            rig.Host.OfferVoice(offered);
            var (player, _) = rig.AddClient(name);
            Assert(player.IsVoiceOffered == offered && rig.Host.IsVoiceOffered == offered, name + "'s run: the setting is " + offered + " on both sides");
            Throws<InvalidOperationException>(() => rig.Host.OfferVoice(!offered), "the setting is fixed while a player is connected");
            rig.Host.OfferVoice(offered);
            var talker = rig.Talk(player, 1000, 10);
            rig.Advance(20);
            rig.Flush();
            var results = talker.Results.Take(10).ToList();
            if (offered)
            {
                Assert(results.All(result => result is VoiceSubmitResult.Sent or VoiceSubmitResult.Held) && hostEars.From(name).Count == 10,
                    name + "'s run offers voice: the host hears all ten frames");
            }
            else
            {
                Assert(results.All(result => result == VoiceSubmitResult.NotOffered) && hostEars.From(name).Count == 0,
                    name + "'s run offers none: nothing is sent or heard");
            }

            rig.Host.CloseConnections(MultiplayerSessionEndReason.HostEnded, "The host stopped hosting.");
            rig.Flush();
            Assert(player.IsEnded && rig.Host.IsVoiceOffered, "stopping ends " + name + "'s session, and the run's setting with it");
        }

        var client = SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("voice-session"), rig.NowMicros);
        Throws<InvalidOperationException>(() => client.OfferVoice(false), "only a host offers voice");
        client.Shutdown(MultiplayerSessionEndReason.Left);
    }

    internal static void JoinAcceptedSaysWhenTheSessionOffersNoVoice()
    {
        // VoiceMuteTests' vector, a session that offers voice, is unchanged; a session that offers none adds one byte.
        var offered = new byte[] { 0x02, 0x10, 0x01, 0x73, 0x07, 0x01, 0x02, 0x70, 0x31, 0x01, 0x01, 0x68, 0x05, 0x14, 0x03, 0x01, 0x00, 0x00 };
        var withoutVoice = new byte[] { 0x02, 0x11, 0x01, 0x73, 0x07, 0x01, 0x02, 0x70, 0x31, 0x01, 0x01, 0x68, 0x05, 0x14, 0x03, 0x01, 0x00, 0x00, 0x00 };
        Assert(Write(voiceOffered: true).SequenceEqual(offered), "a session that offers voice writes the bytes it always did");
        Assert(Write(voiceOffered: false).SequenceEqual(withoutVoice), "one that offers none adds a false byte after the join generation");
        Assert(Read(offered).VoiceOffered && !Read(withoutVoice).VoiceOffered, "and each reads back as written");

        // A reader never reads past the fields it knows: one more field after this one is skipped, as this one is by a
        // reader that predates it.
        var extended = withoutVoice.Concat(new byte[] { 0x07 }).ToArray();
        extended[1] = 0x12;
        Assert(Read(extended) is { VoiceOffered: false, Generation: 0, RecordKept: true }, "a byte after it is left unread");
        withoutVoice[18] = 0x02;
        try
        {
            Read(withoutVoice);
            Assert(false, "a value other than 0 or 1 is refused");
        }
        catch (ProtocolException)
        {
        }

        // A real host says so to a peer that joins it.
        using var rig = new VoiceSessionHarness();
        rig.Host.OfferVoice(false);
        Assert(!Accepted(rig, "raw-1").VoiceOffered, "a host that offers no voice says so in JoinAccepted");
        rig.Host.CloseConnections(MultiplayerSessionEndReason.HostEnded, "The host stopped hosting.");
        rig.Flush();
        Assert(Accepted(rig, "raw-2").VoiceOffered, "and one that offers voice writes nothing more");

        static byte[] Write(bool voiceOffered)
        {
            var frame = new ProtocolWriter();
            var body = new ProtocolWriter();
            JoinMessages.WriteJoinAccepted(frame, body, new JoinAcceptedMessage
            {
                SessionId = "s",
                Seed = 7,
                Epoch = 1,
                ParticipantId = "p1",
                HostParticipantId = "h",
                Tick = 5,
                StepRateHz = 20,
                NextSequence = 3,
                RecordKept = true,
                VoiceOffered = voiceOffered
            });
            return frame.ToArray();
        }

        static JoinAcceptedMessage Read(byte[] bytes)
        {
            var reader = new ProtocolReader(bytes);
            Assert(reader.ReadVarUInt() == MessageType.JoinAccepted, "the envelope names JoinAccepted");
            return JoinMessages.ReadJoinAccepted(reader.Slice(reader.ReadLength(ProtocolLimits.MaximumFrameBytes)));
        }

        static JoinAcceptedMessage Accepted(VoiceSessionHarness rig, string identity)
        {
            var pair = rig.Network.Connect(identity);
            rig.Host.Accept(pair.HostEnd);
            var peer = new RawPeer(pair.ClientEnd);
            peer.Join(identity);
            rig.Flush();
            return JoinMessages.ReadJoinAccepted(peer.Receive().First(message => message.Type == MessageType.JoinAccepted).Reader());
        }
    }

    /// <summary>Connects <paramref name="engine"/> to the rig's host over a new link, shown the session as offering no voice.</summary>
    private static VirtualLinkEnd ConnectShownWithoutVoice(VoiceSessionHarness rig, SessionEngine engine, string identity)
    {
        var pair = rig.Network.Connect(identity);
        rig.Host.Accept(pair.HostEnd);
        engine.Connect(pair.ClientEnd, identity, shownWithoutVoice: true);
        return pair.ClientEnd;
    }

    /// <summary>Has a raw host accept the client: join, an empty snapshot, the roster <paramref name="roster"/> names, and a commit.</summary>
    private static void AcceptRaw(RawPeer host, bool voiceOffered, params VoiceRosterEntry[] roster) =>
        host.Send((frame, body) =>
        {
            JoinMessages.WriteJoinAccepted(frame, body, new JoinAcceptedMessage
            {
                SessionId = "raw-session",
                Seed = 1,
                ParticipantId = "client",
                StepRateHz = 60,
                NextSequence = 1,
                VoiceOffered = voiceOffered
            });
            SessionMessages.WriteEpochMessage(frame, body, MessageType.SnapshotBegin, 0);
            SessionMessages.WriteEmpty(frame, body, MessageType.SnapshotEnd);
            VoiceMessages.WriteRoster(frame, body, roster);
            SessionMessages.WriteCommit(frame, body, 0, 0);
        });

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("Assertion failed: " + message + " (expected " + typeof(TException).Name + ")");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
