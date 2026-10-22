extern alias provider;

using System.Net;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.ControlVoiceSteps;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ProviderDrop = provider::TopiaForge.Multiplayer.Engine.VoiceDropReason;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The host's "offers voice chat" setting through the voice SPI, over real providers on the deterministic network with
/// real cryptography, transport, engine and Opus codec: a session that offers no voice carries none, its host's own
/// player's included, and every player's indicator says so; each hosting run applies its own setting, the launcher's
/// planned ones too; and a join shown the session without voice, by its descriptor or its LAN listing, stays without it
/// when the host later says otherwise.
/// </summary>
internal static class InternalVoiceOfferTests
{
    private const double Heard = -35;
    private const double Silent = -80;
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');

    internal static void ASessionWithoutVoiceCarriesNoneThroughTheSpi()
    {
        // The host turned "Offer voice chat" off. Sam and Lee turned voice on, as players who consented do, and so did the
        // host's own player.
        using var rig = ControlRig.WithVoice(360, Sandbox);
        var hoster = HostOffering(rig, voice: false);
        That(!rig.Host.Provider.Live!.Host!.Descriptor!.Descriptor.Voice, "the host's descriptor says its session offers no voice");
        var (sam, samVoice) = Join(rig, "sam", Sandbox);
        var (lee, leeVoice) = Join(rig, "lee", Sandbox);
        RunUntilListed(rig, rig.Host, "sam", "lee");
        RunUntilListed(rig, sam, "hoster", "lee");
        RunUntilListed(rig, lee, "hoster", "sam");
        var everyone = new[] { (Peer: rig.Host, Voice: hoster), (Peer: sam, Voice: samVoice), (Peer: lee, Voice: leeVoice) };
        That(rig.Run(() => everyone.All(member => member.Voice.Service.TransmitState == InternalVoiceTransmitState.NotOffered)),
            "every indicator says the session offers no voice chat, the host's own included");

        hoster.Listen("sam", IdOf(rig.Host, "sam"));
        hoster.Listen("lee", IdOf(rig.Host, "lee"));
        samVoice.Listen("hoster", IdOf(sam, "hoster"));
        samVoice.Listen("lee", IdOf(sam, "lee"));
        leeVoice.Listen("hoster", IdOf(lee, "hoster"));
        leeVoice.Listen("sam", IdOf(lee, "sam"));
        hoster.Talk(Speech(2, 91));
        samVoice.Talk(Speech(2, 92));
        leeVoice.Talk(Speech(2, 93));
        RunFor(rig, 2_500_000);

        foreach (var (peer, voice) in everyone)
        {
            var statistics = voice.Service.GetStatistics();
            That(voice.Service.Enabled && statistics.SubmittedSamples > 48_000 && statistics.FramesEncoded == 0 && statistics.FramesSent == 0,
                peer.Name + "'s microphone was handed in for two seconds, and nothing of it was coded or sent (" + statistics.FramesEncoded + " coded)");
            That(statistics.State == InternalVoiceTransmitState.NotOffered && statistics.FramesReceived == 0 && statistics.Speakers.All(speaker => speaker.Decoded == 0),
                peer.Name + " received nothing, and no playback stream decoded a frame");
            var engine = peer.Provider.Engine.GetVoiceStatistics();
            That(engine.Submitted == 0 && engine.Sent == 0 && engine.Delivered == 0 && engine.TotalDropped == 0,
                peer.Name + "'s engine was handed nothing, so no voice datagram left it and none arrived (" + engine.Sent + " sent)");
        }

        That(rig.Host.Provider.Engine.GetVoiceStatistics().Relayed == 0, "the host relayed nothing");
        foreach (var (voice, label) in new[] { (hoster, "sam"), (hoster, "lee"), (samVoice, "hoster"), (samVoice, "lee"), (leeVoice, "hoster"), (leeVoice, "sam") })
        {
            That(voice.LevelSince(label, 0) < Silent, "nobody heard " + label + " (" + voice.LevelSince(label, 0) + " dBFS)");
        }
    }

    internal static void EachHostingRunOffersVoiceAsItWasToldTo()
    {
        using var rig = ControlRig.WithVoice(361, Sandbox);
        rig.Host.Runtime.PreferredDisplayName = "Hoster";
        var hoster = new ControlVoice(rig.Host, rig.Network);
        hoster.Service.Enabled = true;
        var sam = rig.AddPeer("sam", Sandbox);
        sam.Runtime.PreferredDisplayName = "Sam";
        var samVoice = new ControlVoice(sam, rig.Network);
        samVoice.Service.Enabled = true;
        var run = 0;
        foreach (var offered in new[] { false, true, false })
        {
            run++;
            rig.StartHosting(new InternalHostSettings("Run " + run, 8) { Voice = offered });
            That(rig.Host.Provider.Live!.Host!.Descriptor!.Descriptor.Voice == offered, "run " + run + "'s descriptor says it offers voice: " + offered);
            rig.JoinAndWait(sam);
            RunUntilListed(rig, rig.Host, "sam");
            var label = "sam-" + run;
            hoster.Listen(label, IdOf(rig.Host, "sam"));
            samVoice.Talk(Speech(1.2, (uint)(100 + run)));
            RunFor(rig, 1_600_000);
            var heard = hoster.LevelSince(label, 0);
            if (offered)
            {
                That(heard > Heard && samVoice.Service.TransmitState != InternalVoiceTransmitState.NotOffered &&
                     hoster.Service.TransmitState != InternalVoiceTransmitState.NotOffered, "run " + run + " offers voice: the host hears Sam (" + heard + " dBFS)");
            }
            else
            {
                That(heard < Silent && samVoice.Service.TransmitState == InternalVoiceTransmitState.NotOffered &&
                     hoster.Service.TransmitState == InternalVoiceTransmitState.NotOffered, "run " + run + " offers none: the host hears nothing of Sam (" + heard + " dBFS)");
            }

            samVoice.StopTalking();
            rig.Host.Control.StopHosting();
            That(rig.Run(() => sam.Control.Role == InternalSessionRole.Solo), "run " + run + " ends, and Sam is back in solo play");
            That(rig.Run(() => hoster.Service.TransmitState != InternalVoiceTransmitState.NotOffered &&
                               samVoice.Service.TransmitState != InternalVoiceTransmitState.NotOffered),
                "leaving the role clears the setting on both sides");
        }
    }

    internal static void ALauncherPlannedHostWithoutVoiceHostsWithoutIt()
    {
        // The launcher's hosting defaults say no voice. The provider plans the session from the launch request, and the
        // in-game flows start the plan through live sessions.
        var ticket = new LaunchSessionTicket("host", "intent-quiet", "Sam", false, 0, false, true, null, null,
            new LaunchSessionTicketHost("Sam's quiet session", 4, "lan", voice: false, passwordRequired: false, servePackages: false));
        var plan = LaunchSessionPlan.FromTicket(ticket, out var unsupported);
        That(unsupported == LaunchSessionUnsupported.None && plan!.Host is { Voice: false }, "the plan hosts without voice, as the launcher asked");

        using var rig = ControlRig.WithVoice(362, Sandbox);
        rig.Host.Runtime.PreferredDisplayName = "Hoster";
        rig.Host.Provider.Live!.StartHosting(plan!.Host!);
        That(rig.Run(() => rig.Host.Control.GetStatus().HostPhase == InternalHostPhase.Hosting), "the planned session opens");
        That(!rig.Host.Provider.Live!.Host!.Descriptor!.Descriptor.Voice && !rig.Host.Provider.Engine.IsVoiceOffered, "it announces no voice, and offers none");
        var hoster = new ControlVoice(rig.Host, rig.Network);
        hoster.Service.Enabled = true;
        var (sam, samVoice) = Join(rig, "sam", Sandbox);
        RunUntilListed(rig, rig.Host, "sam");
        hoster.Listen("sam", IdOf(rig.Host, "sam"));
        samVoice.Talk(Speech(1.2, 111));
        RunFor(rig, 1_600_000);
        That(samVoice.Service.TransmitState == InternalVoiceTransmitState.NotOffered && samVoice.Service.GetStatistics().FramesEncoded == 0 &&
             sam.Provider.Engine.GetVoiceStatistics().Sent == 0, "a player who joins it learns so, and sends nothing");
        That(hoster.LevelSince("sam", 0) < Silent && rig.Host.Provider.Engine.GetVoiceStatistics().Relayed == 0 &&
             hoster.Service.TransmitState == InternalVoiceTransmitState.NotOffered, "and the host relays and hears nothing");
    }

    internal static void AJoinShownWithoutVoiceStaysWithoutItWhateverTheHostSays()
    {
        // Lee joins a session its descriptor shows without voice. The host's process then crashes and starts again under the
        // same identity, at the same address, now offering voice, and Lee's join reconnects to it: that host says voice in
        // JoinAccepted. The stricter setting wins, so Lee still sends and plays nothing, while Yul, who joins the new
        // process and was shown voice, is heard.
        using var rig = ControlRig.WithVoice(363, Sandbox);
        HostOffering(rig, voice: false);
        var (lee, leeVoice) = Join(rig, "lee", Sandbox);
        That(rig.Run(() => leeVoice.Service.TransmitState == InternalVoiceTransmitState.NotOffered), "Lee joined a session shown without voice");
        var restarted = rig.RestartHost(Sandbox);
        restarted.Runtime.PreferredDisplayName = "Hoster";
        rig.StartHosting(new InternalHostSettings("Voice host", 8) { Voice = true });
        var hoster = new ControlVoice(restarted, rig.Network);
        hoster.Service.Enabled = true;
        That(rig.Run(() => lee.Control.GetStatus().JoinPhase == InternalJoinPhase.Reconnecting, 20_000_000, 20_000), "Lee's link to the crashed process goes");
        That(rig.Run(() => lee.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession && lee.Session.Snapshot.State == MultiplayerSessionState.Ready, 20_000_000),
            "Lee reconnects to the restarted process");
        var (yul, yulVoice) = Join(rig, "yul", Sandbox);
        RunUntilListed(rig, restarted, "lee", "yul");
        RunUntilListed(rig, lee, "yul");
        That(restarted.Provider.Engine.IsVoiceOffered && yul.Provider.Engine.IsVoiceOffered && yulVoice.Service.TransmitState != InternalVoiceTransmitState.NotOffered,
            "the restarted host offers voice and says so: Yul, shown voice, has it");
        That(!lee.Provider.Engine.IsVoiceOffered && leeVoice.Service.TransmitState == InternalVoiceTransmitState.NotOffered,
            "but Lee's join was shown no voice, and that still holds");

        hoster.Listen("lee", IdOf(restarted, "lee"));
        hoster.Listen("yul", IdOf(restarted, "yul"));
        leeVoice.Listen("yul", IdOf(lee, "yul"));
        var leeBefore = lee.Provider.Engine.GetVoiceStatistics();
        leeVoice.Talk(Speech(1.5, 121));
        yulVoice.Talk(Speech(1.5, 122));
        RunFor(rig, 2_000_000);
        That(hoster.LevelSince("yul", 0) > Heard, "the restarted host hears Yul (" + hoster.LevelSince("yul", 0) + " dBFS)");
        var leeAfter = lee.Provider.Engine.GetVoiceStatistics();
        That(hoster.LevelSince("lee", 0) < Silent && leeAfter.Sent == leeBefore.Sent && leeVoice.Service.GetStatistics().FramesEncoded == 0,
            "but nothing of Lee: no frame of Lee's was coded or sent");
        That(leeVoice.LevelSince("yul", 0) < Silent && leeAfter.Delivered == leeBefore.Delivered &&
             leeAfter.Dropped(ProviderDrop.NotOffered) > leeBefore.Dropped(ProviderDrop.NotOffered),
            "and Lee plays none of Yul's voice the host relays it, counting what it drops (" + leeAfter.Dropped(ProviderDrop.NotOffered) + ")");
    }

    internal static void TheLanListingShownBeforeJoiningCounts()
    {
        // Two sessions on the local network, over real sockets on loopback: the first one's info says it offers no voice,
        // the second's that it does. Whatever descriptor the join then fetches, what the listing showed holds as well.
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        var source = NetworkDescriptorTests.Build(fixture.GetProperty("valid")[0].GetProperty("descriptor"));
        var seed = NetworkFixtures.Hex(fixture.GetProperty("keys")[0].Text("signingSeed"));
        SignedServerDescriptor Signed(bool voice) => SignedServerDescriptor.Sign(
            new ServerDescriptor(source.SigningKey, source.TransportKey, source.IssuedAt, source.Name, source.GameBuild, source.RuntimeVersion,
                source.ProtocolVersion, source.MaxPlayers, source.PasswordRequired, voice, source.Visibility, source.HostServesPackages,
                source.Gamemode, source.World, source.Packages),
            seed);

        var quiet = new string('a', 64);
        var loud = new string('c', 64);
        using var quietHost = StartThread(140);
        quietHost.Post((stack, _) => stack.Endpoint.SetInfo(Info(quiet, voice: false).Encode()));
        using var loudHost = StartThread(141);
        loudHost.Post((stack, _) => stack.Endpoint.SetInfo(Info(loud, voice: true).Encode()));
        using var browserThread = StartThread(142);
        var browser = new LanBrowser(0, expiryMicros: 30_000_000, bindAddress: IPAddress.Loopback);
        browserThread.Attach(browser);
        var targets = new[] { new IPEndPoint(IPAddress.Loopback, browser.LocalPort) };
        var quietAnnouncer = new LanAnnouncer(new LanBeacon(quiet, quietHost.LocalPort), targets, IPAddress.Loopback);
        var loudAnnouncer = new LanAnnouncer(new LanBeacon(loud, loudHost.LocalPort), targets, IPAddress.Loopback);
        quietHost.Attach(quietAnnouncer);
        loudHost.Attach(loudAnnouncer);
        That(WaitFor(() => browser.Servers.Count(server => server.Info != null) == 2, 5_000), "the browser lists both sessions with their info");

        var served = Signed(voice: true);
        var resolver = new NetworkTargetResolver(() => browser, new KnownHostsStore(new MemoryDataFiles()), () => 1_000,
            (endpoint, _, _) => Task.FromResult(new DescriptorFetch(DescriptorFetchOutcome.Fetched, served, string.Empty)));
        ResolvedTarget Resolve(string serverId)
        {
            var resolved = resolver.ResolveAsync(JoinTarget.Lan(serverId), false, null, CancellationToken.None).GetAwaiter().GetResult();
            That(resolved.Failure == LiveFailure.None, "the LAN session resolves (" + resolved.Failure + ")");
            return resolved;
        }

        That(Resolve(quiet).ShownWithoutVoice, "a session the listing showed without voice joins without it, though its descriptor says voice");
        That(!Resolve(loud).ShownWithoutVoice, "one the listing and the descriptor both show with voice joins with it");
        served = Signed(voice: false);
        That(Resolve(loud).ShownWithoutVoice, "and one whose descriptor says no voice joins without it, whatever the listing said");
        quietHost.Detach(quietAnnouncer);
        loudHost.Detach(loudAnnouncer);
    }

    /// <summary>Opens the rig's host to players, offering voice or not, and gives its player a microphone and ears with voice on.</summary>
    private static ControlVoice HostOffering(ControlRig rig, bool voice)
    {
        rig.Host.Runtime.PreferredDisplayName = "Hoster";
        rig.StartHosting(new InternalHostSettings(voice ? "Voice host" : "Quiet host", 8) { Voice = voice });
        var ears = new ControlVoice(rig.Host, rig.Network);
        ears.Service.Enabled = true;
        return ears;
    }

    private static SessionInfo Info(string serverId, bool voice) =>
        new SessionInfo(serverId, new string('b', 64), "LAN Party", "2545", "0.2.0", "1.0.0", "Open Sandbox", null, 2, 8, false, voice);

    /// <summary>A network thread on a loopback socket, as the LAN tests run one.</summary>
    private static NetworkThread StartThread(int seed)
    {
        var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(seed, 1), new TransportOptions(), Clock()));
        var thread = new NetworkThread("TopiaForge test " + seed, new UdpSocketDriver(0, IPAddress.Loopback), stack, Clock);
        thread.Start();
        return thread;
    }

    private static long Clock() => provider::TopiaForge.Multiplayer.MultiplayerProvider.MonotonicMicros();

    private static bool WaitFor(Func<bool> condition, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }

        return condition();
    }
}
