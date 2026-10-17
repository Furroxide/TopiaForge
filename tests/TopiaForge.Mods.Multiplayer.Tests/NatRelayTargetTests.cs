extern alias provider;

using provider::TopiaForge.Multiplayer.NatTraversal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Where the relay goes, and how often it mints, when the network or the server will not have it: an address that went
/// silent is left for the next one, an allocation a crashed run left on the 5-tuple sends the next one to another port,
/// a credential refused right after an allocation worked is minted again at once, and credentials the server keeps
/// refusing are minted within the control plane's limits of five a minute and twenty an hour.
/// </summary>
internal static class NatRelayTargetTests
{
    private const long Second = 1_000_000;

    internal static void ARelayLeavesAnAddressThatWentSilent()
    {
        var scenario = new NatScenario(1400);
        scenario.Turn.LifetimeSeconds = 60;
        var blocked = 3478;
        scenario.Router.Blackhole = to => to.Port == blocked && NatAddresses.SameAddress(to.Address, NatScenario.ServerAddress);
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        scenario.StartHost(host);
        That(scenario.Network.RunUntil(() => host.Agent.RelayUp, 30 * Second) && NatRelayRecoveryTests.ServerPort(host) == 3479,
            "with the first port blocked, the relay comes up on the second (" + NatRelayRecoveryTests.ServerPort(host) + ")");

        // Now the network blocks the second port instead and lets the first through.
        blocked = 3479;
        That(scenario.Network.RunUntil(() => !host.Agent.RelayUp, 90 * Second), "the relay is lost at its next refresh");
        var lostAt = scenario.Network.Now;
        That(scenario.Network.RunUntil(() => host.Agent.RelayUp, 60 * Second) && NatRelayRecoveryTests.ServerPort(host) == 3478,
            "and comes back on the first port, not the one that went silent (" + NatRelayRecoveryTests.ServerPort(host) + ", " + ((scenario.Network.Now - lostAt) / Second) + " s)");
    }

    internal static void AnAllocationLeftOnTheFiveTupleSendsTheNextElsewhere()
    {
        var scenario = new NatScenario(1410);
        var home = scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10");
        var crashed = scenario.AddPeer("host", home, "192.168.1.10", 27250, true, 1);
        scenario.StartHost(crashed);
        That(scenario.Network.RunUntil(() => crashed.Agent.RelayUp, 10 * Second) && NatRelayRecoveryTests.ServerPort(crashed) == 3478, "the relay is up on the first port");

        // The game crashes: nothing is released, and the server keeps the allocation on that 5-tuple. It starts again on
        // the same port, behind the same mapping, with fresh randomness and a fresh credential that cannot release what
        // the crashed run allocated; the shared socket cannot take another local port.
        scenario.Network.Retire(crashed.Node, scenario.Network.Now);
        scenario.Network.RunFor(Second);
        var restarted = scenario.AddPeer("host", home, "192.168.1.10", 27250, true, 3);
        scenario.StartHost(restarted);
        That(scenario.Network.RunUntil(() => restarted.Agent.RelayUp, 20 * Second),
            "the restarted game's relay comes up within moments (" + restarted.Agent.Status + ", " + scenario.Turn.Mismatches + " mismatches)");
        That(NatRelayRecoveryTests.ServerPort(restarted) == 3479 && scenario.Turn.Mismatches == 1,
            "on the other port, after the first met the allocation left behind once (" + NatRelayRecoveryTests.ServerPort(restarted) + ", " + scenario.Turn.Mismatches + ")");
    }

    internal static void RefusedCredentialsAreMintedWithinTheLimits()
    {
        var scenario = new NatScenario(1420) { Renewable = true };
        scenario.Turn.LifetimeSeconds = 60;
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        scenario.StartHost(host);
        That(scenario.Network.RunUntil(() => host.Agent.RelayUp, 10 * Second), "the relay comes up");

        // A credential revoked while its allocation worked: the next one is minted at once, and the relay is back.
        var minted = scenario.Mints;
        scenario.RevokeCredentials();
        That(scenario.Network.RunUntil(() => !host.Agent.RelayUp, 60 * Second), "a revoked credential loses the relay at its next request");
        var lostAt = scenario.Network.Now;
        That(scenario.Network.RunUntil(() => host.Agent.RelayUp, 10 * Second) && scenario.Mints == minted + 1 && scenario.MintedAt[^1] - lostAt < Second,
            "the first refusal after a working allocation mints again at once (" + (scenario.Mints - minted) + " mints)");

        // A server that refuses every credential, as with a misconfigured key, for an hour.
        scenario.Turn.RefuseEveryCredential = true;
        scenario.RevokeCredentials();
        That(scenario.Network.RunUntil(() => !host.Agent.RelayUp, 60 * Second), "every credential is refused");
        var refusedFrom = scenario.Network.Now;
        var before = scenario.Mints;
        scenario.Network.RunFor(3_600 * Second);
        var mints = scenario.MintedAt.Where(at => at >= refusedFrom - Second).ToList();
        var busiestMinute = mints.Max(start => mints.Count(at => at >= start && at < start + (60 * Second)));
        That(mints.Count <= 20 && busiestMinute <= 5, "the mints stay within five a minute and twenty an hour (" + mints.Count + " in the hour, " + busiestMinute + " in the busiest minute)");
        That(scenario.Mints - before >= 8 && !host.Agent.RelayUp, "while it keeps trying, with backoff");

        // Once the key works again, the relay comes back at the next mint.
        scenario.Turn.RefuseEveryCredential = false;
        That(scenario.Network.RunUntil(() => host.Agent.RelayUp, 330 * Second), "the relay comes back once credentials work again");
    }

    internal static void AShortCredentialTtlDoesNotBusyLoop()
    {
        // A service that hands out a credential whose lifetime is shorter than the renew margin must not drive the agent
        // into a busy loop of continuous minting: the next renewal is never scheduled in the past. Here the credential
        // lasts sixty seconds, below the ninety-second margin, so without the clamp the renewal falls due at once, over
        // and over. With it, the agent renews on a bounded schedule, inside the control plane's five-a-minute limit.
        var scenario = new NatScenario(1430, ttlSeconds: 60) { Renewable = true };
        scenario.Turn.LifetimeSeconds = 60;
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        scenario.StartHost(host);

        // Ten seconds in — inside the renew interval — the credential has been used but not yet re-minted: scheduling the
        // first renewal in the past would have minted again at once.
        scenario.Network.RunFor(10 * Second);
        That(scenario.Mints == 1 && host.Agent.RelayUp, "the first credential is not re-minted at once (" + scenario.Mints + " mints, relayUp " + host.Agent.RelayUp + ")");

        var from = scenario.Network.Now;
        scenario.Network.RunFor(600 * Second);
        var mints = scenario.MintedAt.Where(at => at >= from).ToList();
        var busiestMinute = mints.Count == 0 ? 0 : mints.Max(start => mints.Count(at => at >= start && at < start + (60 * Second)));
        That(mints.Count <= 50 && busiestMinute <= 5, "the agent renews on a bounded schedule, not without end (" + mints.Count + " in ten minutes, " + busiestMinute + " in the busiest minute)");
    }
}
