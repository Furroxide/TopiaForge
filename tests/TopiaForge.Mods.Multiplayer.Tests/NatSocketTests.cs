extern alias provider;

using System.Diagnostics;
using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The production glue on real threads and real sockets, all on the loopback address: two network threads, each built on
/// the NAT layer's socket hook with its service attached, join through a TURN relay, the joiner moves to the direct path
/// their sockets share, and both release their allocations when they stop. It runs over the game's socket and over the
/// IPv4 one a system without IPv6 gets.
/// </summary>
internal static class NatSocketTests
{
    internal static void NetworkThreadsJoinThroughARelayOverLoopback()
    {
        // The NAT layer takes an answer only from the path its request went to, so the relay works over the IPv4 socket
        // only if that socket names the relay as the dual-mode one does.
        JoinThroughARelay(withoutIPv6: false);
        JoinThroughARelay(withoutIPv6: true);
    }

    private static void JoinThroughARelay(bool withoutIPv6)
    {
        var socket = withoutIPv6 ? " over the IPv4 socket" : " over the game's socket";
        var watch = Stopwatch.StartNew();
        using var relay = new LoopbackTurnRelay(900);
        relay.Server.Users["player"] = ("relay-secret", long.MaxValue);
        var signaling = new FakeSignalingService(1_000);
        var host = Build("host", 1, withoutIPv6);
        var joiner = Build("joiner", 2, withoutIPv6);
        try
        {
            using var hosting = NatHost.Start(host.Thread, host.Nat, Plan(relay, host.Thread.LocalPort, () => signaling.ConnectHost(false)), false);
            using var join = NatJoin.Start(joiner.Thread, joiner.Nat, Plan(relay, joiner.Thread.LocalPort, () => signaling.ConnectPeer(NatScenario.PeerId, false)),
                provider::TopiaForge.Multiplayer.Transport.Crypto.X25519.DerivePublicKey(TransportPair.Key(900, 1)));
            NetworkLink? accepted = null;
            That(Pump(signaling, () =>
            {
                while (host.Thread.TryTakeEvent(out var networkEvent))
                {
                    if (networkEvent.Kind == NetworkEventKind.LinkAccepted) accepted = networkEvent.Link;
                }

                return accepted != null && join.Attempt.State == ConnectAttemptState.Established;
            }), "the join connects across two network threads" + socket + " (" + join.Attempt.State + ", " + join.Status + ")");
            That(relay.Allocations == 2, "each side allocated on the relay" + socket + " (" + relay.Allocations + ")");

            var link = join.Attempt.Link!;
            link.Send(ProviderChannel.Session, new ArraySegment<byte>(new byte[] { 1, 2, 3 }));
            accepted!.Send(ProviderChannel.Session, new ArraySegment<byte>(new byte[] { 4, 5 }));
            var atHost = false;
            var atJoiner = false;
            That(Pump(signaling, () =>
            {
                while (accepted.TryReceive(out _, out var frame)) atHost |= frame.Length == 3;
                while (link.TryReceive(out _, out var frame)) atJoiner |= frame.Length == 2;
                return atHost && atJoiner;
            }), "frames cross both ways" + socket);
            // The joiner, which controls the checks, moves to the direct path the two sockets share; the host follows on
            // its own schedule, which the deterministic tests pin down, so here it only has to keep its one session.
            That(Pump(signaling, () => join.Status.Route == NatRoute.Direct && hosting.Status.Sessions == 1),
                "the joiner upgrades to the direct path the two sockets share" + socket + " (" + join.Status + "; " + hosting.Status + ")");
        }
        finally
        {
            joiner.Thread.Stop(300_000);
            host.Thread.Stop(300_000);
        }

        That(host.Thread.HasStopped && joiner.Thread.HasStopped, "both threads stop" + socket);
        That(Pump(signaling, () => relay.Allocations == 0), "and stopping released both allocations on the relay" + socket);
        That(watch.ElapsedMilliseconds < 5_000, "all of it within five seconds" + socket + " (" + watch.ElapsedMilliseconds + " ms)");
    }

    private static (NetworkThread Thread, NatPlumbing Nat) Build(string name, int role, bool withoutIPv6)
    {
        // Both players allowed direct play, so the joiner can move to the direct path the two sockets share.
        var nat = NatPlumbing.Wrap(
            new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6),
            new NatAgentOptions { AcceptLoopback = true, RelayOnly = false, Random = new SeededTransportRandom(900 + role) },
            Clock);
        var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(900, role), new TransportOptions { RelayOnly = false }, Clock()));
        var thread = new NetworkThread("TopiaForge test NAT " + name, nat.Socket, stack, Clock);
        thread.Attach(nat.Service);
        thread.Start();
        return (thread, nat);
    }

    private static NatSessionPlan Plan(LoopbackTurnRelay relay, int port, Func<ISignalingClient> signaling) =>
        new(IceServerSet.Create(Array.Empty<IceServerAddress>(), new[] { new IceServerAddress("relay.example.test", relay.Port) }, "player", "relay-secret", 600),
            signaling, _ => Task.FromResult(new[] { IPAddress.Loopback }))
        {
            LocalAddresses = new[] { IPAddress.Loopback },
            LocalPort = port
        };

    private static bool Pump(FakeSignalingService signaling, Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 4_000)
        {
            signaling.Poll(Clock());
            if (condition()) return true;
            Thread.Sleep(1);
        }

        return condition();
    }

    private static long Clock() => provider::TopiaForge.Multiplayer.MultiplayerProvider.MonotonicMicros();
}
