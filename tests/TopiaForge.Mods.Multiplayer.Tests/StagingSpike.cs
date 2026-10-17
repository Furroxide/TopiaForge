extern alias provider;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using provider::TopiaForge.Multiplayer.ControlPlane;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using ProviderEngine = provider::TopiaForge.Multiplayer.Engine.SessionEngine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The opt-in live spike against the staging service (<c>--staging</c>; CI never passes it). It runs the provider's own
/// HTTPS client, signaling client and NAT layer against <c>https://mp-staging.topiaforge.dev</c> and Cloudflare's relay:
/// it registers a fresh invite-only test server, beats once, makes, resolves and withdraws an invite, takes a relay-only
/// join ticket, mints relay credentials for both sides, joins through two real relay allocations and passes a frame each
/// way, then stops and deletes the server. It prints statuses, shapes and timings only: never a token, a credential, a
/// code or an address. Its sockets bind nothing explicitly; each is connected to the one relay it talks to.
/// </summary>
internal static class StagingSpike
{
    internal const string Origin = "https://mp-staging.topiaforge.dev";

    internal static void Run()
    {
        That(ControlPlaneOrigin.TryParse(Origin, out var origin), "the staging origin parses");
        var services = InternetServices.ForOrigin(origin);
        var client = services.Client;
        var clock = Stopwatch.StartNew();
        T Step<T>(string name, Task<ServiceResult<T>> call) where T : class
        {
            var started = clock.ElapsedMilliseconds;
            var result = call.GetAwaiter().GetResult();
            Console.WriteLine("staging: " + name + " " + (result.Succeeded ? "ok" : result.Failure.ToString()) + " in " + (clock.ElapsedMilliseconds - started) + " ms");
            That(result.Succeeded, name + " succeeds against staging (" + result.Failure + ")");
            return result.Value!;
        }

        var status = Step("status", client.GetStatusAsync(CancellationToken.None));
        Console.WriteLine("staging: features hosting " + status.Hosting + ", invites " + status.Invites + ", relay " + status.Relay + ", signaling " + status.Signaling +
                          ", kill switch " + status.KillSwitch + "; heartbeat " + status.HeartbeatSeconds + " s, listing " + status.ListingTtlSeconds + " s, credential " + status.CredentialTtlSeconds + " s");
        using var identity = LocalIdentity.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var descriptor = SignedServerDescriptor.Sign(new ServerDescriptor(identity.SigningPublicKey, identity.TransportPublicKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            "TopiaForge staging check", "2545", "0.2.0", ProviderEngine.ProtocolVersion, 4, false, false, ServerVisibility.Invite, false, "Open Sandbox", null,
            Array.Empty<DescriptorPackage>()), identity.CopySigningSeed());
        var challenge = Step("challenge", client.RequestChallengeAsync(identity.SigningPublicKey, CancellationToken.None));
        var seed = identity.CopySigningSeed();
        HostRegistration registration;
        try
        {
            registration = Step("register (relay-only, invite-only)", client.RegisterAsync(challenge, descriptor, true, seed, CancellationToken.None));
        }
        finally
        {
            Array.Clear(seed, 0, seed.Length);
        }

        var serverId = registration.ServerId;
        var token = registration.SessionToken;
        Console.WriteLine("staging: session token " + Shape(token) + ", listed " + registration.Listed + ", invite of " + registration.Invite.Code.Length + " characters");
        That(!registration.Listed, "an invite-only server is not listed");
        try
        {
            Thread.Sleep(6_000);
            Step("heartbeat", client.HeartbeatAsync(serverId, token, 1, CancellationToken.None));
            var second = Step("create invite", client.CreateInviteAsync(serverId, token, ControlPlaneClient.MinimumInviteSeconds, CancellationToken.None));
            var resolved = Step("resolve the invite as typed", client.ResolveInviteAsync(second.Display.ToLowerInvariant(), CancellationToken.None));
            That(resolved.ServerId == serverId && resolved.Descriptor.Sha256 == descriptor.Sha256 && resolved.HostRelayOnly, "the invite resolves to this server's exact descriptor");
            Step("revoke the invite", client.RevokeInviteAsync(serverId, token, second.Code, CancellationToken.None));
            var miss = client.ResolveInviteAsync(second.Code, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("staging: resolving the withdrawn invite " + miss.Failure);
            That(miss.Failure == ServiceFailure.NotFound, "a withdrawn invite misses");
            var ticket = Step("join ticket (relay-only)", client.CreateJoinTicketAsync(serverId, registration.Invite.Code, true, CancellationToken.None));
            Console.WriteLine("staging: ticket " + Shape(ticket.Ticket) + ", relay-only " + ticket.RelayOnly + ", host relay-only " + ticket.HostRelayOnly);
            var joinerCredentials = Step("mint for the joiner", client.MintRelayCredentialsAsync(serverId, ticket.Ticket, CancellationToken.None));
            var hostCredentials = Step("mint for the host", client.MintRelayCredentialsAsync(serverId, token, CancellationToken.None));
            Console.WriteLine("staging: credentials last " + joinerCredentials.Servers.TtlSeconds + " s; " + joinerCredentials.Servers.TurnServers.Count + " UDP relay(s), " +
                              joinerCredentials.Servers.StunServers.Count + " STUN server(s)");
            Relay(services, serverId, token, ticket.Ticket, hostCredentials, joinerCredentials, descriptor.Descriptor.TransportKey, identity);
        }
        finally
        {
            var ended = client.EndHostAsync(serverId, token, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("staging: delete " + (ended.Succeeded ? "ok" : ended.Failure.ToString()));
            var after = client.HeartbeatAsync(serverId, token, 0, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("staging: a heartbeat after the delete " + after.Failure);
            That(ended.Succeeded && after.Failure == ServiceFailure.Unauthorized, "the test server is deleted, and its token opens nothing");
        }

        Console.WriteLine("staging: the spike passed in " + clock.ElapsedMilliseconds + " ms.");
    }

    /// <summary>Hosts and joins relay-only on two network threads, through the service's signaling and two real relay allocations.</summary>
    private static void Relay(InternetServices services, string serverId, string token, string ticket, RelayCredentials hostCredentials, RelayCredentials joinerCredentials,
        byte[] hostKey, LocalIdentity identity)
    {
        var host = Build("host", identity.CopyTransportPrivateKey(), acceptsInbound: true);
        var joiner = Build("joiner", provider::TopiaForge.Multiplayer.Transport.Crypto.X25519.GeneratePrivateKey(), acceptsInbound: false);
        var started = Stopwatch.StartNew();
        try
        {
            using var hosting = NatHost.Start(host.Thread, host.Nat, new NatSessionPlan(hostCredentials.Servers, () => services.OpenSignaling(serverId, token), services.Resolve)
            {
                LocalPort = host.Thread.LocalPort
            }, false);
            That(SpinWait.SpinUntil(() => hosting.Status.RelayReady, TimeSpan.FromSeconds(20)), "the host's relay allocation comes up (" + hosting.Status + ")");
            Console.WriteLine("staging: the host's relay came up in " + started.ElapsedMilliseconds + " ms");
            var joinStarted = started.ElapsedMilliseconds;
            using var join = NatJoin.Start(joiner.Thread, joiner.Nat, new NatSessionPlan(joinerCredentials.Servers, () => services.OpenSignaling(serverId, ticket), services.Resolve)
            {
                LocalPort = joiner.Thread.LocalPort
            }, hostKey);
            NetworkLink? accepted = null;
            var joined = SpinWait.SpinUntil(() =>
            {
                while (host.Thread.TryTakeEvent(out var networkEvent))
                {
                    if (networkEvent.Kind == NetworkEventKind.LinkAccepted) accepted = networkEvent.Link;
                }

                return (accepted != null && join.Attempt.State == ConnectAttemptState.Established) || join.Attempt.State == ConnectAttemptState.Failed;
            }, TimeSpan.FromSeconds(40));
            Console.WriteLine("staging: the relay-only join " + join.Attempt.State + " in " + (started.ElapsedMilliseconds - joinStarted) + " ms");
            That(joined && join.Attempt.State == ConnectAttemptState.Established, "the joiner connects (" + join.Status + ")");
            That(SpinWait.SpinUntil(() => join.Status.Route != NatRoute.None, TimeSpan.FromSeconds(5)) && join.Status.Route == NatRoute.Relayed,
                "through the relays (" + join.Status + ")");
            var link = join.Attempt.Link!;
            link.Send(ProviderChannel.Session, new ArraySegment<byte>(new byte[] { 1, 2, 3 }));
            accepted!.Send(ProviderChannel.Session, new ArraySegment<byte>(new byte[] { 4, 5 }));
            var atHost = false;
            var atJoiner = false;
            That(SpinWait.SpinUntil(() =>
            {
                while (accepted.TryReceive(out _, out var frame)) atHost |= frame.Length == 3;
                while (link.TryReceive(out _, out var frame)) atJoiner |= frame.Length == 2;
                return atHost && atJoiner;
            }, TimeSpan.FromSeconds(10)), "a frame crosses each way through the relays");
            Console.WriteLine("staging: frames crossed both relays; host sessions " + hosting.Status.RelayedSessions + " relayed, " + hosting.Status.DirectSessions + " direct");
            That(joiner.Socket.Elsewhere == 0 && host.Socket.Elsewhere == 0, "neither side sent anything anywhere but its relay");
        }
        finally
        {
            joiner.Thread.Stop(500_000);
            host.Thread.Stop(500_000);
        }
    }

    private static (NetworkThread Thread, NatPlumbing Nat, ConnectedUdpSocket Socket) Build(string name, byte[] key, bool acceptsInbound)
    {
        var socket = new ConnectedUdpSocket();
        var nat = NatPlumbing.Wrap(socket, new NatAgentOptions(), Clock);
        var stack = new NetworkStack(new TransportEndpoint(key, new TransportOptions { AcceptsInbound = acceptsInbound }, Clock()));
        var thread = new NetworkThread("TopiaForge staging " + name, nat.Socket, stack, Clock);
        thread.Attach(nat.Service);
        thread.Start();
        return (thread, nat, socket);
    }

    private static long Clock() => provider::TopiaForge.Multiplayer.MultiplayerProvider.MonotonicMicros();

    private static string Shape(string value) => value.Substring(0, Math.Min(5, value.Length)) + "… " + value.Length + " chars";

    /// <summary>
    /// A UDP socket the system binds when it first sends, connected to the one place a relay-only agent ever sends: its
    /// relay. Nothing else can reach it, and a send anywhere else is counted and dropped.
    /// </summary>
    private sealed class ConnectedUdpSocket : IDatagramSocket
    {
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly byte[] scratch = new byte[2048];
        private IPEndPoint? peer;

        internal int Elsewhere { get; private set; }

        public int LocalPort => socket.IsBound ? ((IPEndPoint)socket.LocalEndPoint!).Port : 0;

        public bool WaitReadable(long timeoutMicros) => peer != null ? socket.Poll((int)Math.Min(timeoutMicros, int.MaxValue), SelectMode.SelectRead) : Sleep(timeoutMicros);

        public bool TryReceive(byte[] buffer, out int count, out TransportPath? path)
        {
            count = 0;
            path = null;
            if (peer == null || socket.Available == 0) return false;
            try
            {
                var received = socket.Receive(scratch);
                if (received > buffer.Length) return true;
                Buffer.BlockCopy(scratch, 0, buffer, 0, received);
                count = received;
                path = UdpPaths.For(peer);
            }
            catch (SocketException)
            {
                // Lost, as on any network.
            }

            return true;
        }

        public void Send(TransportPath path, byte[] buffer, int offset, int count)
        {
            if (!UdpPaths.TryGetEndPoint(path, out var target)) return;
            var plain = new IPEndPoint(NatAddresses.Unmap(target.Address), target.Port);
            if (plain.AddressFamily != AddressFamily.InterNetwork) return;
            if (peer == null)
            {
                socket.Connect(plain);
                peer = plain;
            }

            if (!peer.Equals(plain))
            {
                Elsewhere++;
                return;
            }

            try
            {
                socket.Send(buffer, offset, count, SocketFlags.None);
            }
            catch (SocketException)
            {
                // Lost, as on any network.
            }
        }

        public void Dispose() => socket.Dispose();

        private static bool Sleep(long timeoutMicros)
        {
            Thread.Sleep(TimeSpan.FromTicks(Math.Min(timeoutMicros, 2_000) * 10));
            return false;
        }
    }
}
