using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Listener mutes follow the people. A mute set lasts as long as the host keeps the player's participant record, however
/// the client got back into the session, and is forgotten when the join finds a host that did not keep it. A set made
/// while a join is under way applies to the session the join finds, with or without a role change first.
/// </summary>
internal static class VoiceMuteTests
{
    internal static void JoinAcceptedSaysWhetherTheHostKeptTheRecord()
    {
        // Session "s", seed 7, epoch 1, participant "p1", host "h", tick 5, 20 Hz, next sequence 3, record kept, no report.
        var vector = new byte[] { 0x02, 0x0F, 0x01, 0x73, 0x07, 0x01, 0x02, 0x70, 0x31, 0x01, 0x01, 0x68, 0x05, 0x14, 0x03, 0x01, 0x00 };
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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
