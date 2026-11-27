using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The participant list a client shows while it joins. Every host numbers its players itself, p1, p2 and on, and calls
/// its own player "local", so a list is right only beside the ids of the host that sent it. A join that does not find
/// that host with this player's record kept lists nobody until the host it found sends its own list, which follows the
/// snapshot; a rejoin to the host that kept the record keeps the list, and so does a lost link. The worlds here are large,
/// so over a 64 kB/s link a host's answer to a join spans two frames, and the client pumps in between.
/// </summary>
internal static class ParticipantListTests
{
    private static VirtualLinkConditions SlowLink => new() { LatencyTicks = 1, BandwidthBytesPerSecond = 64_000 };

    internal static void AJoinToAnotherHostListsNobodyUntilThatHostDoes()
    {
        // On host A, a is p1 and the traveller p2. The traveller is host B's first guest, so B calls it p1, a's id on A:
        // beside B's ids, A's list would show a as this player. Instead, from B's JoinAccepted on, the traveller's session
        // names B's session and lists nobody, and neither does its players directory, until B's own list arrives behind
        // the snapshot. Then it lists B's players, with B's ids.
        using var rig = new VoiceSessionHarness(SlowLink);
        var hostA = Host(rig, "session-a", "Host A");
        var hostB = Host(rig, "session-b", "Host B");
        VoiceMuteTests.FillWorld(hostB.CreateFacade("world"));
        Join(rig, hostA, "a", "host-a");
        var (traveller, _) = Join(rig, hostA, "traveller", "host-a");
        var session = traveller.CreateFacade("observer");
        That(Rows(session.Snapshot) == "local=Host A^,p1=a,p2=traveller*", "with host A, the traveller is p2 (" + Rows(session.Snapshot) + ")");

        var seen = new List<MultiplayerSessionSnapshot>();
        using var watch = session.SubscribeChanged(seen.Add);
        rig.ConnectTo(hostB, traveller, "traveller", "host-b");
        var joined = new MultiplayerSessionId("session-b");
        for (var tick = 0; tick < 600 && !session.Snapshot.Id.Equals(joined); tick++) rig.Advance();
        That(session.Snapshot.State == MultiplayerSessionState.Synchronizing && Rows(session.Snapshot) == string.Empty &&
             !session.Snapshot.LocalParticipantId.HasValue && !session.Snapshot.HostParticipantId.HasValue,
            "host B accepted the join, and the traveller's session lists nobody (" + Rows(session.Snapshot) + ")");
        That(session.Players.Players.Count == 0 && session.Players.Local == null, "nor does its players directory, the local player included");

        var window = 0;
        for (; window < 600 && session.Snapshot.Participants.Count == 0; window++) rig.Advance();
        That(window > 5, "the window lasts until B's second frame arrives, " + window + " ticks later");
        That(Rows(session.Snapshot) == "local=Host B^,p1=traveller*", "then the session lists B's players, with B's ids (" + Rows(session.Snapshot) + ")");
        That(session.Players.Local is { } local && local.Id.Equals(new ParticipantId("p1")) && local.DisplayName == "traveller",
            "and the players directory's local player is the traveller");
        rig.Flush();
        That(session.Snapshot.State == MultiplayerSessionState.Ready && Rows(session.Snapshot) == "local=Host B^,p1=traveller*", "the traveller is Ready with B");

        foreach (var snapshot in seen)
        {
            var rows = Rows(snapshot);
            That(snapshot.Id.Equals(joined) ? rows.Length == 0 || rows == "local=Host B^,p1=traveller*" : rows == "local=Host A^,p1=a,p2=traveller",
                "every snapshot the session reported lists one host's players with that host's ids (" + snapshot.Id + ": " + rows + ")");
        }

        That(seen.Any(snapshot => snapshot.Id.Equals(joined) && snapshot.Participants.Count == 0), "and some of them fell in the window");
    }

    internal static void ARejoinToAHostThatKeptTheRecordKeepsTheList()
    {
        // The traveller's link to host A drops. While it is down the session still lists A's players, none of them as this
        // player, since no host has accepted it. It rejoins within the grace, so A kept the record and calls it p2 again:
        // through all of A's answer the list stays, with A's ids, and no snapshot ever lists fewer than A's three players.
        using var rig = new VoiceSessionHarness(SlowLink);
        var hostA = Host(rig, "session-a", "Host A");
        VoiceMuteTests.FillWorld(hostA.CreateFacade("world"));
        Join(rig, hostA, "a", "host-a");
        var (traveller, link) = Join(rig, hostA, "traveller", "host-a");
        var session = traveller.CreateFacade("observer");
        var seen = new List<MultiplayerSessionSnapshot>();
        using var watch = session.SubscribeChanged(seen.Add);

        rig.Network.Sever(link);
        for (var tick = 0; tick < 60 && session.Snapshot.State != MultiplayerSessionState.Connecting; tick++) rig.Advance();
        rig.Advance(120);
        That(session.Snapshot.State == MultiplayerSessionState.Connecting && Rows(session.Snapshot) == "local=Host A^,p1=a,p2=traveller" &&
             !session.Snapshot.LocalParticipantId.HasValue,
            "two seconds into the outage, the session lists A's players, none of them as this player (" + Rows(session.Snapshot) + ")");

        rig.ConnectTo(hostA, traveller, "traveller", "host-a");
        for (var tick = 0; tick < 600 && session.Snapshot.State != MultiplayerSessionState.Synchronizing; tick++) rig.Advance();
        var window = 0;
        for (; window < 600 && session.Snapshot.State == MultiplayerSessionState.Synchronizing; window++)
        {
            That(Rows(session.Snapshot) == "local=Host A^,p1=a,p2=traveller*", "while A's answer arrives, the list stays, the traveller p2 (" + Rows(session.Snapshot) + ")");
            rig.Advance();
        }

        That(window > 5 && session.Snapshot.State == MultiplayerSessionState.Ready && Rows(session.Snapshot) == "local=Host A^,p1=a,p2=traveller*",
            "the traveller is Ready again, " + window + " ticks after A accepted it, and listed as before");
        That(seen.Count > 0 && seen.All(snapshot => snapshot.Participants.Count == 3), "no snapshot through the outage and the rejoin lists fewer than three players");
    }

    internal static void ARejoinThatFindsTheRecordGoneListsNobodyUntilTheHostDoes()
    {
        // The traveller's link to host A stays down past the 30-second grace, and A forgets the record. The session still
        // lists what A last sent. Rejoining, the traveller is a new record, p3: in the list it shows, p2 is the traveller,
        // and A calls nobody p2 now. So the session lists nobody until A lists its players again.
        using var rig = new VoiceSessionHarness(SlowLink);
        var hostA = Host(rig, "session-a", "Host A");
        VoiceMuteTests.FillWorld(hostA.CreateFacade("world"));
        Join(rig, hostA, "a", "host-a");
        var (traveller, link) = Join(rig, hostA, "traveller", "host-a");
        rig.Network.Sever(link);
        rig.Advance(31 * 60);
        That(traveller.Snapshot.State == MultiplayerSessionState.Connecting && Rows(traveller.Snapshot) == "local=Host A^,p1=a,p2=traveller" &&
             !hostA.Snapshot.TryGetParticipant(new ParticipantId("p2"), out _),
            "past the grace, A has forgotten p2, and the traveller's session still lists what A last sent");

        rig.ConnectTo(hostA, traveller, "traveller", "host-a");
        for (var tick = 0; tick < 600 && traveller.Snapshot.State != MultiplayerSessionState.Synchronizing; tick++) rig.Advance();
        That(traveller.Snapshot.State == MultiplayerSessionState.Synchronizing && Rows(traveller.Snapshot) == string.Empty &&
             !traveller.Snapshot.LocalParticipantId.HasValue,
            "A accepts the traveller as a new record, and the session lists nobody (" + Rows(traveller.Snapshot) + ")");
        rig.Flush();
        That(traveller.Snapshot.State == MultiplayerSessionState.Ready && Rows(traveller.Snapshot) == "local=Host A^,p1=a,p3=traveller*",
            "until A lists its players, the traveller now p3 (" + Rows(traveller.Snapshot) + ")");
    }

    internal static void TwoProcessesUnderOneIdentityNeverShareAList()
    {
        // Two copies of the game that share one data folder host under one identity, so the traveller's links know both
        // as host-a. The second copy, whose world is large, took the traveller first, as p1. The traveller then moved to
        // the first copy, where a is p1 and the traveller p2. Back within the grace, it finds the second copy with its
        // record kept, as p1: beside that id, the first copy's list would show a as this player. The ids are not the ones
        // the list was given with, so the session lists nobody until the second copy lists its own players.
        using var rig = new VoiceSessionHarness(SlowLink);
        var first = Host(rig, "first-copy", "Host A");
        var second = Host(rig, "second-copy", "Host A2");
        VoiceMuteTests.FillWorld(second.CreateFacade("world"));
        var (traveller, _) = Join(rig, second, "traveller", "host-a");
        That(Rows(traveller.Snapshot) == "local=Host A2^,p1=traveller*", "with the second copy, the traveller is p1 (" + Rows(traveller.Snapshot) + ")");
        Join(rig, first, "a", "host-a");
        rig.ConnectTo(first, traveller, "traveller", "host-a");
        rig.Flush();
        That(Rows(traveller.Snapshot) == "local=Host A^,p1=a,p2=traveller*", "with the first copy, it is p2 (" + Rows(traveller.Snapshot) + ")");

        rig.ConnectTo(second, traveller, "traveller", "host-a");
        var back = new MultiplayerSessionId("second-copy");
        for (var tick = 0; tick < 600 && !traveller.Snapshot.Id.Equals(back); tick++) rig.Advance();
        That(traveller.Snapshot.State == MultiplayerSessionState.Synchronizing && Rows(traveller.Snapshot) == string.Empty &&
             !traveller.Snapshot.LocalParticipantId.HasValue,
            "the second copy kept the record, as p1, and the session lists nobody (" + Rows(traveller.Snapshot) + ")");
        rig.Flush();
        That(Rows(traveller.Snapshot) == "local=Host A2^,p1=traveller*", "until the second copy lists its players (" + Rows(traveller.Snapshot) + ")");
    }

    internal static void AJoinWhoseAnswerWasLostListsNobodyUntilTheHostDoes()
    {
        // Host A's process dies and starts again under the same identity, with a large world. With the first process x was
        // p1, a p2 and the traveller p3; c and d join the new one first, as p1 and p2. The new process accepts the
        // traveller's first join, making its record, p3, but the link goes before the answer arrives. Joining again, the
        // traveller hears its record was kept, as p3: the record is the new process's, and the list it shows is the old
        // one's. A join whose answer was lost counts as one that found the record lost, so the session lists nobody until
        // the new process lists its players, rather than the old process's x and a in the new session.
        using var rig = new VoiceSessionHarness(SlowLink);
        var firstRun = Host(rig, "first-run", "Host");
        var links = new List<VirtualLinkEnd>();
        foreach (var identity in new[] { "x", "a" }) links.Add(Join(rig, firstRun, identity, "host-a").Link);
        var (traveller, travellerLink) = Join(rig, firstRun, "traveller", "host-a");
        links.Add(travellerLink);
        That(Rows(traveller.Snapshot) == "local=Host^,p1=x,p2=a,p3=traveller*", "with the first process, the traveller is p3 (" + Rows(traveller.Snapshot) + ")");
        foreach (var link in links) rig.Network.Sever(link);
        firstRun.Shutdown(MultiplayerSessionEndReason.HostEnded);

        var secondRun = Host(rig, "second-run", "Host");
        VoiceMuteTests.FillWorld(secondRun.CreateFacade("world"));
        Join(rig, secondRun, "c", "host-a");
        Join(rig, secondRun, "d", "host-a");
        var lost = rig.ConnectTo(secondRun, traveller, "traveller", "host-a");
        for (var tick = 0; tick < 600 && !secondRun.Snapshot.Participants.Any(participant => participant.DisplayName == "traveller"); tick++) rig.Advance();
        That(traveller.Snapshot.State == MultiplayerSessionState.Connecting && secondRun.Snapshot.TryGetParticipant(new ParticipantId("p3"), out var made) &&
             made!.DisplayName == "traveller", "the new process made the traveller's record, p3, and its answer is on the way");
        rig.Network.Sever(lost);
        rig.Advance(2);
        That(traveller.Snapshot.State == MultiplayerSessionState.Connecting && Rows(traveller.Snapshot) == "local=Host^,p1=x,p2=a,p3=traveller",
            "the link went before the answer; the session still lists what the old process sent (" + Rows(traveller.Snapshot) + ")");

        rig.ConnectTo(secondRun, traveller, "traveller", "host-a");
        var joined = new MultiplayerSessionId("second-run");
        for (var tick = 0; tick < 600 && !traveller.Snapshot.Id.Equals(joined); tick++) rig.Advance();
        That(traveller.Snapshot.State == MultiplayerSessionState.Synchronizing && Rows(traveller.Snapshot) == string.Empty,
            "the second answer says the record was kept, and the session lists nobody (" + Rows(traveller.Snapshot) + ")");
        rig.Flush();
        That(traveller.Snapshot.State == MultiplayerSessionState.Ready && Rows(traveller.Snapshot) == "local=Host^,p1=c,p2=d,p3=traveller*",
            "until the new process lists its players (" + Rows(traveller.Snapshot) + ")");
    }

    internal static void TheListGenerationMovesOnlyWhenTheListIsLetGo()
    {
        // Whatever a consumer keeps per participant id, such as a voice stream, goes when the engine lets a list go. Over a
        // fast link host B's whole answer arrives in one frame, so the session never lists nobody on the way from A's list
        // to B's, and the generation alone says the ids changed hands. It moves at that join and on going back to solo
        // play, and never at a stall, a lost link or a rejoin that keeps the list.
        using var rig = new VoiceSessionHarness();
        var hostA = Host(rig, "session-a", "Host A");
        var hostB = Host(rig, "session-b", "Host B");
        Join(rig, hostA, "a", "host-a");
        var (traveller, link) = Join(rig, hostA, "traveller", "host-a");
        var generation = traveller.ParticipantListGeneration;
        var directory = traveller.CreateFacade("players-observer").Players;
        That(directory.TryGet(new ParticipantId("p1"), out var oldPlayer), "the directory finds A's first guest");
        var oldInput = oldPlayer!.Input;
        var oldUi = oldPlayer.Ui;
        foreach (var end in new[] { link, link.Peer! })
        {
            end.State = EngineLinkState.Stalled;
            end.Outbound!.Held = true;
        }

        rig.Advance(30);
        That(traveller.Snapshot.State == MultiplayerSessionState.Reconnecting && traveller.ParticipantListGeneration == generation, "a stall keeps the list");
        foreach (var end in new[] { link, link.Peer! })
        {
            end.State = EngineLinkState.Open;
            end.Outbound!.Held = false;
        }

        rig.Flush();
        rig.Network.Sever(link);
        rig.Advance(10);
        That(traveller.Snapshot.State == MultiplayerSessionState.Connecting && Rows(traveller.Snapshot) == "local=Host A^,p1=a,p2=traveller" &&
             traveller.ParticipantListGeneration == generation, "so does a lost link");
        rig.ConnectTo(hostA, traveller, "traveller", "host-a");
        rig.Flush();
        That(Rows(traveller.Snapshot) == "local=Host A^,p1=a,p2=traveller*" && traveller.ParticipantListGeneration == generation,
            "and a rejoin to the host that kept the record");
        That(directory.TryGet(oldPlayer.Id, out var rejoined) && ReferenceEquals(oldPlayer, rejoined), "a retained list preserves its handles over reconnect");

        var seen = new List<int>();
        using var watch = traveller.CreateFacade("observer").SubscribeChanged(snapshot => seen.Add(snapshot.Participants.Count));
        rig.ConnectTo(hostB, traveller, "traveller", "host-b");
        rig.Flush();
        That(Rows(traveller.Snapshot) == "local=Host B^,p1=traveller*" && seen.All(count => count > 0) && traveller.ParticipantListGeneration == generation + 1,
            "a join to another host lets the list go, though the session never listed nobody (" + string.Join(",", seen) + ")");
        That(!oldPlayer.IsPresent && directory.TryGet(oldPlayer.Id, out var replacement) && !ReferenceEquals(oldPlayer, replacement),
            "a fast join cannot retarget the old handle to B's p1");
        That(!oldInput.IsHeld("fire") && oldUi.ShowToast("old").ErrorCode == ModErrorCode.NotFound,
            "retained input and UI are retired with the old incarnation");
        traveller.SwitchToSolo(new SoloSessionStart(new MultiplayerSessionId("solo"), new SessionSeed(4), new ParticipantId("local"), "traveller"));
        That(traveller.ParticipantListGeneration == generation + 2, "and so does going back to solo play");
        That(directory.Players.Count == 1 && directory.Local!.IsLocal && directory.Local.IsHost, "returning to solo leaves exactly one player");
    }

    /// <summary>
    /// Describes a snapshot's list in order: each participant's id and name, then ^ for the host and * for this process's
    /// player. An empty list is an empty string.
    /// </summary>
    internal static string Rows(MultiplayerSessionSnapshot snapshot) =>
        string.Join(",", snapshot.Participants.Select(participant =>
            participant.Id.Value + "=" + participant.DisplayName + (participant.IsHost ? "^" : string.Empty) + (participant.IsLocal ? "*" : string.Empty)));

    /// <summary>Starts a host that numbers its players itself and calls its own player "local", as a live host does.</summary>
    private static SessionEngine Host(VoiceSessionHarness rig, string session, string name) =>
        rig.Track(SessionEngine.CreateHost(
            VoiceMuteTests.Numbered(rig),
            MultiplayerProcessKind.Interactive,
            new MultiplayerSessionId(session),
            new SessionSeed(3),
            new ParticipantId("local"),
            name,
            rig.NowMicros));

    /// <summary>Joins a new client to <paramref name="host"/>, which its link knows as <paramref name="hostIdentity"/>, until it is ready.</summary>
    private static (SessionEngine Engine, VirtualLinkEnd Link) Join(VoiceSessionHarness rig, SessionEngine host, string identity, string hostIdentity)
    {
        var engine = rig.Track(SessionEngine.CreateClient(rig.Options, new MultiplayerSessionId("joining"), rig.NowMicros));
        var link = rig.ConnectTo(host, engine, identity, hostIdentity);
        rig.Flush();
        That(engine.Snapshot.State == MultiplayerSessionState.Ready, identity + " joins");
        return (engine, link);
    }
}
