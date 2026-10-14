using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// What the host's voice moderation must not get wrong over time: a stalled host strikes nobody, moderation outlives a
/// participant record's grace period, a listener's mutes stay in the session they were made in, and a short stream that
/// restarts plays from its first frame.
/// </summary>
internal static class VoiceModerationTests
{
    internal static void HostStallsStrikeNobody()
    {
        // a talks normally, 25 datagrams a second, while the host's main thread stalls three times: 2 s, then 1.1 s twice,
        // 37 s apart. Each time the backlog arrives at once.
        using var rig = new VoiceSessionHarness();
        var (a, _) = rig.AddClient("a");
        var (b, _) = rig.AddClient("b");
        var bEars = rig.Listen(b);
        var talker = rig.Talk(a, 2000);
        foreach (var stallTicks in new[] { 120, 66, 66 })
        {
            rig.Advance(5 * 60);
            rig.Stalled.Add(rig.Host);
            rig.Advance(stallTicks);
            rig.Stalled.Remove(rig.Host);
            rig.Advance(32 * 60);
        }

        var statistics = rig.Host.GetVoiceStatistics();
        Assert(statistics.Strikes == 0 && statistics.AutoMutes == 0 && statistics.Kicks == 0 && !a.IsEnded,
            "no stall struck a, muted a or removed a (" + statistics.Strikes + " strikes)");
        Assert(statistics.Dropped(VoiceDropReason.RateLimited) > 0, "the two-second backlog was more than a second of burst, so some of it was dropped");

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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
