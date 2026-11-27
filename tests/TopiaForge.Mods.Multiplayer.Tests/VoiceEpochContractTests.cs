extern alias provider;

using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.CommunicationRoutingTests;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class VoiceEpochContractTests
{
    internal static void Run()
    {
        VoicePacketTests.DatagramsRoundTripOneOrTwoFrames();
        VoicePacketTests.MalformedDatagramsAreRefusedWhole();
        That(VoicePacket.MaximumBytes(160) + 40 <= provider::TopiaForge.Multiplayer.Transport.TransportOptions.MaximumDatagramBytes,
            "two maximum Opus frames plus transport overhead fit the unchanged datagram bound");
        var legacyUplink = new byte[] { 1, 1, 0, 1, 0, 1, 1 };
        var legacyRelay = new byte[] { 2, 1, 1, 1, 0, 1, 0, 1, 1 };
        That(!VoicePacket.TryReadUplink(legacyUplink, 160, out _) && !VoicePacket.TryReadRelayed(legacyRelay, 160, out _),
            "epoch-less protocol-one packets fail closed in both directions");
        foreach (var custom in new[] { false, true })
        {
            using var rig = new VoiceSessionHarness();
            if (custom) rig.Options.JoinPolicy = new PermissivePolicy();
            var pair = rig.Network.Connect("legacy");
            rig.Host.Accept(pair.HostEnd);
            var peer = new RawPeer(pair.ClientEnd);
            peer.Join("Sam", protocolVersion: "1.0.0");
            rig.Flush();
            var messages = peer.Receive();
            That(messages.Any(message => message.Type == (custom ? MessageType.JoinAccepted : MessageType.JoinRejected)),
                "normal admission refuses the old major; custom admission fixture bypasses the version gate");
            if (!custom) continue;
            pair.ClientEnd.Send(EngineChannel.Voice, new ArraySegment<byte>(legacyUplink));
            rig.Advance(5);
            That(!rig.Host.TryReceiveVoiceFrame(out _) && rig.Host.GetVoiceStatistics().Dropped(VoiceDropReason.Malformed) == 1,
                "custom admission cannot make legacy voice playable");
        }
        VoiceBudgetTests.FourSpeakersFitTheirBudget();
        VoiceRestartTests.ARestartIsHeardFromTheDatagramBesideItsAcknowledgement();
        Console.WriteLine("Voice epoch contract: frozen vectors, malformed/legacy formats, default/custom admission, transport bounds, four-speaker budget and acknowledgement ordering passed.");
    }

    private sealed class PermissivePolicy : IJoinPolicy
    {
        public JoinDecision Evaluate(JoinRequestContext context) => JoinDecision.Accept();
    }
}
