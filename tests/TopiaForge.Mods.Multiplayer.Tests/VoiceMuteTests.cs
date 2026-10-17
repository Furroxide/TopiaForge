using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Listener mutes follow the people. A mute set lasts as long as the host keeps the player's participant record, however
/// the client got back into the session, and is forgotten when the join finds a host that did not keep it, or when a
/// join's answer is lost, which may hide a record the host made meanwhile. Names name the players the session lists, so
/// they belong to the host that listed them, even while a join is under way or after one, until the new host lists its
/// own. A set that names nobody, made while a join is under way or with no link at all, applies to the session the join
/// finds, with or without a role change first.
/// </summary>
internal static class VoiceMuteTests
{
    internal static void JoinAcceptedSaysWhetherTheHostKeptTheRecord()
    {
        // Session "s", seed 7, epoch 1, participant "p1", host "h", tick 5, 20 Hz, next sequence 3, record kept, no report,
        // join generation 0.
        var vector = new byte[] { 0x02, 0x10, 0x01, 0x73, 0x07, 0x01, 0x02, 0x70, 0x31, 0x01, 0x01, 0x68, 0x05, 0x14, 0x03, 0x01, 0x00, 0x00 };
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
            RecordKept = true
        });
        Assert(frame.ToArray().SequenceEqual(vector), "JoinAccepted carries whether the host kept the record, after the next sequence");
        Assert(Read(vector) is { RecordKept: true, NextSequence: 3, SessionId: "s" }, "and reads it back");
        vector[15] = 0x00;
        Assert(Read(vector) is { RecordKept: false }, "a new record reads as such");
        vector[15] = 0x02;
        try
        {
            Read(vector);
            Assert(false, "a flag other than 0 or 1 is refused");
        }
        catch (ProtocolException)
        {
        }

        static JoinAcceptedMessage Read(byte[] bytes)
        {
            var reader = new ProtocolReader(bytes);
            Assert(reader.ReadVarUInt() == MessageType.JoinAccepted, "the envelope names JoinAccepted");
            return JoinMessages.ReadJoinAccepted(reader.Slice(reader.ReadLength(ProtocolLimits.MaximumFrameBytes)));
        }
    }

    internal static void MutesSurviveAnInPlaceReplacementHoweverTheClientResynchronizes()
    {
        // b, c and d mute a. The host replaces its session in place while b's link is down; c resynchronizes over its link,
        // and so does d, whose link drops only afterwards. b and d then reconnect into the new session. Same host, same
        // records, same participant ids: all three still hear none of a.
        using var rig = new VoiceSessionHarness();
        var (a, _) = rig.AddClient("a");
        var (b, bLink) = rig.AddClient("b");
        var (c, _) = rig.AddClient("c");
        var (d, dLink) = rig.AddClient("d");
        var aId = new ParticipantId("a");
        foreach (var listener in new[] { b, c, d }) listener.SetListenerMutes(new[] { aId });
        rig.Flush();

        rig.Network.Sever(bLink);
        rig.Flush();
        rig.Host.ReplaceSession(new MultiplayerSessionId("voice-session-2"), new SessionSeed(8));
        rig.Flush();
        rig.Network.Sever(dLink);
        rig.Flush();
        rig.Connect(b, "b");
        rig.Connect(d, "d");
        rig.Flush();
        var replaced = new MultiplayerSessionId("voice-session-2");
        Assert(new[] { b, c, d }.All(engine => engine.Snapshot.State == MultiplayerSessionState.Ready && engine.Snapshot.Id.Equals(replaced)),
            "b, c and d are all in the replacement session");

        var ears = new[] { b, c, d }.Select(rig.Listen).ToList();
        var before = rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted);
        rig.Talk(a, 2000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears.All(listener => listener.From("a").Count == 0),
            "none of them hears a (b " + ears[0].From("a").Count + ", c " + ears[1].From("a").Count + ", d " + ears[2].From("a").Count + ")");
        Assert(rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == before + 15,
            "because the host skipped all three for each of a's 5 datagrams");
        var kept = new List<ParticipantId>();
        Assert(new[] { b, c, d }.All(engine => !engine.GetListenerMutes(kept) && kept.SequenceEqual(new[] { aId })),
            "and each engine still reports a as muted");
    }

    internal static void MutesStayBehindWhenTheRecordExpired()
    {
        // b mutes a, then stays away past the 30-second grace, so the host forgets b's record. b comes back to the same host
        // and even the same session id, but as a new record: its old mute set is forgotten, and never sent.
        using var rig = new VoiceSessionHarness();
        var (a, _) = rig.AddClient("a");
        var (b, bLink) = rig.AddClient("b");
        b.SetListenerMutes(new[] { new ParticipantId("a") });
        rig.Flush();
        rig.Network.Sever(bLink);
        rig.Advance(31 * 60);
        Assert(!rig.Host.Snapshot.TryGetParticipant(new ParticipantId("b"), out _), "b's record is gone");
        rig.Connect(b, "b");
        rig.Flush();
        Assert(b.Snapshot.State == MultiplayerSessionState.Ready && b.Snapshot.Id.Equals(new MultiplayerSessionId("voice-session")),
            "b is back in the same session");

        var ears = rig.Listen(b);
        rig.Talk(a, 2000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears.From("a").Count == 10, "b hears a again (" + ears.From("a").Count + ")");
        Assert(rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 0, "and the host never learned b's old mutes");
        var kept = new List<ParticipantId> { new("stale") };
        Assert(!b.GetListenerMutes(kept) && kept.Count == 0, "b's engine reports the set empty");
    }

    internal static void MutesMadeWithOneHostNeverReachAnother()
    {
        // The traveller visits host B, then moves to host A and mutes A's player a there. Within B's grace it goes back to
        // B, which kept its record. That record is B's, not A's: the set made with A is forgotten, so B's own a is heard and
        // B never learns whom the traveller muted.
        using var rig = new VoiceSessionHarness();
        var other = rig.Track(SessionEngine.CreateHost(rig.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("other"),
            new SessionSeed(9), new ParticipantId("host-b"), "Host B", rig.NowMicros));
        var otherA = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("other"), rig.NowMicros));
        rig.ConnectTo(other, otherA, "a", "host-b");
        var traveller = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("joining"), rig.NowMicros));
        rig.ConnectTo(other, traveller, "traveller", "host-b");
        rig.Flush();
        rig.AddClient("a");
        rig.Connect(traveller, "traveller");
        rig.Flush();
        Assert(traveller.Snapshot.Id.Equals(new MultiplayerSessionId("voice-session")), "the traveller moved to host A");
        traveller.SetListenerMutes(new[] { new ParticipantId("a") });
        rig.Flush();

        rig.ConnectTo(other, traveller, "traveller", "host-b");
        rig.Flush();
        Assert(traveller.Snapshot.State == MultiplayerSessionState.Ready && traveller.Snapshot.Id.Equals(new MultiplayerSessionId("other")),
            "the traveller is back on host B, well within the grace of the record B kept");
        var ears = rig.Listen(traveller);
        rig.Talk(otherA, 5000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears.From("a").Count == 10, "the traveller hears B's a (" + ears.From("a").Count + ")");
        Assert(other.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 0, "and host B never learned whom it muted on host A");
        var kept = new List<ParticipantId>();
        Assert(!traveller.GetListenerMutes(kept) && kept.Count == 0, "the traveller's engine reports the set empty");
    }

    internal static void AMuteSetMadeWhileJoiningAppliesToTheSessionTheJoinFinds()
    {
        // Two travellers on host A head for host B. t1's link just moves; t2 leaves for solo play and becomes a client
        // first. Each mutes everyone while its join is under way, as a voice service re-applying a lasting wish does, and
        // the wish holds in the session the join finds either way.
        using var rig = new VoiceSessionHarness();
        var other = rig.Track(SessionEngine.CreateHost(rig.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("other"),
            new SessionSeed(9), new ParticipantId("host-b"), "Host B", rig.NowMicros));
        var otherA = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("other"), rig.NowMicros));
        rig.ConnectTo(other, otherA, "a", "host-b");
        var (t1, _) = rig.AddClient("t1");
        var (t2, _) = rig.AddClient("t2");
        rig.ConnectTo(other, t1, "t1", "host-b");
        t2.SwitchToSolo(new SoloSessionStart(new MultiplayerSessionId("between"), new SessionSeed(3), new ParticipantId("t2"), "T2"));
        t2.SwitchToClient(new MultiplayerSessionId("joining"));
        rig.ConnectTo(other, t2, "t2", "host-b");
        t1.SetListenerMutes(Array.Empty<ParticipantId>(), muteAll: true);
        t2.SetListenerMutes(Array.Empty<ParticipantId>(), muteAll: true);
        rig.Flush();
        Assert(new[] { t1, t2 }.All(engine => engine.Snapshot.State == MultiplayerSessionState.Ready && engine.Snapshot.Id.Equals(new MultiplayerSessionId("other"))),
            "both travellers join host B");

        var ears = new[] { t1, t2 }.Select(rig.Listen).ToList();
        rig.Talk(otherA, 5000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears[0].From("a").Count == 0 && ears[1].From("a").Count == 0,
            "neither hears B's a (t1 " + ears[0].From("a").Count + ", t2 " + ears[1].From("a").Count + ")");
        Assert(other.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 10, "because host B skipped both for each of a's 5 datagrams");
        var kept = new List<ParticipantId>();
        Assert(t1.GetListenerMutes(kept) && kept.Count == 0 && t2.GetListenerMutes(kept) && kept.Count == 0,
            "and both engines report mute-all");
    }

    internal static void AWishToHearNobodyMadeWithNoLinkHoldsWhereverTheJoinGoes()
    {
        // The traveller's link to host A drops, and while it is down the player asks to hear nobody. That names nobody, so
        // it holds in the session the next join finds: host B, another host, never relays the traveller a frame.
        using var rig = new VoiceSessionHarness();
        var other = rig.Track(SessionEngine.CreateHost(rig.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("other"),
            new SessionSeed(9), new ParticipantId("host-b"), "Host B", rig.NowMicros));
        var otherA = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("other"), rig.NowMicros));
        rig.ConnectTo(other, otherA, "a", "host-b");
        var (traveller, link) = rig.AddClient("traveller");
        rig.Network.Sever(link);
        rig.Advance(2);
        Assert(traveller.Snapshot.State == MultiplayerSessionState.Connecting, "the traveller's link is down");
        traveller.SetListenerMutes(Array.Empty<ParticipantId>(), muteAll: true);
        rig.ConnectTo(other, traveller, "traveller", "host-b");
        rig.Flush();
        Assert(traveller.Snapshot.State == MultiplayerSessionState.Ready && traveller.Snapshot.Id.Equals(new MultiplayerSessionId("other")),
            "the traveller joins host B");

        var ears = rig.Listen(traveller);
        rig.Talk(otherA, 5000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears.From("a").Count == 0 && other.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 5,
            "host B skips the traveller for each of a's 5 datagrams (" + ears.From("a").Count + " heard)");
        var kept = new List<ParticipantId>();
        Assert(traveller.GetListenerMutes(kept) && kept.Count == 0, "and the traveller's engine reports mute-all");
    }

    internal static void NamesGivenBeforeTheNewHostListsItsPlayersNameNobodyThere()
    {
        // Host B's world is large, so over a 64 kB/s link its answer to a join spans two frames: JoinAccepted in the first,
        // its participant list after the snapshot in the second. In between, a joining player's session names B's session
        // and lists nobody. t1 mutes the a it saw with host A all the same. That a is A's, nobody with B, so the name goes
        // at once. t2's link drops in that window, and t2 mutes A's a while it is down: the name stays with A, and B, which
        // kept t2's record, forgets it at the next join. Both hear all of B's own a, and B never learns the name.
        using var rig = new VoiceSessionHarness(new VirtualLinkConditions { LatencyTicks = 1, BandwidthBytesPerSecond = 64_000 });
        var aId = new ParticipantId("a");
        rig.AddClient("a");
        var (t1, _) = rig.AddClient("t1");
        var (t2, _) = rig.AddClient("t2");
        var other = rig.Track(SessionEngine.CreateHost(rig.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("other"),
            new SessionSeed(9), new ParticipantId("host-b"), "Host B", rig.NowMicros));
        FillWorld(other.CreateFacade("world"));
        var otherA = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("other"), rig.NowMicros));
        rig.ConnectTo(other, otherA, "a", "host-b");
        rig.Flush();
        rig.ConnectTo(other, t1, "t1", "host-b");
        var t2Link = rig.ConnectTo(other, t2, "t2", "host-b");
        var joined = new MultiplayerSessionId("other");
        for (var tick = 0; tick < 600 && !(t1.Snapshot.Id.Equals(joined) && t2.Snapshot.Id.Equals(joined)); tick++) rig.Advance();
        Assert(new[] { t1, t2 }.All(engine => engine.Snapshot.State == MultiplayerSessionState.Synchronizing && engine.Snapshot.Participants.Count == 0),
            "t1 and t2 are joining host B, and their sessions list nobody yet");

        t1.SetListenerMutes(new[] { aId });
        rig.Network.Sever(t2Link);
        rig.Advance();
        Assert(t2.Snapshot.State == MultiplayerSessionState.Connecting && t2.Snapshot.Participants.Count == 0,
            "t2's link drops before B lists its players");
        t2.SetListenerMutes(new[] { aId });
        var kept = new List<ParticipantId>();
        Assert(!t1.GetListenerMutes(kept) && kept.Count == 0, "t1's name goes at once: it names nobody with B");
        Assert(!t2.GetListenerMutes(kept) && kept.SequenceEqual(new[] { aId }), "t2's, given with no link, waits with A, whose player it names");
        rig.ConnectTo(other, t2, "t2", "host-b");
        rig.Flush();
        Assert(new[] { t1, t2 }.All(engine => engine.Snapshot.State == MultiplayerSessionState.Ready && engine.Snapshot.Id.Equals(joined)),
            "both are in B's session");
        Assert(!t2.GetListenerMutes(kept) && kept.Count == 0, "and B, another host, has forgotten t2's name");

        var ears = new[] { t1, t2 }.Select(rig.Listen).ToList();
        rig.Talk(otherA, 5000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears.All(listener => listener.From("a").Count == 10),
            "both hear all of B's a (t1 " + ears[0].From("a").Count + ", t2 " + ears[1].From("a").Count + ")");
        Assert(other.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 0, "and B never learned the name");
    }

    internal static void NamesFromARestartedHostsOldListNameNobody()
    {
        // Host A numbers its players itself, as a live host does: x is p1, a is p2 and the traveller p3. A's process dies and
        // starts again under the same identity, with a large world, so its answer to a join spans two frames. c and d join
        // the new process first, as p1 and p2, and the traveller comes back as a new record. Until the new list arrives,
        // the traveller's session lists nobody, and the player mutes p2 from the old process's list, meaning a. p2 is d
        // now: the name goes at once, and the traveller hears all of d.
        using var rig = new VoiceSessionHarness(new VirtualLinkConditions { LatencyTicks = 1, BandwidthBytesPerSecond = 64_000 });
        var numbered = Numbered(rig);
        var p2 = new ParticipantId("p2");
        var first = rig.Track(SessionEngine.CreateHost(numbered, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("first-run"),
            new SessionSeed(3), new ParticipantId("host"), "Host", rig.NowMicros));
        var links = new List<VirtualLinkEnd>();
        SessionEngine Join(SessionEngine host, string identity)
        {
            var engine = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("joining"), rig.NowMicros));
            links.Add(rig.ConnectTo(host, engine, identity));
            rig.Flush();
            return engine;
        }

        Join(first, "x");
        Join(first, "a");
        var traveller = Join(first, "traveller");
        Assert(traveller.Snapshot.TryGetParticipant(p2, out var before) && before!.DisplayName == "a", "with the first process, p2 is a");

        // The first process dies without a word: its links go, and it never sends another frame.
        foreach (var link in links) rig.Network.Sever(link);
        first.Shutdown(MultiplayerSessionEndReason.HostEnded);
        var second = rig.Track(SessionEngine.CreateHost(numbered, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("second-run"),
            new SessionSeed(4), new ParticipantId("host"), "Host", rig.NowMicros));
        FillWorld(second.CreateFacade("world"));
        Join(second, "c");
        var d = Join(second, "d");
        Assert(second.Snapshot.TryGetParticipant(p2, out var now) && now!.DisplayName == "d", "with the second process, p2 is d");
        rig.ConnectTo(second, traveller, "traveller");
        var joined = new MultiplayerSessionId("second-run");
        for (var tick = 0; tick < 600 && !traveller.Snapshot.Id.Equals(joined); tick++) rig.Advance();
        Assert(traveller.Snapshot.State == MultiplayerSessionState.Synchronizing && traveller.Snapshot.Participants.Count == 0,
            "the traveller is joining the second process, and its session lists nobody until the new list comes");

        traveller.SetListenerMutes(new[] { p2 });
        var kept = new List<ParticipantId>();
        Assert(!traveller.GetListenerMutes(kept) && kept.Count == 0, "the name goes at once: the first process's p2 is nobody now");
        rig.Flush();
        Assert(traveller.Snapshot.State == MultiplayerSessionState.Ready && traveller.Snapshot.TryGetParticipant(p2, out var listed) &&
            listed!.DisplayName == "d", "back in, the traveller's session lists p2 as d");
        var ears = rig.Listen(traveller);
        rig.Talk(d, 6000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears.From("p2").Count == 10, "the traveller hears all of d (" + ears.From("p2").Count + ")");
        Assert(second.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 0, "and the host never took d for the muted a");
    }

    internal static void MutesFromBeforeARestartStayBehindWhenItsFirstAnswerIsLost()
    {
        // Host A numbers its players, as a live host does: x is p1, a is p2, t1 p3 and t2 p4. t1 mutes p2, meaning a. A's
        // process dies and starts again under the same identity, and c and d join the new process as p1 and p2. Each
        // traveller's first join to it is accepted, so the host makes its record, but the link goes before the answer
        // arrives. With its link down, t2 then mutes p2 from what it saw, meaning a too. Both join again, and the host says
        // it kept their records, records neither ever saw it make: neither name survives, and both hear all of d.
        using var rig = new VoiceSessionHarness();
        var numbered = Numbered(rig);
        var p2 = new ParticipantId("p2");
        var first = rig.Track(SessionEngine.CreateHost(numbered, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("first-run"),
            new SessionSeed(3), new ParticipantId("host"), "Host", rig.NowMicros));
        var links = new List<VirtualLinkEnd>();
        SessionEngine Join(SessionEngine host, string identity)
        {
            var engine = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("joining"), rig.NowMicros));
            links.Add(rig.ConnectTo(host, engine, identity));
            rig.Flush();
            return engine;
        }

        Join(first, "x");
        Join(first, "a");
        var t1 = Join(first, "t1");
        var t2 = Join(first, "t2");
        Assert(t1.Snapshot.TryGetParticipant(p2, out var before) && before!.DisplayName == "a", "with the first process, p2 is a");
        t1.SetListenerMutes(new[] { p2 });
        rig.Flush();

        foreach (var link in links) rig.Network.Sever(link);
        first.Shutdown(MultiplayerSessionEndReason.HostEnded);
        var second = rig.Track(SessionEngine.CreateHost(numbered, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("second-run"),
            new SessionSeed(4), new ParticipantId("host"), "Host", rig.NowMicros));
        Join(second, "c");
        var d = Join(second, "d");
        Assert(second.Snapshot.TryGetParticipant(p2, out var now) && now!.DisplayName == "d", "with the second process, p2 is d");
        foreach (var (traveller, identity) in new[] { (t1, "t1"), (t2, "t2") })
        {
            var link = rig.ConnectTo(second, traveller, identity);
            rig.Advance();
            Assert(traveller.Snapshot.State == MultiplayerSessionState.Connecting && second.Snapshot.Participants.Any(participant => participant.DisplayName == identity),
                identity + "'s join is accepted, and the answer is still on its way");
            rig.Network.Sever(link);
            rig.Advance();
        }

        var kept = new List<ParticipantId>();
        Assert(!t1.GetListenerMutes(kept) && kept.Count == 0, "t1's set goes with the answer it never heard");
        t2.SetListenerMutes(new[] { p2 });
        rig.ConnectTo(second, t1, "t1");
        rig.ConnectTo(second, t2, "t2");
        rig.Flush();
        Assert(new[] { t1, t2 }.All(engine => engine.Snapshot.State == MultiplayerSessionState.Ready && engine.Snapshot.Id.Equals(new MultiplayerSessionId("second-run"))),
            "both are in the second process's session");
        Assert(!t2.GetListenerMutes(kept) && kept.Count == 0, "and t2's name, from the first process's players, is gone too");
        var ears = new[] { t1, t2 }.Select(rig.Listen).ToList();
        rig.Talk(d, 6000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears.All(listener => listener.From("p2").Count == 10),
            "both hear all of d (t1 " + ears[0].From("p2").Count + ", t2 " + ears[1].From("p2").Count + ")");
        Assert(second.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 0, "and the second process never took d for the muted a");
    }

    internal static void AJoinReplacedBeforeItsRequestLeftKeepsTheMutes()
    {
        // b mutes a, and b's link drops. b sets out to rejoin twice in a row, the first attempt replaced before it sent
        // anything, so no answer can have gone missing. The host kept b's record: the mute stays, and the host leaves a out.
        using var rig = new VoiceSessionHarness();
        var (a, _) = rig.AddClient("a");
        var (b, link) = rig.AddClient("b");
        var aId = new ParticipantId("a");
        b.SetListenerMutes(new[] { aId });
        rig.Flush();
        rig.Network.Sever(link);
        rig.Advance(2);
        rig.Connect(b, "b");
        rig.Connect(b, "b");
        rig.Flush();
        var kept = new List<ParticipantId>();
        Assert(b.Snapshot.State == MultiplayerSessionState.Ready && !b.GetListenerMutes(kept) && kept.SequenceEqual(new[] { aId }), "b is back and still mutes a");
        var ears = rig.Listen(b);
        rig.Talk(a, 5000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears.From("a").Count == 0 && rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) > 0, "and b hears none of a");
    }

    internal static void NamesGivenBeforeTheSameHostListsAgainStay()
    {
        // b's link drops and b comes back to the same host, which kept b's record. The host's world is large, so over a
        // 64 kB/s link its list comes well after JoinAccepted. In between, the player mutes a from the list b still shows,
        // which is this host's: the record was kept, so the id still names a, the name stays, and b hears none of a.
        using var rig = new VoiceSessionHarness(new VirtualLinkConditions { LatencyTicks = 1, BandwidthBytesPerSecond = 64_000 });
        FillWorld(rig.Host.CreateFacade("world"));
        var (a, _) = rig.AddClient("a");
        var (b, link) = rig.AddClient("b");
        var aId = new ParticipantId("a");
        rig.Network.Sever(link);
        rig.Advance(2);
        rig.Connect(b, "b");
        for (var tick = 0; tick < 600 && b.Snapshot.State != MultiplayerSessionState.Synchronizing; tick++) rig.Advance();
        Assert(b.Snapshot.State == MultiplayerSessionState.Synchronizing, "b is back in, and the host's world is still on its way");
        b.SetListenerMutes(new[] { aId });
        var kept = new List<ParticipantId>();
        Assert(!b.GetListenerMutes(kept) && kept.SequenceEqual(new[] { aId }), "the name stays");
        rig.Flush();
        var ears = rig.Listen(b);
        rig.Talk(a, 5000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(b.Snapshot.State == MultiplayerSessionState.Ready && ears.From("a").Count == 0 &&
            rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) > 0, "and b hears none of a");
    }

    /// <summary>Engine options for a host that numbers its players itself, p1, p2 and on, as a live host does.</summary>
    internal static EngineOptions Numbered(VoiceSessionHarness rig) => new()
    {
        StepRateHz = 60,
        DownstreamRateHz = 0,
        ClockSyncIntervalMicros = 0,
        ObjectIdPrefix = "voice-",
        CallbackFailures = EngineCallbackFailureMode.Propagate,
        Diagnostics = rig.Diagnostics,
        Voice = rig.Voice
    };

    /// <summary>
    /// Gives a host twenty replicated values of about 4 kB each, so its answer to a join spans two frames, the participant
    /// list after the snapshot in the second. Over a slow link the second comes well after the first.
    /// </summary>
    internal static void FillWorld(IMultiplayerSession world)
    {
        var codec = new JsonTestCodec<WorldChunk>();
        for (var index = 0; index < 20; index++)
        {
            var registered = world.RegisterState(new ReplicatedStateDefinition<WorldChunk>("chunk-" + index, new WorldChunk(new string('x', 4_000)), codec));
            Assert(registered.Succeeded, "chunk " + index + " registers");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}

/// <summary>One replicated piece of a large world, for joins whose snapshot spans more than one frame.</summary>
internal sealed class WorldChunk
{
    public WorldChunk(string text)
    {
        Text = text;
    }

    public string Text { get; }
}
