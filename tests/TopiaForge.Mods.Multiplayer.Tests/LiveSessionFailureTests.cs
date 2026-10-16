extern alias provider;

using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Crypto;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The ways a live session can go wrong and must still end cleanly: links lost early, silent or repeated connections,
/// networks that fail to open or give up, a host that times out, files that cannot be read, and closing mid-save.
/// </summary>
internal static class LiveSessionFailureTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();

    internal static void ALinkLostBeforeTheSessionStartsEndsTheJoin()
    {
        using var rig = new LiveRig(140);
        Register(rig.Host);
        rig.StartHosting(new HostSettings("Flaky") { Port = 0 });
        var guest = rig.AddClient("guest");
        Register(guest);
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.Synchronizing), "the link comes up");

        // The link dies before the host admits the player: the join must end, not wait for ever.
        var hostNode = rig.HostFactory.Networks[^1].Node;
        rig.Network.Partition(rig.NodeOf(guest), hostNode, true);
        That(rig.Run(() => join.Phase == JoinPhase.Ended, 120_000_000), "a link lost before the session started ends the join (" + join.Phase + ")");
        That(join.Failure == LiveFailure.ConnectionLost && IsSolo(guest.Snapshot), "as a lost connection, back in solo play (" + join.Failure + ")");
        rig.Network.Partition(rig.NodeOf(guest), hostNode, false);
        var again = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => again.Phase == JoinPhase.InSession, 30_000_000), "and the player can join again (" + again.Phase + ", " + again.Failure + ")");
    }

    internal static void SilentConnectionsCannotFillAHost()
    {
        using var rig = new LiveRig(141);
        Register(rig.Host);
        rig.StartHosting(new HostSettings("Small", 2) { Port = 0 });
        var hostNode = rig.HostFactory.Networks[^1].Node;
        var hostKey = rig.Host.Live!.Host!.Descriptor!.Descriptor.TransportKey;
        var silent = new List<ConnectAttempt>();
        for (var index = 0; index < 4; index++)
        {
            var options = new TransportOptions { Random = new SeededTransportRandom(1410 + index) };
            var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(141, 10 + index), options, rig.Network.Now));
            rig.Network.AddStack("silent-" + index, stack);
            var attempt = new ConnectAttempt();
            stack.Connect(attempt, VirtualDatagramNetwork.PathTo(hostNode, TransportPathKind.Lan), hostKey, rig.Network.Now);
            silent.Add(attempt);
        }

        That(rig.Run(() => silent.All(attempt => attempt.State == ConnectAttemptState.Established)), "four peers connect and never ask to join");
        rig.Run(() => false, 11_000_000);
        var guest = rig.AddClient("guest");
        Register(guest);
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.InSession, 30_000_000),
            "the host lets the silent links go, so a real player still gets in (" + join.Phase + ", " + join.Failure + ")");
    }

    internal static void OneSourceHoldsOnlyItsShareOfAHost()
    {
        var network = new VirtualDatagramNetwork(142);
        var host = new NetworkStack(
            new TransportEndpoint(TransportPair.Key(142, 1), new TransportOptions { Random = new SeededTransportRandom(1420), RelayOnly = false }, network.Now),
            maximumLinksPerSource: 2,
            sourceOf: _ => "one household");
        var hostNode = network.AddStack("host", host);
        var hostKey = X25519.DerivePublicKey(TransportPair.Key(142, 1));
        var attempts = new List<ConnectAttempt>();
        for (var index = 0; index < 3; index++)
        {
            var client = new NetworkStack(new TransportEndpoint(TransportPair.Key(142, 10 + index), new TransportOptions { Random = new SeededTransportRandom(1421 + index), RelayOnly = false }, network.Now));
            network.AddStack("client-" + index, client);
            var attempt = new ConnectAttempt();
            client.Connect(attempt, VirtualDatagramNetwork.PathTo(hostNode), hostKey, network.Now);
            attempts.Add(attempt);
        }

        var accepted = 0;
        network.RunUntil(() =>
        {
            while (host.TryAccept(out _)) accepted++;
            return host.RefusedForSource == 1;
        }, 5_000_000);
        network.RunFor(1_000_000);
        while (host.TryAccept(out _)) accepted++;
        That(accepted == 2 && host.RefusedForSource == 1, "a third link from one source is closed (" + accepted + " accepted)");

        // Sources that cannot be told apart, such as a relay's, are never capped.
        That(UdpPaths.SourceOf(new TransportPath(new byte[] { 2, 7 }, TransportPathKind.Relayed)) == null, "a relay path names no source");
        var portA = UdpPaths.For(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.20"), 1000));
        var portB = UdpPaths.For(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.20"), 2000));
        var other = UdpPaths.For(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.21"), 1000));
        That(UdpPaths.SourceOf(portA) == UdpPaths.SourceOf(portB) && UdpPaths.SourceOf(portA) != UdpPaths.SourceOf(other),
            "a UDP source is its address, whatever the port");
    }

    internal static void JoiningPlayersAndBrowsersAcceptNobody()
    {
        using var rig = new LiveRig(143);
        Register(rig.Host);
        rig.StartHosting(new HostSettings("Host") { Port = 0 });
        var guest = rig.AddClient("guest");
        Register(guest);
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "the guest joins");
        var guestStack = rig.Factories[guest].Networks[^1].Stack;
        That(!guestStack.Endpoint.Options.AcceptsInbound, "a joining player's endpoint accepts no peers");

        var stranger = new NetworkStack(new TransportEndpoint(TransportPair.Key(143, 20), new TransportOptions { Random = new SeededTransportRandom(1430) }, rig.Network.Now));
        rig.Network.AddStack("stranger", stranger);
        var attempt = new ConnectAttempt();
        stranger.Connect(attempt, VirtualDatagramNetwork.PathTo(rig.NodeOf(guest), TransportPathKind.Lan), null, rig.Network.Now);
        That(rig.Run(() => attempt.State == ConnectAttemptState.Failed, 20_000_000) && guestStack.Endpoint.Statistics.InitiationsRefused > 0 &&
             guestStack.LinkCount == 1, "a stranger cannot connect to a joining player or learn its key");

        // The network thread's events are bounded: past the bound they are dropped, and a link nobody will take is closed.
        using var thread = new NetworkThread("TopiaForge test events", new UdpSocketDriver(0, System.Net.IPAddress.Loopback),
            new NetworkStack(new TransportEndpoint(TransportPair.Key(143, 30), new TransportOptions(), 0)), () => 0);
        for (var index = 0; index < NetworkThread.MaximumEvents + 10; index++) thread.Raise(NetworkEvent.Failed(new InvalidOperationException()));
        var key = new byte[32];
        key[0] = 9;
        var stray = new NetworkLink(1, key, default, new byte[32]);
        thread.Raise(NetworkEvent.Accepted(stray));
        That(thread.PendingEvents == NetworkThread.MaximumEvents && stray.CloseRequested, "events stop at the bound, and a stray link is closed");
        That(thread.TryTakeEvent(out _) && thread.PendingEvents == NetworkThread.MaximumEvents - 1, "and taking one makes room");
    }

    internal static void ClosingSavesWhatChangedAndUnreadableFilesAreEmpty()
    {
        using var rig = new LiveRig(144);
        Register(rig.Host);
        rig.HostFiles.FailReadsOf.Add(BanList.FilePath);
        rig.HostFiles.FailReadsOf.Add(KnownHosts.FilePath);
        rig.StartHosting(new HostSettings("Careful") { Port = 0 });
        That(rig.Host.Live!.HostPhase == HostPhase.Hosting, "unreadable ban and known-host files read as empty, so hosting still starts");
        rig.HostFiles.FailReadsOf.Clear();

        var first = rig.AddClient("first");
        var second = rig.AddClient("second");
        var joins = new[] { first, second }.Select(client =>
        {
            Register(client);
            return rig.Join(client, new JoinSettings(client == first ? "First" : "Second"));
        }).ToArray();
        That(rig.Run(() => joins.All(join => join.Phase == JoinPhase.InSession)), "two players join");

        // Two bans, the first saved slowly, and the game closes at once: both must land.
        rig.HostFiles.BeforeWrite = path => path == BanList.FilePath ? Task.Delay(100) : Task.CompletedTask;
        rig.Host.Live.Ban(rig.Host.Snapshot.Participants.Single(participant => participant.DisplayName == "First").Id, "First");
        rig.Host.Live.Ban(rig.Host.Snapshot.Participants.Single(participant => participant.DisplayName == "Second").Id, "Second");
        rig.Host.Dispose();
        int saved;
        lock (rig.HostFiles.Contents) saved = rig.HostFiles.Contents.TryGetValue(BanList.FilePath, out var bytes) ? BanList.Parse(bytes).Count : 0;
        That(saved == 2, "closing waits for both bans to be saved (" + saved + ")");
    }

    internal static void ASaveSlowerThanClosingStillLands()
    {
        using var rig = new LiveRig(148);
        Register(rig.Host);
        rig.StartHosting(new HostSettings("Patient host") { Port = 0 });
        var guest = rig.AddClient("guest");
        Register(guest);
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "the guest joins");

        // The save takes longer than closing waits for it; it must still land, because closing never cancels a save.
        rig.HostFiles.BeforeWrite = path => path == BanList.FilePath ? Task.Delay(1_500) : Task.CompletedTask;
        rig.Host.Live!.Ban(rig.Host.Snapshot.Participants.Single(participant => participant.DisplayName == "Guest").Id, "Guest");
        rig.Host.Dispose();
        var deadline = Environment.TickCount64 + 5_000;
        var saved = 0;
        while (saved == 0 && Environment.TickCount64 < deadline)
        {
            lock (rig.HostFiles.Contents) saved = rig.HostFiles.Contents.TryGetValue(BanList.FilePath, out var bytes) ? BanList.Parse(bytes).Count : 0;
            if (saved == 0) Thread.Sleep(20);
        }

        That(saved == 1, "a save still running when the game closed lands afterwards");
    }

    internal static void ClosingSavesAKnownHostRecordedOffTheMainThread()
    {
        using var rig = new LiveRig(149);
        var guest = rig.AddClient("guest");
        guest.Live!.PrepareAsync();
        That(rig.Run(() => guest.Live.IsReady), "the identity loads");

        // The resolver records a typed address on its own thread and marks the record; closing before the next frame
        // must still save it.
        lock (guest.Live.KnownHosts) guest.Live.KnownHosts.Remember("192.168.1.20", 27250, new string('a', 64), 1);
        guest.Live.MarkKnownHostsChanged();
        var files = rig.ClientFiles(guest);
        guest.Dispose();
        byte[]? saved;
        lock (files.Contents) files.Contents.TryGetValue(KnownHosts.FilePath, out saved);
        That(saved != null && KnownHosts.Parse(saved).Check("192.168.1.20", 27250, new string('a', 64)) == KnownHostCheck.Match,
            "closing saves a known host recorded since the last frame");
    }

    internal static void JoinsEndCleanlyWhenTheNetworkFails()
    {
        using var rig = new LiveRig(145);
        Register(rig.Host);
        rig.StartHosting(new HostSettings("Host") { Port = 0 });

        // The join's own network cannot open.
        var unlucky = rig.AddClient("unlucky");
        Register(unlucky);
        rig.Factories[unlucky].FailStarts = true;
        var failed = rig.Join(unlucky, new JoinSettings("Unlucky"));
        That(rig.Run(() => failed.Phase == JoinPhase.Ended) && failed.Failure == LiveFailure.Unreachable && IsSolo(unlucky.Snapshot),
            "a network that cannot open ends the join, solo play untouched (" + failed.Failure + ")");

        // A host that never answers times out, rather than being called an impostor.
        var silent = rig.Network.AddRaw("silent");
        var descriptor = rig.Host.Live!.Host!.Descriptor!;
        rig.Resolver.Override = ResolvedTarget.Found(VirtualDatagramNetwork.PathTo(silent, TransportPathKind.Lan), descriptor);
        var patient = rig.AddClient("patient");
        Register(patient);
        var waiting = rig.Join(patient, new JoinSettings("Patient"));
        That(rig.Run(() => waiting.Phase == JoinPhase.Ended, 20_000_000) && waiting.Failure == LiveFailure.TimedOut,
            "a host that never answers times out (" + waiting.Failure + ")");

        // A typed address that serves another server now names it, and only that server can be trusted next time.
        var changedId = new string('e', 64);
        rig.Resolver.Override = ResolvedTarget.IdentityChanged(changedId);
        var careful = rig.AddClient("careful");
        Register(careful);
        var changed = rig.Join(careful, new JoinSettings("Careful"));
        That(rig.Run(() => changed.Phase == JoinPhase.Ended) && changed.Failure == LiveFailure.HostIdentityChanged && changed.ChangedServerId == changedId,
            "a changed identity names the server the address serves now");
        rig.Resolver.Override = null;
        var trusting = rig.Join(careful, new JoinSettings("Careful") { TrustedServerId = changed.ChangedServerId });
        That(rig.Run(() => trusting.Phase != JoinPhase.Resolving) && rig.Resolver.LastTrustedServerId == changedId,
            "and a new join trusts exactly that server");
    }

    internal static void AHostWhoseNetworkStopsEndsEveryoneAndPlaysOn()
    {
        using var rig = new LiveRig(146);
        Register(rig.Host);
        rig.StartHosting(new HostSettings("Host") { Port = 0 });
        var guest = rig.AddClient("guest");
        Register(guest);
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "the guest joins");
        rig.HostFactory.Networks[^1].FailNextEvent = true;
        That(rig.Run(() => rig.Host.Live!.HostPhase == HostPhase.Failed) && rig.Host.Live!.HostFailure == LiveFailure.NetworkStopped,
            "the host notices its network gave up (" + rig.Host.Live!.HostPhase + ")");
        That(rig.Host.Snapshot.Participants.Count == 1 && rig.Host.Live.Host == null, "and plays on alone, ready to host again");
        That(rig.Run(() => join.Phase == JoinPhase.Ended, 60_000_000), "the guest's session ends too (" + join.Failure + ")");
    }

    internal static void EveryEndingWipesThePasswordKey()
    {
        using var rig = new LiveRig(147);
        Register(rig.Host);
        rig.StartHosting(new HostSettings("Locked") { Port = 0, Password = "open sesame" });
        var guest = rig.AddClient("guest");
        Register(guest);
        var wrong = rig.Join(guest, new JoinSettings("Guest") { Password = "guess" });
        That(rig.Run(() => wrong.Phase == JoinPhase.Ended) && wrong.Failure == LiveFailure.Rejected && !wrong.HoldsPasswordKey,
            "a refused proof leaves no key behind");
        var right = rig.Join(guest, new JoinSettings("Guest") { Password = "open sesame" });
        That(rig.Run(() => right.Phase == JoinPhase.InSession) && right.HoldsPasswordKey, "an admitted player keeps the key for reconnecting");
        var guestId = rig.Host.Snapshot.Participants.Single(participant => participant.DisplayName == "Guest").Id;
        That(rig.Host.Live!.Host!.Kick(guestId, "Time to go."), "the host kicks the guest");
        That(rig.Run(() => right.Phase == JoinPhase.Ended) && right.Failure == LiveFailure.Kicked && !right.HoldsPasswordKey,
            "and a kick wipes it");
    }

    private static bool IsSolo(MultiplayerSessionSnapshot snapshot) =>
        snapshot.State == MultiplayerSessionState.Ready && snapshot.LocalParticipantId.HasValue && snapshot.HostParticipantId.HasValue &&
        snapshot.LocalParticipantId.Value.Equals(snapshot.HostParticipantId.Value) && snapshot.Participants.Count == 1;

    private static void Register(IMultiplayerSession session)
    {
        if (!session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)).TryGetValue(out _))
        {
            throw new InvalidOperationException("Assertion failed: the score registers");
        }
    }
}
