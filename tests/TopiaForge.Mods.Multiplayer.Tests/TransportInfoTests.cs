extern alias provider;

using System.Text;
using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The unauthenticated info query a browser uses before it joins.</summary>
internal static class TransportInfoTests
{
    internal static void BrowsersLearnSessionInfoWithoutAHandshake()
    {
        var pair = new TransportPair(90);
        var info = Encoding.UTF8.GetBytes("Open Sandbox, 3 of 8 players");
        pair.Host.SetInfo(info);
        var sizes = new List<int>();
        pair.Network.Observe = (_, _, bytes) => sizes.Add(bytes.Length);
        pair.Client.QueryInfo(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now);
        TransportInfoReply? reply = null;
        That(pair.Network.RunUntil(() => pair.Client.TryReceiveInfo(out reply), 1_000_000), "the host answers");
        That(reply!.Info.SequenceEqual(info) && reply.IsCompatible && reply.Path.Equals(VirtualDatagramNetwork.PathTo(pair.HostNode)),
            "with its info, from its own path");
        That(reply.RoundTripMicros == 2 * pair.Network.Conditions.LatencyMicros, "and the round trip is measured");
        That(sizes.SequenceEqual(new[] { InfoPackets.QuerySize, InfoPackets.ReplyHeaderSize + info.Length }),
            "the query fills a datagram and the reply is smaller");
        That(pair.Host.ConnectionCount == 0 && pair.Client.ConnectionCount == 0 && pair.Host.HalfOpenCount == 0, "and nobody keeps state");
    }

    internal static void InfoRepliesAreBoundedMatchedAndRateLimited()
    {
        var pair = new TransportPair(91);
        Throws<ArgumentException>(() => pair.Host.SetInfo(new byte[InfoPackets.MaximumInfoBytes + 1]), "info must fit one reply");
        var hostPath = VirtualDatagramNetwork.PathTo(pair.HostNode);
        pair.Client.QueryInfo(hostPath, pair.Network.Now);
        pair.Network.RunFor(100_000);
        That(!pair.Client.TryReceiveInfo(out _) && pair.Host.Statistics.InfoRepliesSent == 0, "a host without info stays silent");

        pair.Host.SetInfo(new byte[InfoPackets.MaximumInfoBytes]);
        var replySizes = new List<int>();
        byte[]? query = null;
        pair.Network.Observe = (from, _, bytes) =>
        {
            if (from == pair.HostNode) replySizes.Add(bytes.Length);
            else if (bytes[0] == PacketType.InfoQuery) query = bytes;
        };
        pair.Client.QueryInfo(hostPath, pair.Network.Now);
        That(pair.Network.RunUntil(() => pair.Client.TryReceiveInfo(out _), 1_000_000) && replySizes.Single() == TransportOptions.MaximumDatagramBytes,
            "the largest info fills a reply exactly");

        // Replies must come from the queried path and carry the query's id.
        var impostor = pair.Network.AddRaw("impostor");
        pair.Client.QueryInfo(hostPath, pair.Network.Now);
        pair.Network.RunFor(1_000);
        var id = PacketWriter.ReadUInt64(query!, 2);
        var dropped = pair.Client.Statistics.DatagramsDropped;
        pair.Network.Inject(impostor, pair.ClientNode, Reply(id, new byte[] { 6, 6, 6 }));
        pair.Network.Inject(impostor, pair.ClientNode, Reply(id ^ 1, new byte[] { 6 }));

        // The impostors land at 11 ms; the genuine reply needs a full round trip, 20 ms.
        pair.Network.RunFor(15_000);
        That(pair.Client.Statistics.DatagramsDropped == dropped + 2 && !pair.Client.TryReceiveInfo(out _),
            "a reply from another path, or with another id, is dropped");
        That(pair.Network.RunUntil(() => pair.Client.TryReceiveInfo(out var genuine) && genuine.Info.Length == InfoPackets.MaximumInfoBytes, 1_000_000),
            "and the genuine reply still arrives");

        var browser = pair.Network.AddRaw("browser");
        var future = Query(77);
        future[1] = 2;
        pair.Network.Inject(browser, pair.HostNode, future);
        pair.Network.Inject(browser, pair.HostNode, Query(78).Take(InfoPackets.QuerySize - 1).ToArray());
        pair.Network.RunFor(50_000);
        var answer = browser.Inbox.Single().Bytes;
        That(answer[1] == HandshakePackets.Version && answer.Length == InfoPackets.ReplyHeaderSize,
            "a browser on another version learns ours and no info; a short query earns nothing");

        browser.Inbox.Clear();
        pair.Network.RunFor(1_000_000);
        for (var index = 0; index < 12; index++) pair.Network.Inject(browser, pair.HostNode, Query(100UL + (ulong)index));
        pair.Network.RunFor(50_000);
        That(browser.Inbox.Count == 10, "a source gets ten replies a second (" + browser.Inbox.Count + ")");
    }

    private static byte[] Query(ulong id)
    {
        var datagram = new byte[InfoPackets.QuerySize];
        var writer = new PacketWriter();
        writer.Reset(datagram, 0, datagram.Length);
        InfoPackets.WriteQuery(writer, id);
        return datagram;
    }

    private static byte[] Reply(ulong id, byte[] info)
    {
        var datagram = new byte[InfoPackets.ReplyHeaderSize + info.Length];
        var writer = new PacketWriter();
        writer.Reset(datagram, 0, datagram.Length);
        InfoPackets.WriteReply(writer, id, info);
        return datagram;
    }
}
