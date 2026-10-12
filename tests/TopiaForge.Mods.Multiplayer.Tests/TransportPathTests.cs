extern alias provider;

using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using provider::TopiaForge.Multiplayer.Transport.Paths;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using static TopiaForge.Mods.Multiplayer.Tests.TransportFrames;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Path validation: following a peer that moved, upgrading relay to direct, and never leaving relay-only.</summary>
internal static class TransportPathTests
{
    internal static void ANatRebindingMovesTheConnectionOnceValidated()
    {
        var pair = new TransportPair(70);
        var (client, host) = pair.Establish();
        pair.ClientNode.Rebind();
        client.Send(ProviderChannel.Session, Pattern(300, 1), 0, 300);
        var atHost = new List<byte[]>();
        That(pair.Network.RunUntil(() =>
        {
            Drain(host, atHost);
            return atHost.Count == 1 && host.Path.Equals(VirtualDatagramNetwork.PathTo(pair.ClientNode));
        }, 3_000_000), "the host takes the frame from the new address and moves there once it answers a challenge");
        That(host.PathChanges == 1 && client.PathChanges == 0 && !host.IsValidatingPath, "only the side that saw the move changes path");
        host.Send(ProviderChannel.Session, Pattern(200, 2), 0, 200);
        var atClient = new List<byte[]>();
        That(pair.Network.RunUntil(() =>
        {
            Drain(client, atClient);
            return atClient.Count == 1;
        }, 2_000_000) && atClient[0].SequenceEqual(Pattern(200, 2)), "and its replies reach the new address");
        That(pair.HostDiagnostics.Reports.Any(report => report.Kind == TransportDiagnosticKind.PathChanged && report.Connection == host.Number),
            "diagnostics note the move by connection number alone");
        That(pair.Network.Spins == 0, "validation never spins");
    }

    internal static void ARelayedConnectionUpgradesToDirect()
    {
        var pair = new TransportPair(71);
        var (client, host) = pair.Establish(kind: TransportPathKind.Relayed);
        That(client.Path.Kind == TransportPathKind.Relayed && host.Path.Kind == TransportPathKind.Relayed, "the connection starts on the relay");
        That(client.ProbePath(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now), "the client tries the direct path");
        That(!client.ProbePath(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now), "once at a time");
        That(pair.Network.RunUntil(() => client.Path.Kind == TransportPathKind.Direct, 2_000_000), "the host answers and the client moves");
        client.Send(ProviderChannel.Session, Pattern(100, 3), 0, 100);
        var atHost = new List<byte[]>();
        That(pair.Network.RunUntil(() =>
        {
            Drain(host, atHost);
            return atHost.Count == 1 && host.Path.Kind == TransportPathKind.Direct;
        }, 3_000_000), "the host sees the client's traffic arrive directly, validates it and follows");
        That(client.PathChanges == 1 && host.PathChanges == 1, "each side moved once");
    }

    internal static void AnUnansweredProbeLeavesTheConnectionWhereItWas()
    {
        var pair = new TransportPair(72);
        var (client, host) = pair.Establish(kind: TransportPathKind.Relayed);
        var directChallenges = 0;
        pair.Network.Unreachable = (_, path) => path.Kind == TransportPathKind.Direct;
        pair.Network.ObservePath = (from, path, bytes) =>
        {
            if (from == pair.ClientNode && path.Kind == TransportPathKind.Direct)
            {
                That(bytes.Length == TransportOptions.MaximumDatagramBytes, "a challenge fills a whole datagram");
                directChallenges++;
            }
        };
        That(client.ProbePath(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now), "the client tries a direct path that is blocked");
        That(pair.Network.RunUntil(() => !client.IsValidatingPath, 10_000_000), "it gives up");
        That(directChallenges == PathValidator.ChallengesPerCandidate && client.Path.Kind == TransportPathKind.Relayed && client.PathChanges == 0,
            "after three challenges the connection stays on the relay (" + directChallenges + ")");
        client.Send(ProviderChannel.Session, Pattern(50, 4), 0, 50);
        var atHost = new List<byte[]>();
        That(pair.Network.RunUntil(() =>
        {
            Drain(host, atHost);
            return atHost.Count == 1;
        }, 2_000_000), "and frames still flow over it");
    }

    internal static void RelayOnlyNeverTouchesADirectPath()
    {
        var pair = new TransportPair(73, configureClient: client => client.RelayOnly = true);
        Throws<ArgumentException>(
            () => pair.Client.Connect(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now),
            "a relay-only endpoint refuses to connect directly");
        Throws<ArgumentException>(
            () => pair.Client.QueryInfo(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now),
            "or to query a direct path");
        var clientSends = new List<TransportPathKind>();
        pair.Network.ObservePath = (from, path, _) =>
        {
            if (from == pair.ClientNode) clientSends.Add(path.Kind);
        };
        var (client, host) = pair.Establish(kind: TransportPathKind.Relayed);
        That(!client.ProbePath(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now), "it never probes a direct path");
        var dropped = pair.Client.Statistics.DatagramsDropped;
        That(host.ProbePath(VirtualDatagramNetwork.PathTo(pair.ClientNode), pair.Network.Now), "the host may try the client's direct address");
        var stranger = pair.Network.AddRaw("stranger");
        pair.Network.Inject(stranger, pair.ClientNode, TransportHandshakeTests.Initiation(0x55, 73));
        pair.Network.RunFor(5_000_000);
        That(host.Path.Kind == TransportPathKind.Relayed && host.PathChanges == 0, "but the client never answers there, so the host stays on the relay");
        That(stranger.Inbox.Count == 0 && pair.Client.Statistics.DatagramsDropped >= dropped + 4,
            "the client drops every datagram from a direct path, handshakes included");
        That(clientSends.Count > 0 && clientSends.All(kind => kind == TransportPathKind.Relayed), "and every datagram it sends goes over the relay");
        That(pair.Network.RunUntil(() => client.State == TransportConnectionState.Established && !client.IsStalled, 1_000), "the connection is healthy");
    }

    internal static void AStrayCopyNeverMovesTheConnection()
    {
        var pair = new TransportPair(74);
        var (client, host) = pair.Establish();
        byte[]? captured = null;
        var injecting = false;
        pair.Network.Drop = (from, _, bytes) =>
        {
            if (injecting || captured != null || from != pair.ClientNode || bytes[0] != PacketType.Protected || bytes.Length < 200) return false;
            captured = bytes;
            return true;
        };
        client.Send(ProviderChannel.Session, Pattern(200, 5), 0, 200);
        That(pair.Network.RunUntil(() => captured != null, 1_000_000), "a fresh packet is captured before it arrives");
        var stray = pair.Network.AddRaw("stray");
        injecting = true;
        pair.Network.Inject(stray, pair.HostNode, captured!);
        injecting = false;
        var atHost = new List<byte[]>();
        That(pair.Network.RunUntil(() =>
        {
            Drain(host, atHost);
            return atHost.Count == 1;
        }, 1_000_000), "the genuine copy is accepted wherever it came from");
        That(pair.Network.RunUntil(() => !host.IsValidatingPath, 10_000_000) && host.PathChanges == 0 &&
             host.Path.Equals(VirtualDatagramNetwork.PathTo(pair.ClientNode)),
            "but the stray address cannot answer a challenge, so the host stays put");
        // The real client's next packet comes from the old path, which cancels the check before all three go out.
        That(stray.Inbox.Count >= 1 && stray.Inbox.Count <= PathValidator.ChallengesPerCandidate &&
             stray.Inbox.All(item => item.Bytes.Length == TransportOptions.MaximumDatagramBytes),
            "the stray address received at most three challenges and nothing else (" + stray.Inbox.Count + ")");
        client.Send(ProviderChannel.Session, Pattern(20, 6), 0, 20);
        That(pair.Network.RunUntil(() =>
        {
            Drain(host, atHost);
            return atHost.Count >= 2;
        }, 3_000_000), "the real path still works");
    }

    internal static void OnlyTheEchoedChallengeValidatesAPath()
    {
        var network = new VirtualDatagramNetwork(76);
        var endpoint = new TransportEndpoint(TransportPair.Key(76, 1), new TransportOptions { Random = new SeededTransportRandom(76) }, network.Now);
        var node = network.AddEndpoint("host", endpoint);
        var raw = new RawTransportPeer(network, "raw", 76);
        raw.Connect(node);
        TransportConnection? accepted = null;
        That(network.RunUntil(() => endpoint.TryAccept(out accepted), 1_000_000), "the raw peer connects");
        network.RunFor(100_000);
        raw.ReadAll();

        // A real packet from the peer's LAN address makes the host check that path with a challenge.
        raw.Send(node, writer => writer.WriteByte(FrameType.Ping), TransportPathKind.Lan);
        network.RunFor(50_000);
        raw.ReadAll();
        That(accepted!.IsValidatingPath && raw.Received.Challenges.Count == 1, "the host challenges the new path");
        var challenge = raw.Received.Challenges[0];
        var wrong = challenge.Select(value => (byte)(value ^ 0xFF)).ToArray();
        raw.Send(node, writer => FrameWriter.WritePathFrame(writer, FrameType.PathResponse, wrong), TransportPathKind.Lan);
        network.RunFor(50_000);
        That(accepted.PathChanges == 0 && accepted.IsValidatingPath, "a response with other bytes proves nothing");
        raw.Send(node, writer => FrameWriter.WritePathFrame(writer, FrameType.PathResponse, challenge), TransportPathKind.Lan);
        network.RunFor(50_000);
        That(accepted.PathChanges == 1 && accepted.Path.Kind == TransportPathKind.Lan, "the echoed bytes move the connection to the path they validate");
    }

    internal static void ChallengesAreAnsweredOnTheirOwnPathAtTheirOwnSize()
    {
        var network = new VirtualDatagramNetwork(75);
        var endpoint = new TransportEndpoint(TransportPair.Key(75, 1), new TransportOptions { Random = new SeededTransportRandom(75) }, network.Now);
        var node = network.AddEndpoint("host", endpoint);
        var raw = new RawTransportPeer(network, "raw", 75);
        raw.Connect(node);
        TransportConnection? accepted = null;
        That(network.RunUntil(() => endpoint.TryAccept(out accepted), 1_000_000), "the raw peer connects");
        network.RunFor(100_000);
        raw.ReadAll();
        raw.OpenedSizes.Clear();
        var data = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 };
        var challenge = raw.Send(node, writer => FrameWriter.WritePathFrame(writer, FrameType.PathChallenge, data), TransportPathKind.Lan);
        network.RunFor(100_000);
        raw.ReadAll();
        That(raw.Received.Responses.Count == 1 && raw.Received.Responses[0].SequenceEqual(data), "the challenge is answered with its own bytes");
        That(raw.OpenedSizes.Contains(challenge.Length) && raw.OpenedSizes.All(size => size <= TransportOptions.MaximumDatagramBytes),
            "and the answer is no larger than the challenge that provoked it (" + challenge.Length + ")");
        That(!accepted!.IsValidatingPath && accepted.PathChanges == 0, "a packet of path frames alone never makes the connection move");
    }
}
