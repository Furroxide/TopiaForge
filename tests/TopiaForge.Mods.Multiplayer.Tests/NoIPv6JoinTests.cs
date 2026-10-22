extern alias provider;

using System.Net;
using System.Net.Sockets;
using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A join that can only reach its host over IPv6, from a computer whose multiplayer sockets run over IPv4 alone, ends at
/// once with a reason of its own, in words that name no address; every other join goes on as before, and nothing changes
/// where IPv6 exists. The diagnostics say once that the network runs over IPv4 only. Each check names the system it
/// models: the existing seam opens the IPv4 socket a system without IPv6 gets, on any machine.
/// </summary>
internal static class NoIPv6JoinTests
{
    private const string ServerId = "c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3";
    private const string Detail = "This computer has no IPv6, so it cannot reach this address; ask the host for an IPv4 address or an invite.";
    private const string IPv4OnlyLine = "Multiplayer [live.network-ipv4-only]: The multiplayer network runs over IPv4 only, because this computer has no IPv6.";

    /// <summary>
    /// Under the seam, an IPv6 address, typed or in a direct link, and a name that resolves to IPv6 alone fail as NoIPv6
    /// before anything is fetched, relay-only included. An IPv4 address, a name with one, plain or mapped into IPv6, a name
    /// with none and a LAN session resolve exactly as before, and none of them even asks whether the system has IPv6. Only
    /// the refusal a system without IPv6 gives makes the system's answer IPv4 only.
    /// </summary>
    internal static void WithoutIPv6AnIPv6AddressFailsAtOnce()
    {
        var probes = 0;
        var resolver = Resolver(() =>
        {
            Interlocked.Increment(ref probes);
            return IPSockets.RunsIPv4Only(withoutIPv6: true);
        }, out var contacted, out var lookups);
        That((int)LiveFailure.NoIPv6 == 41 && (int)InternalLiveFailure.NoIPv6 == 41 &&
             provider::TopiaForge.Multiplayer.InternalControl.ToFailure(LiveFailure.NoIPv6) == InternalLiveFailure.NoIPv6,
            "the failure is 41, and the internal control reports it under the same code");
        That(IPSockets.RunsIPv4Only(withoutIPv6: true) && IPSockets.RunsIPv4Only() == !Socket.OSSupportsIPv6,
            "the seam runs the sockets over IPv4 alone, and without it they do so exactly where the system has no IPv6");

        // A probe refused for any other reason, such as running out of sockets, says nothing about IPv6, so a passing failure
        // never ends a join as NoIPv6.
        That(IPSockets.RunsIPv4Only(SocketError.AddressFamilyNotSupported), "the refusal a system without IPv6 gives reads as IPv4 only");
        foreach (var refusal in new[]
                 {
                     SocketError.TooManyOpenSockets, SocketError.NoBufferSpaceAvailable, SocketError.AccessDenied,
                     SocketError.ProtocolNotSupported, SocketError.SocketNotSupported, SocketError.NetworkDown,
                 })
        {
            That(!IPSockets.RunsIPv4Only(refusal), "a probe refused with " + refusal + " says nothing about IPv6, so it does not read as IPv4 only");
        }

        // Global, loopback, link-local, unique-local and translated addresses alike: none is reachable over IPv4.
        foreach (var host in new[] { "2001:db8::7", "::1", "fe80::1", "fd00::5", "64:ff9b::cb00:7105" })
        {
            foreach (var serverId in new[] { null, ServerId })
            {
                foreach (var allowDirect in new[] { true, false })
                {
                    var resolved = Resolve(resolver, JoinTarget.Direct(host, 27250, serverId), allowDirect);
                    That(resolved.Failure == LiveFailure.NoIPv6 && resolved.Detail == Detail && contacted.Count == 0,
                        host + (serverId == null ? " typed" : " in a direct link") + (allowDirect ? string.Empty : " under relay-only") +
                        " fails at once as NoIPv6, with nothing contacted and before privacy's question (" + resolved.Failure + ": " + resolved.Detail + ")");
                }
            }
        }

        var name = Resolve(resolver, JoinTarget.Direct("v6only.example", 27250), allowDirect: true);
        That(name.Failure == LiveFailure.NoIPv6 && lookups.Contains("v6only.example") && contacted.Count == 0,
            "a name that resolves to IPv6 alone fails the same way, after its lookup and before anything is sent (" + name.Failure + ")");
        That(Resolve(resolver, JoinTarget.Direct("printer.local", 27250), allowDirect: false).Failure == LiveFailure.NoIPv6 && contacted.Count == 0,
            "and so does a local name, which a relay-only player still looks up");
        var looked = lookups.Count;
        That(Resolve(resolver, JoinTarget.Direct("v6only.example", 27250), allowDirect: false).Failure == LiveFailure.DirectNotAllowed && lookups.Count == looked,
            "a relay-only player still looks up no name beyond the local network, so such a name is refused for privacy first, as before");

        // Everything with an IPv4 address goes on exactly as before, and the system is not even asked.
        var asked = probes;
        var both = Resolve(resolver, JoinTarget.Direct("both.example", 27250), allowDirect: true);
        That(both.Failure == LiveFailure.None && contacted.Count == 1 && contacted[0].Equals(new IPEndPoint(IPAddress.Parse("203.0.113.5"), 27250)),
            "a name with an IPv4 address is fetched there first, as before (" + both.Failure + ")");
        var mapped = Resolve(resolver, JoinTarget.Direct("mapped.example", 27250), allowDirect: true);
        That(mapped.Failure == LiveFailure.None && contacted.Count == 2 && contacted[1].Address.Equals(IPAddress.Parse("::ffff:203.0.113.6")),
            "and so is a name whose one address is an IPv4 address in its IPv6-mapped form, which an IPv4 socket reaches (" + mapped.Failure + ")");
        var typed = Resolve(resolver, JoinTarget.Direct("198.51.100.9", 27250), allowDirect: true);
        That(typed.Failure == LiveFailure.None && typed.Path!.Kind == TransportPathKind.Direct && contacted.Count == 3 &&
             contacted[2].Equals(new IPEndPoint(IPAddress.Parse("198.51.100.9"), 27250)),
            "an IPv4 address is fetched at that address and joined directly, as before (" + typed.Failure + ")");
        var lan = Resolve(resolver, JoinTarget.Direct("192.168.1.20", 27250), allowDirect: false);
        That(lan.Failure == LiveFailure.None && lan.Path!.Kind == TransportPathKind.Lan && contacted.Count == 4, "a LAN address joins under relay-only, as before");
        var nothing = Resolve(resolver, JoinTarget.Direct("nothing.example", 27250), allowDirect: true);
        That(nothing.Failure == LiveFailure.Unreachable && nothing.Detail == "The host name has no usable address.",
            "a name that resolves to nothing proves nothing about IPv6 and ends as before (" + nothing.Failure + ": " + nothing.Detail + ")");
        var heard = Resolve(resolver, JoinTarget.Lan(ServerId), allowDirect: false);
        That(heard.Failure == LiveFailure.NotFound && heard.Detail == "The session is no longer on the local network.",
            "a LAN session is looked for in the LAN list, as before (" + heard.Failure + ")");
        That(probes == asked && contacted.Count == 4, "none of these asks whether the system has IPv6");
    }

    /// <summary>
    /// Where the system has IPv6, every IPv6 target resolves as it always has: fetched at its own address, refused for
    /// privacy where relay-only refuses it, and a name that resolves to IPv6 alone fetched too. Asked itself, the resolver
    /// joins the IPv6 loopback exactly where the system has IPv6, and fails it as NoIPv6 exactly where it has none.
    /// </summary>
    internal static void WhereIPv6ExistsNothingChanges()
    {
        var probes = 0;
        var resolver = Resolver(() =>
        {
            Interlocked.Increment(ref probes);
            return false;
        }, out var contacted, out _);
        var direct = Resolve(resolver, JoinTarget.Direct("2001:db8::7", 27250, ServerId), allowDirect: true);
        That(direct.Failure == LiveFailure.None && direct.Path!.Kind == TransportPathKind.Direct && contacted.Count == 1 &&
             contacted[0].Equals(new IPEndPoint(IPAddress.Parse("2001:db8::7"), 27250)),
            "an IPv6 address in a direct link is fetched at that address and joined directly (" + direct.Failure + ")");
        That(Resolve(resolver, JoinTarget.Direct("2001:db8::7", 27250), allowDirect: false).Failure == LiveFailure.DirectNotAllowed && contacted.Count == 1,
            "a relay-only player is refused it for privacy, with nothing contacted");
        var loopback = Resolve(resolver, JoinTarget.Direct("::1", 27250), allowDirect: false);
        That(loopback.Failure == LiveFailure.None && loopback.Path!.Kind == TransportPathKind.Lan && contacted.Count == 2,
            "the IPv6 loopback joins as a LAN path (" + loopback.Failure + ")");
        var name = Resolve(resolver, JoinTarget.Direct("v6only.example", 27250), allowDirect: true);
        That(name.Failure == LiveFailure.None && contacted.Count == 3 && contacted[2].Address.Equals(IPAddress.Parse("2001:db8::7")),
            "a name that resolves to IPv6 alone is fetched at its first address (" + name.Failure + ")");
        That(probes > 0, "the system was asked, and said it has IPv6");

        var system = new NetworkTargetResolver(() => null, new KnownHostsStore(new MemoryDataFiles()), () => 1_000,
            (_, _, _) => Task.FromResult(new DescriptorFetch(DescriptorFetchOutcome.Fetched, Served(), string.Empty)));
        var own = Resolve(system, JoinTarget.Direct("::1", 27250), allowDirect: false);
        That(own.Failure == (Socket.OSSupportsIPv6 ? LiveFailure.None : LiveFailure.NoIPv6),
            "asked itself, the resolver joins the IPv6 loopback exactly where the system has IPv6 (" + own.Failure + ", IPv6 " +
            (Socket.OSSupportsIPv6 ? "present" : "absent") + ")");
    }

    /// <summary>
    /// Through the internal control, as the game joins: under the seam, a typed IPv6 address and a direct link to one end
    /// at once as NoIPv6, before any network starts or any descriptor is asked for, and CONNECT's check says the same to a
    /// relay-only player instead of asking to show the host this player's address. The diagnostics name the failure only,
    /// and nothing the player or the logs see names the address.
    /// </summary>
    internal static void AJoinThatNeedsIPv6EndsBeforeAnyPacket()
    {
        using var rig = new ControlRig(186);
        var peer = rig.AddPeer("player");
        var fetches = 0;
        rig.Resolver.Inner = new NetworkTargetResolver(() => null, new KnownHostsStore(new MemoryDataFiles()), () => 1_000,
            (endpoint, serverId, token) =>
            {
                Interlocked.Increment(ref fetches);
                return DescriptorClient.FetchAsync(endpoint, serverId, TimeSpan.FromSeconds(2), token, withoutIPv6: true);
            },
            ipv4Only: () => IPSockets.RunsIPv4Only(withoutIPv6: true));

        var texts = new List<string>();
        foreach (var text in new[] { "[2001:db8::7]:27263", "topiaforge://join/direct/" + ServerId + "/2001:db8::7/27263" })
        {
            var started = rig.Network.Now;
            That(peer.Control.Join(peer.Target(text), new InternalJoinSettings { AllowDirect = true }).Succeeded, "the join to " + text + " starts");
            That(rig.Run(() => peer.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "and ends");
            var status = peer.Control.GetStatus();
            That(status.JoinFailure == InternalLiveFailure.NoIPv6 && status.JoinDetail == Detail,
                text + " ends as NoIPv6, in this game's own words (" + status.JoinFailure + ": " + status.JoinDetail + ")");
            That(fetches == 0 && peer.Factory.Networks.Count == 0 && rig.Network.Now - started < 1_000_000,
                "at once, before any network starts or any descriptor is asked for (" + fetches + " fetches, " + peer.Factory.Networks.Count +
                " networks, " + (rig.Network.Now - started) + " µs)");
            texts.Add(status.JoinDetail);
        }

        var check = peer.Control.Query(peer.Target("[2001:db8::7]:27263"), allowDirect: false);
        That(rig.Run(() => check.IsCompleted) && check.Failure == InternalLiveFailure.NoIPv6 && fetches == 0,
            "CONNECT's check says the same to a relay-only player, rather than ask to show the host this player's address for a join that cannot work (" +
            check.Failure + ")");
        texts.Add(check.Detail);
        check.Dispose();

        That(peer.Runtime.RecentDiagnostics.Count(record =>
                record.Code == "live.join-ended" && record.Text.EndsWith("The joined session ended: NoIPv6.", StringComparison.Ordinal)) == 2,
            "each join's end reaches the diagnostics by the failure's name alone");
        texts.AddRange(peer.Log.Lines);
        texts.AddRange(peer.Runtime.RecentDiagnostics.Select(record => record.Text));
        That(!texts.Any(text => text.Contains("2001:db8", StringComparison.OrdinalIgnoreCase) || text.Contains("27263", StringComparison.Ordinal)),
            "and nothing the player or the logs see names the address");
        That(!peer.Runtime.RecentDiagnostics.Any(record => record.Code == "live.network-ipv4-only"), "no network ran, so no network is said to run over IPv4");
    }

    /// <summary>
    /// Under the seam, an invite and a listed server join through the TopiaForge service exactly as before: the service's
    /// route never reaches the network's resolver, so it is never asked whether the system has IPv6.
    /// </summary>
    internal static void InvitesAndListedServersJoinAsBefore()
    {
        using var rig = new InternetRig(7_251);
        var probes = 0;
        var fetches = 0;
        rig.Resolver = () => new NetworkTargetResolver(() => null, new KnownHostsStore(new MemoryDataFiles()), () => 1_000,
            (_, _, _) =>
            {
                Interlocked.Increment(ref fetches);
                return Task.FromResult(new DescriptorFetch(DescriptorFetchOutcome.Unreachable, null, "The host could not be reached."));
            },
            ipv4Only: () =>
            {
                Interlocked.Increment(ref probes);
                return IPSockets.RunsIPv4Only(withoutIPv6: true);
            });
        var host = rig.AddPeer("host", NatBehaviour.PortRestricted, "198.51.100.190", "192.168.1.190");
        var invite = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Public));
        var serverId = rig.Service.Sessions.Keys.Single();
        var invited = rig.AddPeer("invited", NatBehaviour.PortRestricted, "198.51.100.191", "10.0.0.191");
        rig.Join(invited, invite.Display);
        var listed = rig.AddPeer("listed", NatBehaviour.FullCone, "198.51.100.192", "10.0.0.192");
        rig.Join(listed, "topiaforge://join/server/" + serverId);
        That(invited.Status.JoinPhase == InternalJoinPhase.InSession && listed.Status.JoinPhase == InternalJoinPhase.InSession && probes == 0 && fetches == 0,
            "an invite and a listed server join as before, and neither reaches the check (" + probes + " asked, " + fetches + " fetched)");
    }

    /// <summary>
    /// The game's network factory notes the fallback from every game socket it opens, a session's through the TopiaForge
    /// service under the NAT layer included: always under the seam, and without it exactly where the system has no IPv6.
    /// The diagnostics then say once, as information and without an address, that the network runs over IPv4 only; where
    /// IPv6 exists, as on the virtual network, which never falls back, they say nothing new.
    /// </summary>
    internal static void TheDiagnosticsSayWhenTheNetworkRunsIPv4Only()
    {
        foreach (var withoutIPv6 in new[] { true, false })
        {
            var factory = new SocketNetworkFactory(Clock, IPAddress.Loopback, withoutIPv6);
            That(!factory.OpenedIPv4Only, "a factory that opened nothing has nothing to say");
            var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(187, withoutIPv6 ? 1 : 2), new TransportOptions(), Clock()));
            using (factory.Start(0, stack))
            {
                That(factory.OpenedIPv4Only == (withoutIPv6 || !Socket.OSSupportsIPv6),
                    "a game socket opened " + (withoutIPv6 ? "under the seam" : "as the system allows") + " is noted as IPv4 only exactly when it is (" +
                    factory.OpenedIPv4Only + ")");
            }

            // A session through the TopiaForge service opens its game socket under the NAT layer, which contacts nothing
            // until a session starts on it.
            var traversing = new SocketNetworkFactory(Clock, IPAddress.Loopback, withoutIPv6);
            var traversal = traversing.StartTraversal(0,
                new NetworkStack(new TransportEndpoint(TransportPair.Key(187, withoutIPv6 ? 3 : 4), new TransportOptions(), Clock())),
                new provider::TopiaForge.Multiplayer.NatTraversal.NatAgentOptions());
            using (traversal.Network)
            {
                That(traversing.OpenedIPv4Only == (withoutIPv6 || !Socket.OSSupportsIPv6),
                    "and so is one under the NAT layer, for a session through the TopiaForge service (" + traversing.OpenedIPv4Only + ")");
            }
        }

        using var rig = new ControlRig(187);
        var peer = rig.AddPeer("player");
        rig.StartHosting(new InternalHostSettings("Plain host", 8));
        rig.JoinAndWait(peer);
        That(!Lines(rig.Host).Any() && !Lines(peer).Any(), "where IPv6 exists the diagnostics hold no such line");

        // The fallback ran: the host's game socket opened over IPv4 alone.
        rig.Host.Factory.OpenedIPv4Only = true;
        That(rig.Run(() => Lines(rig.Host).Any(), 100_000), "the host's diagnostics say so");
        rig.Run(() => false, 100_000);
        var lines = Lines(rig.Host).ToList();
        That(lines.Count == 1 && lines[0].Severity == InternalDiagnosticSeverity.Info && lines[0].Text == IPv4OnlyLine,
            "once, as information (" + lines.Count + ": " + string.Join(" | ", lines.Select(line => line.Text)) + ")");
        That(rig.Host.Log.Lines.Count(line => line == "info: " + IPv4OnlyLine) == 1, "and the log has it once too");
        That(MultiplayerDiagnosticRedactor.Redact(lines[0].Text) == lines[0].Text, "nothing in it is shaped like an address");
        That(!Lines(peer).Any(), "a player whose network has IPv6 says nothing");
    }

    /// <summary>
    /// A resolver whose every fetch is recorded and finds the shared fixtures' first server, whose names resolve as
    /// <see cref="Names"/> says, and which asks <paramref name="ipv4Only"/> whether the system's sockets run over IPv4 alone.
    /// </summary>
    private static NetworkTargetResolver Resolver(Func<bool> ipv4Only, out List<IPEndPoint> contacted, out List<string> lookups)
    {
        var served = Served();
        var fetched = new List<IPEndPoint>();
        var looked = new List<string>();
        contacted = fetched;
        lookups = looked;
        return new NetworkTargetResolver(() => null, new KnownHostsStore(new MemoryDataFiles()), () => 1_000, (endpoint, _, _) =>
        {
            lock (fetched) fetched.Add(endpoint);
            return Task.FromResult(new DescriptorFetch(DescriptorFetchOutcome.Fetched, served, string.Empty));
        }, ipv4Only, host =>
        {
            lock (looked) looked.Add(host);
            return Names(host);
        });
    }

    /// <summary>
    /// What the test's host names resolve to: IPv6 alone, both families, IPv4 in its mapped form alone, a local name with IPv6
    /// alone, or nothing.
    /// </summary>
    private static Task<IPAddress[]> Names(string host) => Task.FromResult(host switch
    {
        "v6only.example" => new[] { IPAddress.Parse("2001:db8::7"), IPAddress.Parse("2001:db8::8") },
        "both.example" => new[] { IPAddress.Parse("2001:db8::7"), IPAddress.Parse("203.0.113.5") },
        "mapped.example" => new[] { IPAddress.Parse("::ffff:203.0.113.6") },
        "printer.local" => new[] { IPAddress.Parse("fe80::5") },
        _ => Array.Empty<IPAddress>()
    });

    private static SignedServerDescriptor Served()
    {
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        return SignedServerDescriptor.Sign(
            NetworkDescriptorTests.Build(fixture.GetProperty("valid")[0].GetProperty("descriptor")),
            NetworkFixtures.Hex(fixture.GetProperty("keys")[0].Text("signingSeed")));
    }

    private static ResolvedTarget Resolve(NetworkTargetResolver resolver, JoinTarget target, bool allowDirect) =>
        resolver.ResolveAsync(target, allowDirect, null, CancellationToken.None).GetAwaiter().GetResult();

    private static IEnumerable<MultiplayerDiagnosticRecord> Lines(ControlPeer peer) =>
        peer.Runtime.RecentDiagnostics.Where(record => record.Code == "live.network-ipv4-only");

    private static long Clock() => provider::TopiaForge.Multiplayer.MultiplayerProvider.MonotonicMicros();
}
