extern alias provider;

using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Shaping;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using static TopiaForge.Mods.Multiplayer.Tests.TransportFrames;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The traffic classes inside each connection's budget, and the host-wide uplink with its shedding.</summary>
internal static class TransportShapingTests
{
    private const int VoiceRate = 12 * 1024;
    private const int RealtimeRate = 8 * 1024;
    private const int SustainedRate = 32 * 1024;

    internal static void DatagramClassesStayWithinTheirShares()
    {
        var pair = new TransportPair(80);
        var (client, host) = pair.Establish();
        var datagrams = new List<(ProviderChannel Channel, byte[] Payload)>();
        var start = pair.Network.Now;
        Offer(pair, client, host, 4_000_000, voice: 400, realtime: 400, datagrams: datagrams);
        var seconds = (pair.Network.Now - start) / 1_000_000.0;
        var voice = Bytes(datagrams, ProviderChannel.Voice);
        var realtime = Bytes(datagrams, ProviderChannel.Realtime);
        That(voice <= (VoiceRate * seconds) + ClassShaper.DepthBytes && voice >= VoiceRate * seconds * 0.8,
            "voice holds 96 kbit/s however much is offered (" + voice + " bytes in " + seconds + " s)");
        That(realtime <= (RealtimeRate * seconds) + ClassShaper.DepthBytes && realtime >= RealtimeRate * seconds * 0.8,
            "realtime holds 64 kbit/s (" + realtime + " bytes)");
        That(pair.Network.Spins == 0, "a class held back by its share does not spin");
    }

    internal static void SessionKeepsItsShareUnderDatagramLoad()
    {
        var pair = new TransportPair(81);
        var (client, host) = pair.Establish();
        for (var index = 0; index < 40; index++) client.Send(ProviderChannel.Session, Pattern(16 * 1024, index), 0, 16 * 1024);
        var frames = new List<byte[]>();
        var datagrams = new List<(ProviderChannel Channel, byte[] Payload)>();
        Offer(pair, client, host, 3_000_000, voice: 400, realtime: 400, frames: frames, datagrams: datagrams);
        var before = frames.Sum(frame => (long)frame.Length) + host.BufferedBytes;
        Offer(pair, client, host, 4_000_000, voice: 400, realtime: 400, frames: frames, datagrams: datagrams);
        var session = frames.Sum(frame => (long)frame.Length) + host.BufferedBytes - before;

        // Packet overhead comes out of every class, so Session's payload runs a little under its 96 kbit/s share.
        That(session >= 4 * 12 * 1024 * 0.75, "with voice and realtime saturated, Session still moves its share (" + session + " bytes in 4 s)");
    }

    internal static void IdleClassesLeaveTheirShareToSession()
    {
        var pair = new TransportPair(82);
        var (client, host) = pair.Establish();
        for (var index = 0; index < 20; index++) client.Send(ProviderChannel.Session, Pattern(16 * 1024, index), 0, 16 * 1024);
        var frames = new List<byte[]>();
        Offer(pair, client, host, 3_000_000, frames: frames);
        var before = frames.Sum(frame => (long)frame.Length) + host.BufferedBytes;
        Offer(pair, client, host, 4_000_000, frames: frames);
        var session = frames.Sum(frame => (long)frame.Length) + host.BufferedBytes - before;
        That(session >= SustainedRate * 4 * 0.85, "alone, Session uses the whole 256 kbit/s (" + session + " bytes in 4 s)");
    }

    internal static void TheUplinkIsSharedFairlyAndShedsVoiceFirst()
    {
        var network = new VirtualDatagramNetwork(83);
        var hostOptions = new TransportOptions { Random = new SeededTransportRandom(830), UplinkBytesPerSecond = 64 * 1024 };
        var host = new TransportEndpoint(TransportPair.Key(83, 1), hostOptions, network.Now);
        var hostNode = network.AddEndpoint("host", host);
        var ends = new List<(TransportConnection AtHost, TransportConnection AtClient)>();
        for (var index = 0; index < 3; index++)
        {
            var endpoint = new TransportEndpoint(TransportPair.Key(83, 10 + index), new TransportOptions { Random = new SeededTransportRandom(831 + index) }, network.Now);
            network.AddEndpoint("client" + index, endpoint);
            var atClient = endpoint.Connect(VirtualDatagramNetwork.PathTo(hostNode), network.Now);
            TransportConnection? atHost = null;
            That(network.RunUntil(() => atClient.State == TransportConnectionState.Established && (atHost != null || host.TryAccept(out atHost)), 5_000_000),
                "client " + index + " connects");
            ends.Add((atHost!, atClient));
        }

        foreach (var (atHost, _) in ends)
        {
            for (var index = 0; index < 30; index++) atHost.Send(ProviderChannel.Session, Pattern(16 * 1024, index), 0, 16 * 1024);
        }

        var voiceEarly = 0L;
        var voiceLate = 0L;
        var maximumLevel = 0;
        var sentBefore = ends.Select(end => end.AtHost.BytesSent).ToArray();
        var start = network.Now;
        for (var tick = 0; tick < 1_200; tick++)
        {
            foreach (var (atHost, _) in ends) atHost.Send(ProviderChannel.Voice, new byte[200], 0, 200);
            network.RunFor(5_000);
            maximumLevel = Math.Max(maximumLevel, host.SheddingLevel);
            foreach (var (_, atClient) in ends)
            {
                while (atClient.TryReceive(out var channel, out var frame))
                {
                    if (channel != ProviderChannel.Voice) continue;
                    if (tick < 200) voiceEarly += frame.Length;
                    else if (tick >= 1_000) voiceLate += frame.Length;
                }
            }
        }

        var seconds = (network.Now - start) / 1_000_000.0;
        var shares = ends.Select((end, index) => end.AtHost.BytesSent - sentBefore[index]).ToArray();
        var total = shares.Sum();
        That(total <= (64 * 1024 * seconds) + (64 * 1024 / 10) + (3 * 16 * 1024), "the host never outruns its uplink (" + total + " bytes in " + seconds + " s)");
        That(shares.All(share => share >= total * 0.25), "each connection gets a fair part (" + string.Join(", ", shares) + ")");
        That(maximumLevel >= 2 && voiceLate < voiceEarly / 4, "sustained pressure stops voice first (level " + maximumLevel + ", voice " + voiceEarly + " then " + voiceLate + ")");
        That(network.RunUntil(() => host.SheddingLevel == 0, 60_000_000), "once the pressure ends, shedding steps back down");
    }

    /// <summary>Offers datagrams every 5 ms for <paramref name="micros"/>, draining what arrives at the host.</summary>
    private static void Offer(
        TransportPair pair,
        TransportConnection client,
        TransportConnection host,
        long micros,
        int voice = 0,
        int realtime = 0,
        List<byte[]>? frames = null,
        List<(ProviderChannel Channel, byte[] Payload)>? datagrams = null)
    {
        frames ??= new List<byte[]>();
        var until = pair.Network.Now + micros;
        while (pair.Network.Now < until)
        {
            if (voice > 0) client.Send(ProviderChannel.Voice, new byte[voice], 0, voice);
            if (realtime > 0) client.Send(ProviderChannel.Realtime, new byte[realtime], 0, realtime);
            pair.Network.RunFor(5_000);
            Drain(host, frames, datagrams);
        }
    }

    private static long Bytes(List<(ProviderChannel Channel, byte[] Payload)> datagrams, ProviderChannel channel) =>
        datagrams.Where(item => item.Channel == channel).Sum(item => (long)item.Payload.Length);
}
