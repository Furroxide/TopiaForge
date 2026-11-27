using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// What the host's voice moderation must not get wrong over time: a stalled host strikes nobody, a bitrate flood is
/// struck like any other, moderation outlives a participant record's grace period, a full table forgets whoever was seen
/// longest ago, a listener's mutes stay behind when the player leaves for another role or a host that does not know
/// them, and a short stream that restarts plays from its first frame.
/// </summary>
internal static class VoiceModerationTests
{
    internal static void HostStallsStrikeNobody()
    {
        // a talks at the edge of the limits, 25 datagrams a second of two 80-byte frames, about 34 kbit/s on the wire
        // against the 40 allowed. The host's main thread stalls for 0.5, 1.2, 3 and 6 seconds, then twice for 1.1
        // seconds two seconds apart. Each time the backlog arrives at once, as much of it as the link holds.
        using var rig = new VoiceSessionHarness(packetBytes: 80);
        var (a, _) = rig.AddClient("a");
        var (b, _) = rig.AddClient("b");
        var bEars = rig.Listen(b);
        var talker = rig.Talk(a, 2000);
        rig.Advance(5 * 60);
        foreach (var (stallTicks, afterTicks) in new[] { (30, 5 * 60), (72, 5 * 60), (180, 5 * 60), (360, 5 * 60), (66, 2 * 60), (66, 32 * 60) })
        {
            rig.Stalled.Add(rig.Host);
            rig.Advance(stallTicks);
            rig.Stalled.Remove(rig.Host);
            rig.Advance(afterTicks);
        }

        var statistics = rig.Host.GetVoiceStatistics();
        Assert(statistics.Strikes == 0 && statistics.AutoMutes == 0 && statistics.Kicks == 0 && !a.IsEnded,
            "no stall struck a, muted a or removed a (" + statistics.Strikes + " strikes)");
        Assert(statistics.Dropped(VoiceDropReason.RateLimited) > 0, "the longer backlogs were more than a second of burst, so some of each was dropped");

        // Fresh speech is never what gets dropped: everything a said in the last 30 s, well after the last stall, arrived,
        // apart from the few frames still on their way.
        var spoken = talker.Next;
        var recent = bEars.From("a").Select(frame => frame.Timestamp).Where(timestamp => timestamp + (30 * 50) >= spoken && timestamp + 10 < spoken).ToList();
        Assert(recent.Count == (30 * 50) - 10 && recent.Zip(recent.Skip(1)).All(pair => pair.Second == pair.First + 1),
            "b heard every frame of the last 30 seconds (" + recent.Count + ")");
    }

    internal static void ModerationOutlivesTheGracePeriod()
    {
        using var rig = new VoiceSessionHarness();
        var (a, aLink) = rig.AddClient("a");
        var aId = new ParticipantId("a");
        Assert(rig.Host.SetServerMute(aId, true), "the host mutes a");
        rig.Flush();

        // a drops out for longer than the 30-second grace, so the host forgets the participant record, then rejoins.
        rig.Network.Sever(aLink);
        rig.Advance(31 * 60);
        Assert(!rig.Host.Snapshot.TryGetParticipant(aId, out _), "a's participant record is gone");
        aLink = rig.Connect(a, "a");
        rig.Flush();
        Assert(rig.Host.IsServerMuted(aId) && a.IsServerMuted(aId), "a is still muted after rejoining");
        Assert(a.SubmitVoiceFrame(0, VoiceFrameFlags.None, rig.Codec.Packet(1)) == VoiceSubmitResult.ServerMuted, "and cannot talk");
        rig.Host.SetServerMute(aId, false);
        rig.Flush();

        // The ladder from automatic mutes to removal survives the same way: two mutes, a long absence, and the third removes a.
        ulong next = 0;
        Assert(rig.FloodUntilMuted(aLink, ref next) > 0, "a first flood mutes a");
        rig.Advance(31 * 60);
        Assert(rig.FloodUntilMuted(aLink, ref next) > 0, "a second one too");
        rig.Network.Sever(aLink);
        rig.Advance(31 * 60);
        aLink = rig.Connect(a, "a");
        rig.Flush();
        Assert(!a.IsEnded && a.Snapshot.State == MultiplayerSessionState.Ready, "a rejoins after its record was forgotten");
        rig.FloodUntilMuted(aLink, ref next);
        rig.Flush();
        var statistics = rig.Host.GetVoiceStatistics();
        Assert(statistics.Kicks == 1 && a.IsEnded && a.Snapshot.EndReason == MultiplayerSessionEndReason.Kicked,
            "the third automatic mute within ten minutes removes a, rejoin or not");
    }

    internal static void TheModerationTableIsBounded()
    {
        // With room for three players, a muted player and four quiet ones pass through, each gone longer than the grace.
        using var rig = new VoiceSessionHarness(tune: voice => voice.ModerationCapacity = 3);
        var (muted, mutedLink) = rig.AddClient("m");
        Assert(rig.Host.SetServerMute(new ParticipantId("m"), true), "the host mutes m");
        rig.Flush();
        rig.Network.Sever(mutedLink);
        rig.Advance(31 * 60);
        foreach (var name in new[] { "q1", "q2", "q3", "q4" })
        {
            var (_, link) = rig.AddClient(name);
            rig.Network.Sever(link);
            rig.Advance(31 * 60);
            Assert(rig.Host.VoiceModerationCount <= 3, "the table holds no more than its capacity after " + name);
        }

        rig.Connect(muted, "m");
        rig.Flush();
        Assert(rig.Host.IsServerMuted(new ParticipantId("m")), "the muted player, seen longest ago, outlived the quiet ones");
    }

    internal static void ListenerMutesStayWithTheirSession()
    {
        // A traveller mutes "a" in one session, then joins another host, where a different player is also called "a".
        using var rig = new VoiceSessionHarness();
        rig.AddClient("a");
        var (traveller, _) = rig.AddClient("traveller");
        traveller.SetListenerMutes(new[] { new ParticipantId("a") });
        rig.Flush();
        var other = rig.Track(SessionEngine.CreateHost(rig.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("other"),
            new SessionSeed(9), new ParticipantId("host-b"), "Host B", rig.NowMicros));
        var otherA = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("other"), rig.NowMicros));
        rig.ConnectTo(other, otherA, "a");
        traveller.SwitchToSolo(new SoloSessionStart(new MultiplayerSessionId("between"), new SessionSeed(3), new ParticipantId("traveller"), "Traveller"));
        traveller.SwitchToClient(new MultiplayerSessionId("joining"));
        rig.ConnectTo(other, traveller, "traveller");
        rig.Flush();
        Assert(traveller.Snapshot.State == MultiplayerSessionState.Ready, "the traveller joins host B");
        var ears = rig.Listen(traveller);
        rig.Talk(otherA, 5000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears.From("a").Count == 10, "the traveller hears host B's a: the mute of the other session's a stayed behind");
        Assert(other.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 0, "and host B never learned whom the traveller had muted");
    }

    internal static void AShortStreamRestartsCleanly()
    {
        // a says ten frames, then a's game restarts and says twenty: sequences and timestamps start again at the bottom.
        using var rig = new VoiceSessionHarness();
        var (a, aLink) = rig.AddClient("a");
        var hostEars = rig.Listen(rig.Host);
        rig.Talk(a, 2000, 10);
        rig.Advance(30);
        rig.Flush();
        rig.Network.Sever(aLink);
        rig.Flush();
        var (restarted, _) = rig.AddClient("a");
        rig.Talk(restarted, 3000, 20);
        rig.Advance(40);
        rig.Flush();
        var (_, decoded, statistics) = hostEars.Play("a");
        var expected = Enumerable.Range(2000, 10).Concat(Enumerable.Range(3000, 20)).Select(id => (short)id);
        Assert(decoded.SequenceEqual(expected), "both streams play in full, the restarted one from its first frame");
        Assert(statistics.Restarts == 1 && statistics.Late == 0, "the restart is noticed, and nothing of the new stream is dropped as late");
    }

    internal static void ListenerMutesStayBehindWhenTheLinkMovesToAnotherSession()
    {
        // The traveller mutes "a", and everyone, in host A's session. Then its link moves to host B with no role change, as
        // a reconnect does once host A has restarted: host B's session has another id, and a player of its own called "a".
        using var rig = new VoiceSessionHarness();
        rig.AddClient("a");
        var (traveller, _) = rig.AddClient("traveller");
        traveller.SetListenerMutes(new[] { new ParticipantId("a") }, muteAll: true);
        rig.Flush();
        var other = rig.Track(SessionEngine.CreateHost(rig.Options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("other"),
            new SessionSeed(9), new ParticipantId("host-b"), "Host B", rig.NowMicros));
        var otherA = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("other"), rig.NowMicros));
        rig.ConnectTo(other, otherA, "a");
        rig.ConnectTo(other, traveller, "traveller");
        rig.Flush();
        Assert(traveller.Snapshot.State == MultiplayerSessionState.Ready && traveller.Snapshot.Id.Equals(new MultiplayerSessionId("other")),
            "the traveller joins host B's session as a client still");
        var ears = rig.Listen(traveller);
        rig.Talk(otherA, 5000, 10);
        rig.Advance(30);
        rig.Flush();
        Assert(ears.From("a").Count == 10, "the traveller hears all of host B's a: neither mute crossed into the other session (" + ears.From("a").Count + ")");
        Assert(other.GetVoiceStatistics().Dropped(VoiceDropReason.ListenerMuted) == 0, "and host B never learned whom the traveller had muted");
    }

    internal static void AFullTableForgetsWhoeverWasSeenLongestAgo()
    {
        // Room for two players. m joins first and is muted by hand; s joins next and earns one strike. s leaves, and m
        // leaves 91 seconds later. Once both records have expired a third player arrives, and the full table must forget
        // one of them: s, who was seen longest ago, not m, who was admitted first.
        using var rig = new VoiceSessionHarness(tune: voice => voice.ModerationCapacity = 2);
        var mId = new ParticipantId("m");
        var (m, mLink) = rig.AddClient("m");
        var (_, sLink) = rig.AddClient("s");
        Assert(rig.Host.SetServerMute(mId, true), "the host mutes m");
        sLink.Send(EngineChannel.Voice, new ArraySegment<byte>(new byte[] { 0x7F }));
        rig.Flush();
        Assert(rig.Host.GetVoiceStatistics().Strikes == 1, "s's malformed datagram earns it a strike");
        rig.Network.Sever(sLink);
        rig.Advance(91 * 60);
        rig.Network.Sever(mLink);
        rig.Advance(31 * 60);
        Assert(!rig.Host.Snapshot.TryGetParticipant(mId, out _) && !rig.Host.Snapshot.TryGetParticipant(new ParticipantId("s"), out _),
            "both records are gone");
        rig.AddClient("n");
        Assert(rig.Host.VoiceModerationCount == 2, "the table stays at its capacity");
        rig.Connect(m, "m");
        rig.Flush();
        Assert(rig.Host.IsServerMuted(mId) && m.IsServerMuted(mId), "m is still muted after rejoining: the table forgot s instead");
    }

    internal static void ABitrateFloodIsStruckAndMuted()
    {
        // A modified client sends one datagram a tick, 60 a second: twice the datagram limit, which on its own never
        // strikes. Each holds two 160-byte frames, though, about 159 kbit/s against the 40 allowed. The bitrate's sustained
        // allowance, 80 kbit/s for five seconds, is spent after about five seconds, and the strikes that follow mute a.
        using var rig = new VoiceSessionHarness();
        var (a, aLink) = rig.AddClient("a");
        var (b, _) = rig.AddClient("b");
        var aId = new ParticipantId("a");
        var big = new FakeVoiceCodec(160);
        var firstStrike = -1;
        var muted = -1;
        for (var tick = 1; tick <= 20 * 60 && muted < 0; tick++)
        {
            var sequence = (ulong)(2 * tick) - 1;
            aLink.Send(EngineChannel.Voice, new ArraySegment<byte>(VoiceRelayPolicyTests.Datagram(big, sequence, sequence - 1, pair: true)));
            rig.Advance();
            var statistics = rig.Host.GetVoiceStatistics();
            if (firstStrike < 0 && statistics.Strikes > 0) firstStrike = tick;
            if (statistics.AutoMutes > 0) muted = tick;
        }

        Assert(firstStrike >= 290 && firstStrike <= 320, "the first strike comes once the flood has lasted about five seconds (tick " + firstStrike + ")");
        Assert(muted > 0 && muted <= 400, "and a is muted within about six and a half seconds (tick " + muted + ")");
        rig.Flush();
        Assert(rig.Host.IsServerMuted(aId) && a.IsServerMuted(aId) && b.IsServerMuted(aId), "everyone learns that a is muted");
        Assert(rig.Diagnostics.Count(EngineDiagnosticKind.VoiceAbuse) == 1, "and the host reports it");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
