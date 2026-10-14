extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Ice;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>ICE-lite on the shared socket: priorities, credentials, checks, triggered checks, consent, and what the agent answers.</summary>
internal static class NatIceTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Parse("198.51.100.20"), 40000);

    internal static void CandidatePrioritiesFollowRfc8445()
    {
        // The control plane's transcript carries exactly these: host, then server-reflexive, then relay.
        That(IceCandidate.ComputePriority(IceCandidateKind.Host, 65535) == 2130706431, "a host candidate ranks highest");
        That(IceCandidate.ComputePriority(IceCandidateKind.ServerReflexive, 65535) == 1694498815, "then a server-reflexive one");
        That(IceCandidate.ComputePriority(IceCandidateKind.Relayed, 65535) == 16777215, "and a relayed one last");
        That(IceCandidate.ComputePriority(IceCandidateKind.PeerReflexive, 65535) > IceCandidate.ComputePriority(IceCandidateKind.ServerReflexive, 65535) &&
             IceCandidate.ComputePriority(IceCandidateKind.PeerReflexive, 65535) < IceCandidate.ComputePriority(IceCandidateKind.Host, 0),
            "a peer-reflexive one sits between host and server-reflexive");
        Throws<ArgumentOutOfRangeException>(() => IceCandidate.ComputePriority(IceCandidateKind.Host, 65536), "a local preference is 16 bits");
        const uint high = 2130706431;
        const uint low = 16777215;
        That(IceCandidate.PairPriority(high, low) == (((ulong)low << 32) + (2UL * high) + 1), "the pair formula: 2^32 MIN + 2 MAX + (G > D)");
        That(IceCandidate.PairPriority(low, high) == (((ulong)low << 32) + (2UL * high)), "and without the tie bit when D is larger");
        That(IceCandidate.PairPriority(high, high) > IceCandidate.PairPriority(high, low), "a host pair outranks one that uses a relay");
    }

    internal static void CredentialsAreRandomValidatedAndUnprinted()
    {
        var first = IceCredentials.Generate(new SeededTransportRandom(600));
        var again = IceCredentials.Generate(new SeededTransportRandom(600));
        var other = IceCredentials.Generate(new SeededTransportRandom(601));
        That(first.Ufrag.Length == 8 && first.Password.Length == 24 && IceCredentials.IsValid(first.Ufrag, 4, 32) && IceCredentials.IsValid(first.Password, 22, 64),
            "credentials fit the signaling grammar");
        That(first.Ufrag == again.Ufrag && first.Password == again.Password && first.Password != other.Password, "a seeded source repeats them, another does not");
        That(!IceCredentials.TryCreate("abc", "0123456789abcdefABCDEF", out _) && !IceCredentials.TryCreate("abcd", "0123456789abcdefABCDE", out _) &&
             !IceCredentials.TryCreate("ab-d", "0123456789abcdefABCDEF", out _) && IceCredentials.TryCreate("abcd", "0123456789abcdefABCDEF", out _),
            "fragments of 4 to 32 and passwords of 22 to 64 characters from A-Z a-z 0-9 + / only");
        That(!first.ToString().Contains(first.Password, StringComparison.Ordinal) && !first.ToString().Contains(first.Ufrag, StringComparison.Ordinal), "credentials never print");
    }

    internal static void ChecksCarryWhatTheRemoteVerifies()
    {
        var (checker, sent) = Checker(true, out var stun);
        var remote = IceCredentials.Generate(new SeededTransportRandom(611));
        checker.SetRemote(remote);
        var socket = LocalBase.Socket(IceCandidate.ComputePriority(IceCandidateKind.Host, 65535));
        checker.AddBase(socket, 0);
        checker.AddRemote(new IceCandidate(IceCandidateKind.ServerReflexive, Remote, IceCandidate.ComputePriority(IceCandidateKind.ServerReflexive, 65535)), 0);
        checker.Poll(0);
        That(sent.Count == 0, "nothing goes out before the punch");
        checker.Start(0);
        checker.Poll(0);
        That(sent.Count == 1, "the first ordinary check goes out at once");
        That(StunMessage.TryParse(sent[0].Bytes, 0, sent[0].Bytes.Length, out var check) && check.Method == StunMethod.Binding && check.Class == StunClass.Request, "it is a Binding request");
        That(check.TryGetText(StunAttribute.Username, 513, out var username) && username == remote.Ufrag + ":" + checker.Local.Ufrag, "naming the remote's fragment, then this side's");
        That(check.TryGetUInt32(StunAttribute.Priority, out var priority) && priority == IceCandidate.ComputePriority(IceCandidateKind.PeerReflexive, 65535),
            "carrying the priority a peer-reflexive candidate here would have");
        That(check.TryGetUInt64(StunAttribute.IceControlling, out var tie) && tie == checker.TieBreaker && !check.Has(StunAttribute.IceControlled), "and the controlling role");
        That(check.HasFingerprint && check.VerifyIntegrity(remote.Key) && !check.VerifyIntegrity(checker.Local.Key), "with integrity under the remote's password");
        checker.Poll(10_000);
        That(sent.Count == 1, "the next ordinary check waits its 50 ms");

        // An answer must prove the remote's password: one under any other key leaves the check running.
        That(!stun.TryComplete(Answer(sent[0].Bytes, IceCredentials.Generate(new SeededTransportRandom(612))), sent[0].Path, 20_000),
            "an answer under the wrong password is refused");
        That(checker.Pairs.Single().State == CandidatePairState.InProgress, "and the pair is still being checked");
        That(stun.TryComplete(Answer(sent[0].Bytes, remote), sent[0].Path, 30_000) && checker.Pairs.Single().State == CandidatePairState.Succeeded,
            "while the remote's own answer completes it");
    }

    internal static void ChecksTriggerChecksAndLearnPeerReflexiveAddresses()
    {
        var (checker, sent) = Checker(false);
        checker.SetRemote(IceCredentials.Generate(new SeededTransportRandom(621)));
        var socket = LocalBase.Socket(IceCandidate.ComputePriority(IceCandidateKind.Host, 65535));
        var relay = LocalBase.Relay(7, IceCandidate.ComputePriority(IceCandidateKind.Relayed, 65535));
        checker.AddBase(socket, 0);
        checker.AddBase(relay, 0);
        checker.AddRemote(new IceCandidate(IceCandidateKind.ServerReflexive, Remote, 1), 0);
        var mapped = new IPEndPoint(IPAddress.Parse("198.51.100.20"), 40123);
        checker.OnRequest(socket, mapped, 12345, 0);
        That(checker.RemoteCandidates.Any(candidate => candidate.Kind == IceCandidateKind.PeerReflexive && candidate.SameAddress(mapped) && candidate.Priority == 12345),
            "an address the remote never signaled becomes a peer-reflexive candidate with the priority its check claimed");
        That(checker.Pairs.Count(pair => pair.Remote.Kind == IceCandidateKind.PeerReflexive) == 1, "paired only with the base it arrived on");
        checker.Poll(0);
        That(sent.Count == 1 && UdpPaths.TryGetEndPoint(sent[0].Path, out var target) && NatAddresses.SameEndPoint(target, mapped), "a triggered check goes back at once, before any punch");
        for (var index = 0; index < 20; index++) checker.OnRequest(socket, new IPEndPoint(IPAddress.Parse("198.51.100.20"), 41000 + index), 1, 0);
        That(checker.RemoteCandidates.Count(candidate => candidate.Kind == IceCandidateKind.PeerReflexive) == ConnectivityChecker.MaximumPeerReflexive,
            "peer-reflexive candidates stop at their bound");
        for (var index = 0; index < 40; index++) checker.AddRemote(new IceCandidate(IceCandidateKind.Host, new IPEndPoint(IPAddress.Parse("198.51.100.30"), 2000 + index), 1), 0);
        That(checker.RemoteCandidates.Count == ConnectivityChecker.MaximumRemoteCandidates && checker.Pairs.Count <= ConnectivityChecker.MaximumPairs, "so do candidates and pairs");
        checker.RemoveBase(socket);
        That(checker.Pairs.All(pair => pair.Local.IsRelay) && checker.RemoteCandidates.All(candidate => candidate.Kind != IceCandidateKind.PeerReflexive),
            "a removed base takes its pairs and the peer-reflexive candidates only it had found");
    }

    internal static void ConsentLapsesThirtySecondsAfterTheLastAnswer()
    {
        var (checker, sent) = Checker(true, out var stun);
        var remote = IceCredentials.Generate(new SeededTransportRandom(631));
        checker.SetRemote(remote);
        checker.AddBase(LocalBase.Socket(IceCandidate.ComputePriority(IceCandidateKind.Host, 65535)), 0);
        checker.AddRemote(new IceCandidate(IceCandidateKind.ServerReflexive, Remote, 1), 0);
        checker.Start(0);
        checker.Poll(0);
        var path = sent[0].Path;
        stun.TryComplete(Answer(sent[0].Bytes, remote), path, 1_000);
        var pair = checker.Pairs.Single();
        That(pair.State == CandidatePairState.Succeeded, "the check's answer makes the pair succeed");
        checker.WantConsent(pair, true, 1_000);

        // Answer the first four consent checks, then fall silent, as a peer that went away would.
        var seen = new HashSet<StunTransactionId>();
        var firstSends = new List<long>();
        var lastAnswer = 1_000L;
        var now = 1_000L;
        var handled = sent.Count;
        while (now < 200_000_000 && pair.State == CandidatePairState.Succeeded)
        {
            now = Math.Max(now + 1, Math.Min(checker.NextTimeoutMicros(), stun.NextTimeoutMicros()));
            checker.Poll(now);
            stun.Poll(now);
            for (; handled < sent.Count; handled++)
            {
                if (!StunMessage.TryParse(sent[handled].Bytes, 0, sent[handled].Bytes.Length, out var request) || !seen.Add(request.TransactionId)) continue;
                firstSends.Add(now);
                if (firstSends.Count > 4) continue;
                stun.TryComplete(Answer(sent[handled].Bytes, remote), path, now);
                lastAnswer = now;
            }
        }

        var gaps = firstSends.Zip(firstSends.Skip(1), (left, right) => right - left).ToList();
        That(firstSends.Count >= 5 && gaps.All(gap => gap >= 12_000_000 && gap <= 18_000_000),
            "consent checks go out every 15 seconds, give or take a fifth (" + string.Join(", ", gaps.Select(gap => gap / 1000)) + " ms)");
        That(pair.State == CandidatePairState.Failed && !pair.ConsentWanted, "once the answers stop, consent lapses");
        That(now - lastAnswer >= ConnectivityChecker.ConsentLifetimeMicros && now - lastAnswer <= ConnectivityChecker.ConsentLifetimeMicros + 1_000,
            "thirty seconds after the last answer (" + ((now - lastAnswer) / 1000) + " ms)");
    }

    internal static void TheAgentAnswersOnlyAuthenticChecks()
    {
        var (scenario, host, joiner, _, _) = NatMatrixTests.Session(640, NatBehaviour.FullCone, NatBehaviour.FullCone);
        var session = host.Agent.Sessions.Single(item => item.State != NatSessionState.Ended);
        var local = session.Checker.Local;
        var remote = session.Checker.Remote!;
        var from = UdpPaths.For(new IPEndPoint(IPAddress.Parse("198.51.100.20"), 40000));
        var now = scenario.Network.Now;
        var answered = host.Agent.ChecksAnswered;
        // The joiner controls, so an authentic check claims ICE-CONTROLLING.
        var cases = new (string Name, byte[] Check)[]
        {
            ("an unknown local fragment", Check("zzzzzzzz:" + remote.Ufrag, local.Key, true, true)),
            ("another remote fragment", Check(local.Ufrag + ":zzzzzzzz", local.Key, true, true)),
            ("the wrong password", Check(local.Ufrag + ":" + remote.Ufrag, remote.Key, true, true)),
            ("no fingerprint", Check(local.Ufrag + ":" + remote.Ufrag, local.Key, false, true)),
            ("the host's own role", Check(local.Ufrag + ":" + remote.Ufrag, local.Key, true, false))
        };
        foreach (var (name, check) in cases)
        {
            That(host.Agent.Receive(from, check, 0, check.Length, now, out _, out _, out _) == NatReceiveResult.Consumed && host.Agent.ChecksAnswered == answered,
                "a check with " + name + " gets no answer");
        }

        var good = Check(local.Ufrag + ":" + remote.Ufrag, local.Key, true, true);
        host.Agent.Receive(from, good, 0, good.Length, now, out _, out _, out _);
        That(host.Agent.ChecksAnswered == answered + 1, "an authentic one is answered");
        var reply = Drain(host.Agent).Last(item => item.Path.Equals(from));
        That(StunMessage.TryParse(reply.Bytes, 0, reply.Bytes.Length, out var response) && response.Class == StunClass.Success && response.VerifyIntegrity(local.Key) &&
             response.TryGetAddress(StunAttribute.XorMappedAddress, out var mapped) && mapped.Equals(new IPEndPoint(IPAddress.Parse("198.51.100.20"), 40000)),
            "with integrity under the host's password and the address it came from");

        // A relay-only agent answers nothing that did not come through its own relay.
        var (hidden, _, hiddenJoiner, _, _) = NatMatrixTests.Session(641, NatBehaviour.FullCone, NatBehaviour.FullCone, joinerRelayOnly: true);
        var joinSession = hiddenJoiner.Agent.Sessions.Single(item => item.IsJoin);
        var probe = Check(joinSession.Checker.Local.Ufrag + ":" + joinSession.Checker.Remote!.Ufrag, joinSession.Checker.Local.Key, true, false);
        var before = hiddenJoiner.Agent.ChecksAnswered;
        Drain(hiddenJoiner.Agent);
        hiddenJoiner.Agent.Receive(UdpPaths.For(new IPEndPoint(IPAddress.Parse("198.51.100.10"), 40000)), probe, 0, probe.Length, hidden.Network.Now, out _, out _, out _);
        That(hiddenJoiner.Agent.ChecksAnswered == before && Drain(hiddenJoiner.Agent).Count == 0, "a relay-only agent never answers a direct check, however authentic");
    }

    private static (ConnectivityChecker Checker, List<(TransportPath Path, byte[] Bytes)> Sent) Checker(bool controlling) => Checker(controlling, out _);

    private static (ConnectivityChecker Checker, List<(TransportPath Path, byte[] Bytes)> Sent) Checker(bool controlling, out StunClient stun)
    {
        var sent = new List<(TransportPath, byte[])>();
        var client = new StunClient(new SeededTransportRandom(650), (path, bytes, length) => sent.Add((path, bytes.AsSpan(0, length).ToArray())));
        stun = client;
        var checker = new ConnectivityChecker(IceCredentials.Generate(new SeededTransportRandom(651)), controlling, 0x0102030405060708UL, client,
            new SeededTransportRandom(652), (local, remote) => local.IsRelay ? provider::TopiaForge.Multiplayer.NatTraversal.Turn.TurnRelayPath.For(local.Allocation, remote) : UdpPaths.For(remote));
        return (checker, sent);
    }

    private static StunMessage Answer(byte[] request, IceCredentials responder)
    {
        StunMessage.TryParse(request, 0, request.Length, out var parsed);
        var writer = new StunMessageWriter();
        var buffer = new byte[256];
        writer.Begin(buffer, StunMethod.Binding, StunClass.Success, parsed.TransactionId);
        writer.AddAddress(StunAttribute.XorMappedAddress, new IPEndPoint(IPAddress.Parse("198.51.100.10"), 40000));
        var length = writer.Finish(responder.Key, true);
        StunMessage.TryParse(buffer, 0, length, out var answer);
        return answer;
    }

    private static byte[] Check(string username, byte[] key, bool fingerprint, bool claimControlling)
    {
        var writer = new StunMessageWriter();
        var buffer = new byte[256];
        var id = new byte[12];
        new Random((username.Length * 31) + (claimControlling ? 1 : 0)).NextBytes(id);
        writer.Begin(buffer, StunMethod.Binding, StunClass.Request, StunTransactionId.Read(id, 0));
        writer.AddText(StunAttribute.Username, username);
        writer.AddUInt32(StunAttribute.Priority, 1);
        writer.AddUInt64(claimControlling ? StunAttribute.IceControlling : StunAttribute.IceControlled, 7);
        return buffer.AsSpan(0, writer.Finish(key, fingerprint)).ToArray();
    }

    private static List<(TransportPath Path, byte[] Bytes)> Drain(NatAgent agent)
    {
        var result = new List<(TransportPath, byte[])>();
        while (agent.TryDequeueDatagram(out var datagram))
        {
            result.Add((datagram.Path, datagram.Buffer.AsSpan(0, datagram.Length).ToArray()));
            agent.Release(datagram);
        }

        return result;
    }
}
