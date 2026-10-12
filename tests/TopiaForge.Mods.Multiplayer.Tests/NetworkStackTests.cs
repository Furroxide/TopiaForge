extern alias provider;

using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using ProviderLinkState = provider::TopiaForge.Multiplayer.Engine.EngineLinkState;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using static TopiaForge.Mods.Multiplayer.Tests.TransportFrames;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Links over the network stack, on the deterministic network: what the engine sees of a live connection.</summary>
internal static class NetworkStackTests
{
    internal static void LinksCarryFramesBothWaysAndNamePeersByKey()
    {
        using var rig = new StackRig(110);
        var (client, host) = rig.Connect();
        That(host.PeerIdentity == Convert.ToHexString(rig.ClientPublicKey).ToLowerInvariant() &&
             client.PeerIdentity == Convert.ToHexString(rig.Identity.TransportPublicKey).ToLowerInvariant(),
            "each side names the other by its transport key");
        That(host.PlayerKey.Value == rig.Identity.PlayerKeyFor(rig.ClientPublicKey) && !client.PlayerKey.IsValid,
            "the host gives the client its per-host player key; a client gives none");
        That(host.ChannelBinding.SequenceEqual(client.ChannelBinding) && host.ChannelBinding.Length == 32,
            "both ends share the handshake hash a password proof binds to");
        That(host.State == ProviderLinkState.Open && client.State == ProviderLinkState.Open, "both links are open");

        client.Send(ProviderChannel.Session, new ArraySegment<byte>(Pattern(3_000, 1)));
        client.Send(ProviderChannel.Realtime, new ArraySegment<byte>(new byte[] { 7, 7 }));
        host.Send(ProviderChannel.Session, new ArraySegment<byte>(Pattern(40_000, 2)));
        var atHost = new List<(ProviderChannel, byte[])>();
        var atClient = new List<(ProviderChannel, byte[])>();
        That(rig.Network.RunUntil(() =>
        {
            Read(host, atHost);
            Read(client, atClient);
            return atHost.Count == 2 && atClient.Count == 1;
        }, 5_000_000), "frames cross both ways");
        That(atHost.Any(item => item.Item1 == ProviderChannel.Realtime && item.Item2.SequenceEqual(new byte[] { 7, 7 })) &&
             atHost.Any(item => item.Item1 == ProviderChannel.Session && item.Item2.SequenceEqual(Pattern(3_000, 1))) &&
             atClient[0].Item2.SequenceEqual(Pattern(40_000, 2)), "intact, on their channels");
        That(rig.Network.Spins == 0, "no busy loop");
    }

    internal static void AStalledReaderHoldsTheSenderBackThroughFlowControl()
    {
        using var rig = new StackRig(111, fast: true);
        var (client, host) = rig.Connect();
        const int Frames = 128;
        const int Size = 64 * 1024;
        for (var index = 0; index < Frames; index++) client.Send(ProviderChannel.Session, new ArraySegment<byte>(Pattern(Size, index)));
        rig.Network.RunFor(8_000_000);
        That(host.InboundBytes <= NetworkLink.InboundBudgetBytes + Size, "the main thread never holds more than its budget (" + host.InboundBytes + ")");
        That(rig.HostStack.Endpoint.ReassemblyBytes <= 2 * 1024 * 1024 + 16 * 1024, "and the transport holds no more than its window (" + rig.HostStack.Endpoint.ReassemblyBytes + ")");
        That(host.State == ProviderLinkState.Open && client.State == ProviderLinkState.Open, "a slow reader keeps the link open: keepalives continue");

        var received = new List<byte[]>();
        That(rig.Network.RunUntil(() =>
        {
            while (host.TryReceive(out var channel, out var frame)) received.Add(frame);
            return received.Count == Frames;
        }, 60_000_000), "once the reader drains, every frame arrives (" + received.Count + ")");
        That(received.Select((frame, index) => frame.SequenceEqual(Pattern(Size, index))).All(match => match), "in order and intact");
    }

    internal static void AMainThreadThatQueuesTooMuchClosesItsLink()
    {
        using var rig = new StackRig(112);
        var (client, _) = rig.Connect();
        var frame = new ArraySegment<byte>(new byte[1024 * 1024]);
        for (var index = 0; index <= NetworkLink.OutboundLimitBytes / (1024 * 1024); index++) client.Send(ProviderChannel.Session, frame);
        That(client.Overloaded && client.CloseRequested, "queuing past the limit marks the link overloaded and asks it to close");
        rig.Network.RunUntil(() => client.State == ProviderLinkState.Closed, 30_000_000);
        That(client.State == ProviderLinkState.Closed, "and it closes");
        client.Send(ProviderChannel.Session, frame);
        That(!client.TryReceive(out _, out _), "a closed link takes nothing and yields nothing");
    }

    internal static void ClosingDeliversWhatWasSentThenReportsClosed()
    {
        using var rig = new StackRig(113);
        var (client, host) = rig.Connect();
        for (var index = 0; index < 5; index++) client.Send(ProviderChannel.Session, new ArraySegment<byte>(Pattern(20_000, index)));
        client.Close();
        var received = new List<byte[]>();
        That(rig.Network.RunUntil(() =>
        {
            while (host.TryReceive(out _, out var frame)) received.Add(frame);
            return host.State == ProviderLinkState.Closed && client.State == ProviderLinkState.Closed;
        }, 20_000_000), "both ends see the link close");
        while (host.TryReceive(out _, out var frame)) received.Add(frame);
        That(received.Count == 5 && received.Select((frame, index) => frame.SequenceEqual(Pattern(20_000, index))).All(match => match),
            "after every frame sent before the close (" + received.Count + ")");
        That(rig.HostStack.LinkCount == 0 && rig.ClientStack.LinkCount == 0, "and the stacks forget them");
    }

    internal static void AClosedLinkStillYieldsMoreThanItsBudget()
    {
        using var rig = new StackRig(117, fast: true);
        var (client, host) = rig.Connect();
        const int Frames = 48;
        const int Size = 64 * 1024;
        for (var index = 0; index < Frames; index++) client.Send(ProviderChannel.Session, new ArraySegment<byte>(Pattern(Size, index)));
        client.Close();

        // The reader waits until the link reports closed: every frame must still be there, though they total well
        // over its budget, because nothing more can arrive once the connection ends.
        That(rig.Network.RunUntil(() => host.State == ProviderLinkState.Closed, 30_000_000), "the host sees the link close");
        var received = new List<byte[]>();
        while (host.TryReceive(out _, out var frame)) received.Add(frame);
        That(received.Count == Frames && received.Select((frame, index) => frame.SequenceEqual(Pattern(Size, index))).All(match => match),
            "and then reads all " + Frames + " frames (" + received.Count + ")");
    }

    internal static void AttemptsFailOnTheWrongKeyOrAForbiddenPath()
    {
        using var rig = new StackRig(114);
        var wrong = new ConnectAttempt();
        var bogusKey = new byte[32];
        bogusKey[0] = 9;
        rig.ClientStack.Connect(wrong, VirtualDatagramNetwork.PathTo(rig.HostNode), bogusKey, rig.Network.Now);
        That(rig.Network.RunUntil(() => wrong.State == ConnectAttemptState.Failed, 10_000_000), "a pinned key that does not match fails the attempt");
        That(wrong.Failure == TransportCloseReason.HandshakeFailed && wrong.Link == null, "as a failed handshake, with no link");
        That(!rig.HostStack.TryAccept(out _), "and the host never sees a link");

        using var hidden = new StackRig(115, relayOnlyClient: true);
        var direct = new ConnectAttempt();
        hidden.ClientStack.Connect(direct, VirtualDatagramNetwork.PathTo(hidden.HostNode), null, hidden.Network.Now);
        That(direct.State == ConnectAttemptState.Failed, "relay-only refuses a direct path before sending anything");
        var lan = new ConnectAttempt();
        hidden.ClientStack.Connect(lan, VirtualDatagramNetwork.PathTo(hidden.HostNode, TransportPathKind.Lan), null, hidden.Network.Now);
        That(hidden.Network.RunUntil(() => lan.State == ConnectAttemptState.Established, 5_000_000), "but still joins on the LAN");
    }

    internal static void DisposingTheStackClosesEveryLink()
    {
        var rig = new StackRig(116);
        var (client, host) = rig.Connect();
        rig.HostStack.Dispose();
        That(host.State == ProviderLinkState.Closed, "the host's links close at once");
        That(rig.Network.RunUntil(() => client.State == ProviderLinkState.Closed, 20_000_000), "and the client's follows when the host goes silent");
        rig.Dispose();
    }

    private static void Read(NetworkLink link, List<(ProviderChannel, byte[])> into)
    {
        while (link.TryReceive(out var channel, out var frame)) into.Add((channel, frame));
    }

    /// <summary>A host stack with an identity and a client stack, on the deterministic network.</summary>
    private sealed class StackRig : IDisposable
    {
        /// <param name="fast">Raises both pacers and the uplink so bulk transfers finish in virtual seconds.</param>
        internal StackRig(int seed, bool relayOnlyClient = false, bool fast = false)
        {
            Network = new VirtualDatagramNetwork(seed);
            var secret = new byte[32];
            new Random(seed).NextBytes(secret);
            Identity = new LocalIdentity(TransportPair.Key(seed, 1), TransportPair.Key(seed, 3), secret, 0);
            var hostEndpoint = new TransportEndpoint(Identity.CopyTransportPrivateKey(), Options(seed * 3, fast, false), Network.Now);
            HostStack = new NetworkStack(hostEndpoint, key => new MultiplayerPlayerKey(Identity.PlayerKeyFor(key)));
            var clientKey = TransportPair.Key(seed, 2);
            var clientEndpoint = new TransportEndpoint(clientKey, Options(seed * 3 + 1, fast, relayOnlyClient), Network.Now);
            ClientPublicKey = clientEndpoint.StaticPublicKey;
            ClientStack = new NetworkStack(clientEndpoint);
            HostNode = Network.AddStack("host", HostStack);
            ClientNode = Network.AddStack("client", ClientStack);
        }

        internal VirtualDatagramNetwork Network { get; }
        internal LocalIdentity Identity { get; }
        internal NetworkStack HostStack { get; }
        internal NetworkStack ClientStack { get; }
        internal VirtualNode HostNode { get; }
        internal VirtualNode ClientNode { get; }
        internal byte[] ClientPublicKey { get; }

        internal (NetworkLink Client, NetworkLink Host) Connect()
        {
            var attempt = new ConnectAttempt();
            ClientStack.Connect(attempt, VirtualDatagramNetwork.PathTo(HostNode), Identity.TransportPublicKey, Network.Now);
            NetworkLink? host = null;
            That(Network.RunUntil(() => attempt.State == ConnectAttemptState.Established && (host != null || HostStack.TryAccept(out host)), 10_000_000),
                "the client connects and the host accepts");
            return (attempt.Link!, host!);
        }

        private static TransportOptions Options(int seed, bool fast, bool relayOnly)
        {
            var options = new TransportOptions { Random = new SeededTransportRandom(seed), RelayOnly = relayOnly };
            if (!fast) return options;
            options.SustainedBytesPerSecond = 8 * 1024 * 1024;
            options.BurstBytesPerSecond = 16 * 1024 * 1024;
            options.UplinkBytesPerSecond = 32 * 1024 * 1024;
            return options;
        }

        public void Dispose()
        {
            HostStack.Dispose();
            ClientStack.Dispose();
            Identity.Dispose();
        }
    }
}
