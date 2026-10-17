extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The TURN client through lost answers: a channel whose refresh answers were lost keeps carrying data and is refreshed
/// again with its own number, even once the client's record of it has lapsed, and a refused refresh loses nothing; a
/// release retransmits until the server answers and can be sent again after silence, a vanished allocation is noticed at
/// its next request, and no server's nonce makes a request overflow.
/// </summary>
internal static class NatTurnRecoveryTests
{
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("198.51.100.7"), 4000);

    internal static void LostRefreshAnswersKeepTheChannel()
    {
        var harness = new TurnHarness(13);
        harness.Start();
        harness.RunFor(1_000_000);
        harness.Client.Permit(Peer.Address, "peer", harness.Now);
        harness.RunFor(1_000_000);
        var path = harness.Client.PathTo(Peer, "peer", harness.Now);
        harness.SendData(path, new byte[] { 0xE8, 1 });
        harness.RunFor(1_000_000);
        That(harness.Client.UsesChannel(Peer, harness.Now), "the channel is bound");

        // All six answers to the 480-second refresh are lost, though the server refreshed the binding each time.
        var boundAt = harness.Now;
        var swallowed = 0;
        harness.Intercept = bytes =>
        {
            if (swallowed == 6 || harness.Now < boundAt + 470_000_000) return false;
            if (!StunMessage.TryParse(bytes, 0, bytes.Length, out var answer) || answer.Method != StunMethod.ChannelBind) return false;
            swallowed++;
            return true;
        };

        const int Rounds = 120;
        for (var round = 0; round < Rounds; round++)
        {
            harness.RunFor(5_000_000);
            harness.PeerSends(Peer, new byte[] { 0xE8, (byte)round });
        }

        That(swallowed == 6, "every answer to the refresh was lost (" + swallowed + ")");
        That(harness.Server.Relayed == Rounds, "the server relayed every peer datagram (" + harness.Server.Relayed + ")");
        That(harness.Delivered.Count == Rounds && harness.Delivered.All(item => item.Path.Equals(path) && item.Channel),
            "and the client delivered every one over the channel (" + harness.Delivered.Count(item => item.Channel) + " of " + Rounds + ")");
        var channels = harness.Server.Allocations.Single().Channels;
        That(channels.Count == 1 && channels.ContainsKey(0x4000) && harness.Client.ChannelCount == 1, "the refresh was tried again with the same channel number");
        That(harness.Client.UsesChannel(Peer, harness.Now) && harness.SendData(path, new byte[] { 0xE8, 2 }) == 6, "and data still leaves as ChannelData");

        // Then every answer is lost for longer than a binding lasts. The server got each request and keeps the binding, so
        // it still relays the peer's data as ChannelData after the client's own record of the last answer has lapsed.
        const int QuietRounds = 140;
        var quietUntil = harness.Now + 660_000_000;
        harness.Intercept = bytes => harness.Now < quietUntil && StunMessage.TryParse(bytes, 0, bytes.Length, out var answer) && answer.Method == StunMethod.ChannelBind;
        var beforeQuiet = harness.Delivered.Count;
        for (var round = 0; round < QuietRounds; round++)
        {
            harness.RunFor(5_000_000);
            harness.PeerSends(Peer, new byte[] { 0xE8, (byte)round });
        }

        That(!harness.Client.UsesChannel(Peer, harness.Now), "the client's record of the binding lapsed");
        That(harness.Delivered.Count == beforeQuiet + QuietRounds && harness.Delivered.Skip(beforeQuiet).All(item => item.Channel),
            "yet every datagram the server relayed over the binding it kept was taken (" + (harness.Delivered.Count - beforeQuiet) + " of " + QuietRounds + ")");
        harness.SendData(path, new byte[] { 0xE8, 4 });
        harness.RunFor(1_000_000);
        That(harness.Client.UsesChannel(Peer, harness.Now) && channels.Count == 1 && channels.ContainsKey(0x4000), "and the next datagram bound the same number again");

        // Now the server refuses the next refresh. The binding it already holds still carries the peer's data until it
        // lapses, and after that the data arrives as Data indications: nothing is lost either way.
        const int RefusedRounds = 150;
        harness.Server.RefuseChannelBinds = true;
        var beforeRefusal = harness.Delivered.Count;
        for (var round = 0; round < RefusedRounds; round++)
        {
            harness.RunFor(5_000_000);
            harness.PeerSends(Peer, new byte[] { 0xE8, (byte)round });
        }

        That(harness.Delivered.Count == beforeRefusal + RefusedRounds, "every peer datagram still reached the client (" + (harness.Delivered.Count - beforeRefusal) + " of " + RefusedRounds + ")");
        That(harness.Delivered.Skip(beforeRefusal).Any(item => item.Channel) && harness.Delivered.Skip(beforeRefusal).Any(item => !item.Channel),
            "over the channel the server kept, then as Data indications once it lapsed");
        That(!harness.Client.UsesChannel(Peer, harness.Now) && harness.SendData(path, new byte[] { 0xE8, 3 }) > 6, "and data leaves as Send indications once the binding is gone");
    }

    internal static void ReleasesRetransmitUntilTheServerAnswers()
    {
        var harness = new TurnHarness(14);
        harness.Start();
        harness.RunFor(1_000_000);
        var lost = 0;
        harness.Server.LoseRequest = (_, bytes) => NatRelayRecoveryTests.IsRelease(bytes) && lost++ < 2;
        harness.Client.Release(harness.Now);
        That(harness.Client.State == TurnAllocationState.Releasing && harness.Server.Allocations.Count == 1, "the first copy is lost, and the release waits for its answer");
        harness.RunFor(3_000_000);
        That(lost == 3 && harness.Server.Releases == 1 && harness.Server.Allocations.Count == 0, "the third copy deletes the allocation (" + lost + " sent)");
        That(harness.Client.State == TurnAllocationState.Released && !harness.Client.ReleaseUnconfirmed, "and the release is settled");

        // A release nobody answers settles too, unconfirmed, and can go again once the 5-tuple turns out to be taken.
        var silent = new TurnHarness(15);
        silent.Start();
        silent.RunFor(1_000_000);
        silent.Server.LoseRequest = (_, bytes) => NatRelayRecoveryTests.IsRelease(bytes);
        silent.Client.Release(silent.Now);
        silent.RunFor(6_000_000);
        That(silent.Client.State == TurnAllocationState.Released && silent.Client.ReleaseUnconfirmed && silent.Server.Allocations.Count == 1,
            "after four unanswered copies it gives up, unconfirmed");
        silent.Server.LoseRequest = null;
        That(silent.Client.ReleaseAgain(silent.Now), "it can be sent again");
        silent.RunFor(1_000_000);
        That(!silent.Client.ReleaseUnconfirmed && silent.Server.Allocations.Count == 0, "and the second try deletes the allocation");
        That(!silent.Client.ReleaseAgain(silent.Now), "after which there is nothing left to release");

        // A client that never allocated has nothing to release, and is done at once.
        var unused = new TurnHarness(16);
        unused.Client.Release(unused.Now);
        That(unused.Client.State == TurnAllocationState.Released && unused.Sent.Count == 0, "a client that never allocated sends nothing");
    }

    internal static void AVanishedAllocationIsNoticedAtItsNextRequest()
    {
        var harness = new TurnHarness(17);
        harness.Start();
        harness.RunFor(1_000_000);
        harness.Server.ForgetAllocations();
        harness.Client.Permit(Peer.Address, "peer", harness.Now);
        harness.RunFor(1_000_000);
        That(harness.Client.State == TurnAllocationState.Failed && harness.Client.Failure == TurnFailure.AllocationMismatch,
            "a 437 to a permission means the server holds no allocation: it is lost (" + harness.Client.State + ", " + harness.Client.Failure + ")");
    }

    internal static void NoNonceMakesARequestOverflow()
    {
        // The longest realm and nonce a server may send, with the longest credential the control plane may mint.
        var user = new string('u', IceServerSet.MaximumCredentialLength);
        var harness = new TurnHarness(18, server =>
        {
            server.Realm = new string('r', TurnClient.MaximumRealmLength);
            server.Users[user] = ("turn-secret", long.MaxValue);
            server.NonceText = new string('n', StunMessage.MaximumTextBytes);
        }, user: user);
        harness.Start();
        harness.RunFor(1_000_000);
        That(harness.Client.State == TurnAllocationState.Ready, "the allocation comes up under the longest credentials (" + harness.Client.State + ")");
        var peers = Enumerable.Range(1, 12).Select(index => IPAddress.Parse("2001:db8::" + index.ToString("x", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        foreach (var peer in peers) harness.Client.Permit(peer, peer.ToString(), harness.Now);
        harness.RunFor(2_000_000);
        That(harness.Sent.All(datagram => datagram.Length <= StunMessage.MaximumBytes), "every request fits a datagram");
        That(harness.Client.State == TurnAllocationState.Ready && peers.All(harness.Client.IsPermitted),
            "and all twelve IPv6 permissions are installed (" + peers.Count(harness.Client.IsPermitted) + ")");
        That(harness.Server.PermissionRequests >= 12, "in batches small enough to fit beside the nonce (" + harness.Server.PermissionRequests + " requests)");
    }
}
