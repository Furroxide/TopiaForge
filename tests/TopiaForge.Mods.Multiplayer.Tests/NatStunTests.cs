extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The STUN codec against RFC 5769's sample messages, plus everything a hostile datagram can try.</summary>
internal static class NatStunTests
{
    // RFC 5769, sections 2.1 to 2.4, transcribed byte for byte and checked against an independent implementation.
    private static readonly byte[] Request = Convert.FromHexString(
        "000100582112a442b7e7a701bc34d686fa87dfae" +
        "802200105354554e207465737420636c69656e74" +
        "002400046e0001ff80290008932ff9b151263b36" +
        "000600096576746a3a6836765920202000080014" +
        "9aeaa70cbfd8cb56781ef2b5b2d3f249c1b571a2" +
        "80280004e57a3bcf");

    private static readonly byte[] IPv4Response = Convert.FromHexString(
        "0101003c2112a442b7e7a701bc34d686fa87dfae" +
        "8022000b7465737420766563746f722000200008" +
        "0001a147e112a643000800142b91f599fd9e90c3" +
        "8c7489f92af9ba53f06be7d780280004c07d4c96");

    private static readonly byte[] IPv6Response = Convert.FromHexString(
        "010100482112a442b7e7a701bc34d686fa87dfae" +
        "8022000b7465737420766563746f722000200014" +
        "0002a1470113a9faa5d3f179bc25f4b5bed2b9d9" +
        "00080014a382954e4be67bf11784c97c8292c275" +
        "bfe3ed4180280004c8fb0b4c");

    private static readonly byte[] LongTermRequest = Convert.FromHexString(
        "000100602112a44278ad3433c6ad72c029da412e" +
        "00060012e3839ee38388e383aae38383e382afe3" +
        "82b900000015001c662f2f3439396b3935346436" +
        "4f4c33346f4c394653547679363473410014000b" +
        "6578616d706c652e6f72670000080014f6702465" +
        "6dd64a3e02b8e0712e85c9a28ca89666");

    private const string ShortTermPassword = "VOkJxbRl1RmTxUk/WvJxBt";

    internal static void StunMatchesTheRfc5769Vectors()
    {
        var shortKey = StunCredentials.ShortTermKey(ShortTermPassword);
        That(StunMessage.TryParse(Request, 0, Request.Length, out var request), "the sample request parses");
        That(request.Method == StunMethod.Binding && request.Class == StunClass.Request && request.HasFingerprint, "it is a Binding request with a fingerprint");
        That(request.VerifyIntegrity(shortKey), "its MESSAGE-INTEGRITY verifies with the short-term key");
        That(!request.VerifyIntegrity(StunCredentials.ShortTermKey("VOkJxbRl1RmTxUk/WvJxBu")), "and fails with any other");
        That(request.TryGetText(StunAttribute.Username, 513, out var username) && username == "evtj:h6vY", "its USERNAME ignores the spaces RFC 5769 pads it with");
        That(request.TryGetUInt32(StunAttribute.Priority, out var priority) && priority == 0x6E0001FF, "PRIORITY reads as written");
        That(request.TryGetUInt64(StunAttribute.IceControlled, out var tieBreaker) && tieBreaker == 0x932FF9B151263B36UL, "ICE-CONTROLLED carries the tie-breaker");
        That(request.UnknownRequired.Count == 0, "every comprehension-required attribute is known");

        That(StunMessage.TryParse(IPv4Response, 0, IPv4Response.Length, out var v4), "the IPv4 response parses");
        That(v4.Class == StunClass.Success && v4.VerifyIntegrity(shortKey) && v4.HasFingerprint, "and verifies");
        That(v4.TryGetAddress(StunAttribute.XorMappedAddress, out var mapped4) && mapped4.Equals(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 32853)),
            "XOR-MAPPED-ADDRESS unmasks to 192.0.2.1 port 32853");
        That(StunMessage.TryParse(IPv6Response, 0, IPv6Response.Length, out var v6) && v6.VerifyIntegrity(shortKey), "the IPv6 response parses and verifies");
        That(v6.TryGetAddress(StunAttribute.XorMappedAddress, out var mapped6) &&
             mapped6.Equals(new IPEndPoint(IPAddress.Parse("2001:db8:1234:5678:11:2233:4455:6677"), 32853)), "and unmasks the IPv6 address");

        var longKey = StunCredentials.LongTermKey("マトリックス", "example.org", "TheMatrIX");
        That(StunMessage.TryParse(LongTermRequest, 0, LongTermRequest.Length, out var longTerm) && longTerm.VerifyIntegrity(longKey),
            "the long-term request verifies with MD5(username:realm:password)");
        That(longTerm.TryGetText(StunAttribute.Realm, 763, out var realm) && realm == "example.org" &&
             longTerm.TryGetText(StunAttribute.Nonce, 763, out var nonce) && nonce == "f//499k954d6OL34oL9FSTvy64sA", "realm and nonce read back");

        // The writer reproduces the long-term vector byte for byte, since its padding is zeros, as the writer's is.
        var writer = new StunMessageWriter();
        var buffer = new byte[StunMessage.MaximumBytes];
        writer.Begin(buffer, StunMethod.Binding, StunClass.Request, StunTransactionId.Read(LongTermRequest, 8));
        writer.AddText(StunAttribute.Username, "マトリックス");
        writer.AddText(StunAttribute.Nonce, "f//499k954d6OL34oL9FSTvy64sA");
        writer.AddText(StunAttribute.Realm, "example.org");
        var length = writer.Finish(longKey, false);
        That(buffer.AsSpan(0, length).SequenceEqual(LongTermRequest), "the writer reproduces the long-term vector exactly");

        // And it writes both XOR-MAPPED-ADDRESS attributes exactly as the responses carry them.
        writer.Begin(buffer, StunMethod.Binding, StunClass.Success, StunTransactionId.Read(IPv6Response, 8));
        writer.AddAddress(StunAttribute.XorMappedAddress, mapped4);
        writer.AddAddress(StunAttribute.XorMappedAddress, mapped6);
        That(buffer.AsSpan(20, 12).SequenceEqual(IPv4Response.AsSpan(36, 12)) && buffer.AsSpan(32, 24).SequenceEqual(IPv6Response.AsSpan(36, 24)),
            "masked addresses match the vectors");
    }

    internal static void TamperedVectorsNeverVerify()
    {
        var key = StunCredentials.ShortTermKey(ShortTermPassword);
        foreach (var vector in new[] { Request, IPv4Response, IPv6Response })
        {
            for (var index = 0; index < vector.Length; index++)
            {
                var copy = (byte[])vector.Clone();
                copy[index] ^= 0x01;
                if (!StunMessage.TryParse(copy, 0, copy.Length, out var message)) continue;

                // Integrity covers every byte before it, padding included; a flip that survives parsing either fails it or
                // turned the fingerprint into an attribute that is ignored behind the integrity check.
                That(!message.VerifyIntegrity(key) || !message.HasFingerprint, "flipping byte " + index + " breaks the fingerprint or the integrity check");
            }
        }
    }

    internal static void HostileStunNeverThrows()
    {
        That(!StunMessage.TryParse(null, 0, 0, out _) && !StunMessage.TryParse(Request, -1, 20, out _) &&
             !StunMessage.TryParse(Request, 0, Request.Length + 1, out _) && !StunMessage.TryParse(Request, Request.Length - 4, 20, out _),
            "bad arguments are refused, not thrown");
        That(!StunMessage.TryParse(Request, 0, 19, out _), "a short header is refused");
        That(!Parses(Patch(Request, 0, 0x40)), "a set top bit is refused");
        That(!Parses(Patch(Request, 4, 0x22)), "a wrong magic cookie is refused");
        That(!Parses(Patch(Request, 3, 0x59)), "a length that disagrees with the datagram is refused");
        That(!Parses(Patch(Request, 21, 0xFF)), "an attribute overrunning the message is refused");
        That(!Parses(Patch(Request, Request.Length - 1, 0x00)), "a wrong fingerprint is refused");
        var send = (byte[])IPv4Response.Clone();
        send[0] = 0x00;
        send[1] = 0x16;
        That(!Parses(send), "a Send request, which does not exist, is refused");
        That(!StunMessage.TryParse(new byte[1600], 0, 1600, out _), "a datagram past 1,500 bytes is refused");

        var writer = new StunMessageWriter();
        var buffer = new byte[StunMessage.MaximumBytes];
        writer.Begin(buffer, StunMethod.Binding, StunClass.Request, StunTransactionId.Read(Request, 8));
        for (var index = 0; index < StunMessage.MaximumAttributes + 1; index++) writer.AddUInt32(StunAttribute.Priority, (uint)index);
        var many = writer.Finish(null, false);
        That(!StunMessage.TryParse(buffer, 0, many, out _), "more than 32 attributes are refused");

        writer.Begin(buffer, StunMethod.Binding, StunClass.Success, StunTransactionId.Read(Request, 8));
        writer.AddBytes(StunAttribute.XorMappedAddress, new byte[] { 0, 3, 1, 2, 3, 4, 5, 6 }, 0, 8);
        writer.AddBytes(StunAttribute.Username, new byte[] { 0xC3, 0x28 }, 0, 2);
        writer.AddBytes(StunAttribute.Realm, new byte[] { 0x61, 0x0A }, 0, 2);
        writer.AddBytes(StunAttribute.ErrorCode, new byte[] { 0, 0, 7, 1 }, 0, 4);
        writer.AddBytes(StunAttribute.Lifetime, new byte[] { 0, 0 }, 0, 2);
        var odd = writer.Finish(null, true);
        That(StunMessage.TryParse(buffer, 0, odd, out var oddMessage), "a well-framed message with bad values still parses");
        That(!oddMessage.TryGetAddress(StunAttribute.XorMappedAddress, out _), "an unknown address family reads as absent");
        That(!oddMessage.TryGetText(StunAttribute.Username, 513, out _), "malformed UTF-8 reads as absent");
        That(!oddMessage.TryGetText(StunAttribute.Realm, 763, out _), "a control character reads as absent");
        That(!oddMessage.TryGetErrorCode(out _), "an error class outside 3 to 6 reads as absent");
        That(!oddMessage.TryGetUInt32(StunAttribute.Lifetime, out _), "a short integer reads as absent");

        var random = new Random(5769);
        var scratch = new byte[StunMessage.MaximumBytes];
        for (var round = 0; round < 20_000; round++)
        {
            var source = round % 3 == 0 ? Request : round % 3 == 1 ? IPv4Response : IPv6Response;
            var length = round % 5 == 0 ? random.Next(0, 200) : source.Length;
            if (round % 5 == 0) random.NextBytes(scratch.AsSpan(0, length));
            else source.CopyTo(scratch, 0);
            for (var flips = random.Next(0, 4); flips > 0 && length > 0; flips--) scratch[random.Next(length)] = (byte)random.Next(256);
            if (StunMessage.TryParse(scratch, 0, length, out var parsed))
            {
                parsed.VerifyIntegrity(StunCredentials.ShortTermKey(ShortTermPassword));
                parsed.TryGetAddress(StunAttribute.XorMappedAddress, out _);
                parsed.TryGetText(StunAttribute.Username, 513, out _);
                parsed.TryGetErrorCode(out _);
                parsed.TryGetUnknownAttributes(out _);
            }
        }
    }

    internal static void NothingRidesBehindIntegrity()
    {
        var key = StunCredentials.ShortTermKey("integrity-integrity-key");
        var writer = new StunMessageWriter();
        var buffer = new byte[StunMessage.MaximumBytes];
        var id = StunTransactionId.Read(Request, 8);
        writer.Begin(buffer, StunMethod.Binding, StunClass.Success, id);
        writer.AddAddress(StunAttribute.XorMappedAddress, new IPEndPoint(IPAddress.Parse("198.51.100.4"), 4000));
        var length = writer.Finish(key, false);

        // Append an unauthenticated address after the integrity attribute, then a fingerprint, as an attacker might.
        var forged = new byte[length + 12 + 8];
        Buffer.BlockCopy(buffer, 0, forged, 0, length);
        writer.Begin(buffer, StunMethod.Binding, StunClass.Success, id);
        writer.AddAddress(StunAttribute.XorPeerAddress, new IPEndPoint(IPAddress.Parse("203.0.113.66"), 5000));
        Buffer.BlockCopy(buffer, 20, forged, length, 12);
        forged[2] = (byte)((forged.Length - 20) >> 8);
        forged[3] = (byte)(forged.Length - 20);
        var crc = StunCrc32.Compute(forged, 0, length + 12) ^ StunMessage.FingerprintXor;
        forged[length + 12] = 0x80;
        forged[length + 13] = 0x28;
        forged[length + 15] = 4;
        StunTransactionId.WriteUInt32(forged, length + 16, crc);
        That(StunMessage.TryParse(forged, 0, forged.Length, out var message), "the message parses");
        That(message.VerifyIntegrity(key), "its integrity still covers the part before it");
        That(!message.Has(StunAttribute.XorPeerAddress), "but the attribute behind the integrity check is ignored");
        That(message.TryGetAddress(StunAttribute.XorMappedAddress, out var mapped) && mapped.Port == 4000, "while the authenticated one reads");

        writer.Begin(buffer, StunMethod.Binding, StunClass.Request, id);
        writer.AddUInt32(0x7001, 1);
        writer.AddUInt32(0xC001, 2);
        var unknown = writer.Finish(null, true);
        That(StunMessage.TryParse(buffer, 0, unknown, out var withUnknown) && withUnknown.UnknownRequired.Count == 1 && withUnknown.UnknownRequired[0] == 0x7001,
            "an unknown comprehension-required type is reported and an optional one skipped");
    }

    internal static void TransactionsRetransmitThenGiveUp()
    {
        var sent = new List<long>();
        long clock = 0;
        var client = new StunClient(new SeededTransportRandom(8489), (_, _, _) => sent.Add(clock));
        var owner = new RecordingOwner();
        var server = new TransportPath(new byte[] { 1, 2, 3 }, TransportPathKind.Direct);
        var id = client.NewTransactionId();
        var request = new byte[20];
        new StunMessageWriter().Begin(request, StunMethod.Binding, StunClass.Request, id);
        var transaction = client.Begin(server, request, 20, id, StunTiming.Gathering, owner, null, clock);
        That(transaction != null && sent.SequenceEqual(new long[] { 0 }), "the first copy goes out at once");
        while (owner.TimedOut == 0)
        {
            clock = client.NextTimeoutMicros();
            client.Poll(clock);
        }

        That(sent.SequenceEqual(new long[] { 0, 500_000, 1_500_000, 3_500_000 }), "copies double their spacing: " + string.Join(",", sent));
        That(clock == StunTiming.Gathering.TotalMicros && clock == 5_500_000, "and the transaction fails after the final wait");
        That(StunTiming.Rfc8489.TotalMicros == 39_500_000, "RFC 8489's own example takes 39.5 seconds");

        id = client.NewTransactionId();
        new StunMessageWriter().Begin(request, StunMethod.Binding, StunClass.Request, id);
        client.Begin(server, request, 20, id, StunTiming.Check, owner, "check", clock);
        var response = Response(id);
        That(!client.TryComplete(response, new TransportPath(new byte[] { 9 }, TransportPathKind.Direct), clock), "a response from another path matches nothing");
        owner.Accept = false;
        That(!client.TryComplete(response, server, clock) && client.Outstanding == 1, "a response the owner refuses leaves the request running");
        owner.Accept = true;
        That(client.TryComplete(response, server, clock) && owner.Responses == 1 && client.Outstanding == 0, "an accepted one completes it");
        That(!client.TryComplete(response, server, clock), "and a duplicate matches nothing");
    }

    private static StunMessage Response(StunTransactionId id)
    {
        var buffer = new byte[64];
        var writer = new StunMessageWriter();
        writer.Begin(buffer, StunMethod.Binding, StunClass.Success, id);
        var length = writer.Finish(null, true);
        StunMessage.TryParse(buffer, 0, length, out var message);
        return message;
    }

    private static bool Parses(byte[] bytes) => StunMessage.TryParse(bytes, 0, bytes.Length, out _);

    private static byte[] Patch(byte[] source, int index, byte value)
    {
        var copy = (byte[])source.Clone();
        copy[index] = value;
        return copy;
    }

    private sealed class RecordingOwner : IStunTransactionOwner
    {
        internal bool Accept { get; set; } = true;

        internal int Responses { get; private set; }

        internal int TimedOut { get; private set; }

        public bool Accepts(StunTransaction transaction, StunMessage response) => Accept;

        public void OnResponse(StunTransaction transaction, StunMessage response, long now) => Responses++;

        public void OnTimeout(StunTransaction transaction, long now) => TimedOut++;
    }
}
