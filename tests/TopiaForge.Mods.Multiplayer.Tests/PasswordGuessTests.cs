extern alias provider;

using System.Net;
using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ProviderEngine = provider::TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The host's password limits, one rule at a time. Wrong proofs count per identity, per source network and for the
/// whole host, so fresh identities, other ports and the other addresses of one /64 buy a guesser nothing, and many
/// networks together meet the host's own budget. Lockouts double while guessing goes on, full tables never forget a
/// limit still in force, and a player the host already holds a place for is never held back.
/// </summary>
internal static class PasswordGuessTests
{
    private const long Minute = 60_000_000;
    private static readonly byte[] HostTransportKey = Enumerable.Repeat((byte)3, 32).ToArray();
    private static readonly Lazy<byte[]> PasswordKey = new(() => PasswordProof.DeriveKey("open sesame", HostTransportKey));

    internal static void WrongProofsCountPerNetworkWhateverTheIdentity()
    {
        var probe = new PolicyProbe(new PasswordGuessLimits(perHost: 1_000_000));
        var home = new SourceNetwork(1, 1);
        var away = new SourceNetwork(2, 2);
        for (var identity = 1; identity <= PasswordGuessLimits.DefaultPerNetwork; identity++)
        {
            That(probe.Code(identity, home, right: false) == AdmissionCode.PasswordWrong, "a fresh identity " + identity + " guesses from one network");
        }

        var sixth = probe.Ask(6, home, right: true);
        That(Code(sixth) == AdmissionCode.TooManyAttempts && sixth.Message.Contains("network", StringComparison.Ordinal),
            "a sixth identity from that network waits, even with the right password (" + sixth.Message + ")");
        That(probe.Ask(7, away, right: true).Accepted, "another network is not held back");
        probe.Now += Minute;
        That(probe.Ask(8, home, right: true).Accepted, "a minute later the network is heard again");

        // Spending the budget again before the last lockout has worn off doubles the next one: two minutes, then four.
        foreach (var lockout in new[] { 2, 4, 8, 8 })
        {
            for (var guess = 0; guess < PasswordGuessLimits.DefaultPerNetwork; guess++) That(probe.Code(100 + guess, home, right: false) == AdmissionCode.PasswordWrong, "guess " + guess);
            probe.Now += (lockout - 1) * Minute;
            That(probe.Code(200, home, right: true) == AdmissionCode.TooManyAttempts, "a lockout of " + lockout + " minutes still holds a minute before it ends");
            probe.Now += Minute;
            That(probe.Ask(201, home, right: true).Accepted, "and is over after " + lockout + " minutes");
        }

        // Waiting does not pay either: a doubling wears off only after eight quiet minutes, so a patient guesser meets
        // a long lockout again, and only a quiet network starts from one minute.
        probe.Now += 7 * Minute;
        for (var guess = 0; guess < PasswordGuessLimits.DefaultPerNetwork; guess++) probe.Code(300 + guess, home, right: false);
        probe.Now += 4 * Minute;
        That(probe.Code(310, home, right: true) == AdmissionCode.TooManyAttempts, "seven quiet minutes leave the lockout at eight minutes");
        probe.Now += 4 * Minute + (4 * 8 * Minute);
        for (var guess = 0; guess < PasswordGuessLimits.DefaultPerNetwork; guess++) probe.Code(400 + guess, home, right: false);
        probe.Now += Minute;
        That(probe.Ask(410, home, right: true).Accepted, "a network quiet for long enough starts again from one minute");

        // One identity is held by its own budget whatever network it uses, and a link whose source cannot be told
        // apart, such as a relay's, meets only the identity and host budgets.
        var traveller = new PolicyProbe(new PasswordGuessLimits(perHost: 1_000_000));
        for (var network = 1; network <= PasswordGuessLimits.DefaultPerIdentity; network++) traveller.Code(1, new SourceNetwork(50, (ulong)network), right: false);
        var stopped = traveller.Ask(1, new SourceNetwork(60, 0), right: true);
        That(Code(stopped) == AdmissionCode.TooManyAttempts && stopped.Message == "Too many wrong passwords; wait a minute.", "an identity is limited whatever network it moves to");
        for (var identity = 2; identity <= 8; identity++) That(traveller.Code(identity, null, right: false) == AdmissionCode.PasswordWrong, "relayed guess " + identity);
        That(traveller.Ask(9, null, right: true).Accepted, "no network budget applies where the source cannot be told");
    }

    internal static void TheHostsBudgetHoldsManyNetworksAndSparesItsPlayers()
    {
        var probe = new PolicyProbe();
        var networks = 0;
        SourceNetwork Fresh() => new((ulong)++networks, 7);
        for (var guess = 0; guess < PasswordGuessLimits.DefaultPerHost; guess++) That(probe.Code(guess, Fresh(), right: false) == AdmissionCode.PasswordWrong, "guess " + guess + " from its own network");
        var newcomer = probe.Ask(100, Fresh(), right: true);
        That(Code(newcomer) == AdmissionCode.TooManyAttempts && newcomer.Message.Contains("try again shortly", StringComparison.Ordinal),
            "once the host's budget is spent every newcomer hears to try again shortly, the right password included (" + newcomer.Message + ")");
        That(probe.Policy.Guesses.HostLocked(probe.Now), "the host turns passwords away");
        That(probe.Ask(101, Fresh(), right: true, returning: true).Accepted, "but a player it already holds a place for comes back");
        That(probe.Code(102, Fresh(), right: false, returning: true) == AdmissionCode.PasswordWrong, "and still has to prove the password");

        // The host's lockout doubles while the guessing goes on, as a network's does.
        foreach (var lockout in new[] { 1, 2, 4, 8, 8 })
        {
            probe.Now += (lockout * Minute) - 1;
            That(Code(probe.Ask(110, Fresh(), right: true)) == AdmissionCode.TooManyAttempts, "a host lockout of " + lockout + " minutes holds to its end");
            probe.Now += 1;
            That(probe.Ask(111, Fresh(), right: true).Accepted, "and ends after " + lockout + " minutes");
            for (var guess = 0; guess < PasswordGuessLimits.DefaultPerHost; guess++) probe.Code(1_000 + guess, Fresh(), right: false);
        }

        That(networks < 128 && probe.Policy.Guesses.TrackedNetworks <= networks, "every guess came from a network of its own (" + networks + ")");
    }

    internal static void FullTablesForgetOnlyWhatNoLongerLimits()
    {
        // The review's case: 1,024 identities spending five guesses each used to clear the table, and an identity
        // already held back got five more guesses in the same minute. The host's budget is lifted here to fill it.
        var probe = new PolicyProbe(new PasswordGuessLimits(perHost: 1_000_000, perNetwork: 1_000_000));
        var shared = new SourceNetwork(9, 9);
        for (var identity = 0; identity < PasswordGuessLimits.DefaultTrackedEntries; identity++)
        {
            for (var guess = 0; guess < PasswordGuessLimits.DefaultPerIdentity; guess++) probe.Code(identity, shared, right: false);
        }

        That(probe.Policy.Guesses.TrackedIdentities == PasswordGuessLimits.DefaultTrackedEntries, "every identity is remembered (" + probe.Policy.Guesses.TrackedIdentities + ")");
        var stranger = probe.Ask(5_000, shared, right: true);
        That(Code(stranger) == AdmissionCode.TooManyAttempts && stranger.Message.Contains("try again shortly", StringComparison.Ordinal),
            "with no room left, a new identity's proof is turned away unchecked");
        for (var guess = 0; guess < PasswordGuessLimits.DefaultPerIdentity; guess++)
        {
            That(probe.Code(0, shared, right: false) == AdmissionCode.TooManyAttempts, "an identity already held back gets no new guess (" + guess + ")");
        }

        That(probe.Policy.Guesses.TrackedIdentities == PasswordGuessLimits.DefaultTrackedEntries, "and the table never grew past its bound");

        // Entries that limit nothing any more make room; the ones still in force stay.
        var small = new PolicyProbe(new PasswordGuessLimits(perHost: 1_000_000, trackedEntries: 4));
        for (var network = 1; network <= 4; network++)
        {
            for (var guess = 0; guess < PasswordGuessLimits.DefaultPerNetwork; guess++) small.Code(network, new SourceNetwork((ulong)network, 0), right: false);
        }

        That(small.Policy.Guesses.TrackedNetworks == 4 && small.Policy.Guesses.TrackedIdentities == 4, "four networks and their four identities are remembered, each held back");
        That(Code(small.Ask(90, new SourceNetwork(5, 0), right: true)) == AdmissionCode.TooManyAttempts, "a fifth network waits while all four are in force");
        small.Now += Minute + PasswordGuessLimits.DefaultDecayMicros;
        That(small.Code(91, new SourceNetwork(5, 0), right: false) == AdmissionCode.PasswordWrong &&
             small.Policy.Guesses.TrackedNetworks <= 4 && small.Policy.Guesses.TrackedIdentities <= 4,
            "once their lockouts have worn off they make room (" + small.Policy.Guesses.TrackedNetworks + " networks)");
    }

    internal static void PortsAndAddressesOfOneSlash64AreOneNetwork()
    {
        static TransportPath Udp(string address, int port) => UdpPaths.For(new IPEndPoint(IPAddress.Parse(address), port));
        static string Network(TransportPath path) => Convert.ToHexString(UdpPaths.NetworkOf(path)!);
        That(Network(Udp("203.0.113.7", 50_000)) == Network(Udp("203.0.113.7", 50_001)), "two ports of one IPv4 address are one network");
        That(Network(Udp("203.0.113.7", 1)) != Network(Udp("203.0.113.8", 1)), "the next address is another");
        That(Network(Udp("::ffff:203.0.113.7", 1)) == Network(Udp("203.0.113.7", 2)), "an IPv4-mapped address is that IPv4 address");
        That(Network(Udp("2001:db8:1:2::1", 1)) == Network(Udp("2001:db8:1:2:ffff:ffff:ffff:fffe", 2)), "two addresses of one /64 are one network");
        That(Network(Udp("2001:db8:1:2::1", 1)) != Network(Udp("2001:db8:1:3::1", 1)), "the next /64 is another");
        That(Network(Udp("fe80::1%3", 1)) != Network(Udp("fe80::1%4", 1)), "a link-local address on another interface is another link");
        That(Network(Udp("1.2.3.4", 1)) != Network(Udp("102:304::", 1)), "an IPv4 address never meets an IPv6 prefix of the same bytes");
        That(UdpPaths.NetworkOf(new TransportPath(new byte[] { 2, 1, 2, 3 }, TransportPathKind.Relayed)) == null, "a path that is not UDP names no network");

        // The links carry only a keyed hash: equal for one network at one host, unrelated across hosts, never printable.
        var bytes = UdpPaths.NetworkOf(Udp("203.0.113.7", 1))!;
        var here = new SourceNetworks();
        That(here.Of(bytes).Equals(here.Of(bytes)) && !here.Of(bytes).Equals(new SourceNetworks().Of(bytes)), "one network hashes the same at one host and differently at another");
        That(here.Of(bytes).ToString() == "source network", "and shows nothing");

        // End to end through a stack: one machine's sockets share a network, another machine has its own, and a
        // relayed link has none.
        var network = new VirtualDatagramNetwork(91);
        var host = new NetworkStack(
            new TransportEndpoint(TransportPair.Key(91, 1), new TransportOptions { Random = new SeededTransportRandom(91) }, network.Now),
            sourceOf: VirtualDatagramNetwork.SourceOf,
            networkOf: VirtualDatagramNetwork.NetworkOf);
        var hostNode = network.AddStack("host", host);
        var first = Client(network, 1, null);
        var links = new List<NetworkLink>
        {
            Accept(network, host, first.Stack, hostNode, TransportPathKind.Lan),
            Accept(network, host, Client(network, 2, first.Node).Stack, hostNode, TransportPathKind.Lan),
            Accept(network, host, Client(network, 3, null).Stack, hostNode, TransportPathKind.Lan),
            Accept(network, host, Client(network, 4, null).Stack, hostNode, TransportPathKind.Relayed)
        };
        That(links[0].SourceNetwork.HasValue && links[0].SourceNetwork.Equals(links[1].SourceNetwork), "two sockets of one machine are one network");
        That(!links[2].SourceNetwork.Equals(links[0].SourceNetwork) && links[3].SourceNetwork == null, "another machine is another, and a relay names none");
        That(network.Spins == 0, "no busy loop");
    }

    private static (NetworkStack Stack, VirtualNode Node) Client(VirtualDatagramNetwork network, int index, VirtualNode? sameMachine)
    {
        var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(91, 10 + index), new TransportOptions { Random = new SeededTransportRandom(910 + index), AcceptsInbound = false }, network.Now));
        return (stack, network.AddStack("client-" + index, stack, sameMachine));
    }

    private static NetworkLink Accept(VirtualDatagramNetwork network, NetworkStack host, NetworkStack client, VirtualNode hostNode, TransportPathKind kind)
    {
        var attempt = new ConnectAttempt();
        client.Connect(attempt, VirtualDatagramNetwork.PathTo(hostNode, kind), null, network.Now);
        NetworkLink? accepted = null;
        That(network.RunUntil(() => attempt.State == ConnectAttemptState.Established && (accepted != null || host.TryAccept(out accepted)), 5_000_000),
            "the host accepts a link over a " + kind + " path");
        That(attempt.Link!.SourceNetwork == null, "an outbound link names no network");
        return accepted!;
    }

    private static AdmissionCode? Code(ProviderEngine.JoinDecision decision) =>
        !decision.Accepted && AdmissionReport.TryDecode(decision.Report, out var code, out _) ? code : null;

    /// <summary>A host policy with the session password, asked by identities over links from the networks a test names.</summary>
    private sealed class PolicyProbe
    {
        private int links;

        internal PolicyProbe(PasswordGuessLimits? limits = null)
        {
            Policy = new HostPolicy(new BanList(), PasswordKey.Value, new TestContent(), () => Now, limits);
        }

        internal HostPolicy Policy { get; }

        internal long Now { get; set; } = 1_000_000;

        internal AdmissionCode? Code(int identity, SourceNetwork? network, bool right, bool returning = false) => PasswordGuessTests.Code(Ask(identity, network, right, returning));

        internal ProviderEngine.JoinDecision Ask(int identity, SourceNetwork? network, bool right, bool returning = false)
        {
            var number = ++links;
            var key = new byte[32];
            BitConverter.GetBytes(identity).CopyTo(key, 0);
            var binding = new byte[32];
            BitConverter.GetBytes(number).CopyTo(binding, 0);
            var link = new NetworkLink(number, key, default, binding, network);
            var proof = right ? PasswordProof.Prove(PasswordKey.Value, link.ChannelBinding) : Enumerable.Repeat((byte)(number % 251), 32).ToArray();
            return Policy.Evaluate(new ProviderEngine.JoinRequestContext(
                link,
                link.PeerIdentity,
                new ProviderEngine.JoinRequestMessage("1.0.0", "Guest", Array.Empty<ProviderEngine.ContractEntry>(), JoinContent.Encode(proof, new byte[] { 1, 2, 3 })),
                Array.Empty<ProviderEngine.ContractEntry>(),
                1,
                8,
                returning));
        }
    }
}
