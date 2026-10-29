extern alias provider;

using System.Text;
using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ProviderEngine = provider::TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Seeded fuzzing of what the live flows read from other players: join content, admission reports, the host's admission
/// decision, and the descriptor endpoint's request line. Nothing throws, and every input is refused or accepted the
/// same way an independent reading of the rules decides.
/// </summary>
internal static class LiveFormatFuzzTests
{
    internal static void JoinContentHasOneSpelling()
    {
        var fuzz = new FuzzMutator(154);
        var corpus = new List<byte[]> { JoinContent.Encode(null, Array.Empty<byte>()), JoinContent.Encode(new byte[32], new byte[] { 1, 2, 3 }) };
        using var run = FuzzLedger.Start("join content");
        for (var round = 0; round < FuzzMutator.Iterations(20_000); round++, run.Inputs++)
        {
            var candidate = fuzz.Random.Next(4) switch
            {
                0 => Encoded(fuzz),
                1 => Assembled(fuzz),
                2 => fuzz.Bytes(fuzz.Random.Next(48)),
                _ => fuzz.Mutate(fuzz.Pick(corpus), 70_000)
            };
            if (!JoinContent.TryDecode(candidate, out var proof, out var content)) continue;
            if (candidate.Length == 0)
            {
                That(proof == null && content.Length == 0, "no bytes at all is a player with nothing to present");
                continue;
            }

            That(JoinContent.Encode(proof, content).SequenceEqual(candidate),
                "accepted join content has one spelling, so its length means what it says: " + Convert.ToHexString(candidate.Take(256).ToArray()));
            if (corpus.Count < 256) corpus.Add(candidate);
        }
    }

    internal static void AdmissionReportsHaveOneSpelling()
    {
        var fuzz = new FuzzMutator(155);
        var corpus = Enum.GetValues<AdmissionCode>().Select(code => AdmissionReport.Encode(code, new[] { "Zombies 0.2.0 is missing." })).ToList();
        using var run = FuzzLedger.Start("admission reports");
        for (var round = 0; round < FuzzMutator.Iterations(10_000); round++, run.Inputs++)
        {
            var candidate = fuzz.Chance(20)
                ? AdmissionReport.Encode(fuzz.Pick(Enum.GetValues<AdmissionCode>()), Enumerable.Range(0, fuzz.Random.Next(4)).Select(_ => WireFormatFuzzTests.DisplayText(fuzz, 64)).ToList())
                : fuzz.Mutate(fuzz.Pick(corpus), 20_000);
            if (!AdmissionReport.TryDecode(candidate, out var code, out var problems)) continue;
            That(AdmissionReport.Encode(code, problems).SequenceEqual(candidate), "an accepted report writes back as itself: " + Encoding.UTF8.GetString(candidate));
            if (corpus.Count < 256) corpus.Add(candidate);
        }
    }

    /// <summary>
    /// The host's admission against join content it did not write: the decision is exactly what the content's form,
    /// the proof and the runtime's content admission say, and every refusal carries a report the player can read.
    /// </summary>
    internal static void TheHostAdmitsExactlyWhatTheRulesAdmit()
    {
        var fuzz = new FuzzMutator(156);
        var content = new TestContent();
        var transportKey = fuzz.Bytes(32);
        var passwordKey = PasswordProof.DeriveKey("open sesame", transportKey);
        var link = new NetworkLink(1, fuzz.Bytes(32), default, fuzz.Bytes(32));
        var proof = PasswordProof.Prove(passwordKey, link.ChannelBinding);
        var corpus = new List<byte[]> { JoinContent.Encode(proof, content.ClientContent()), JoinContent.Encode(null, content.ClientContent()) };
        using var run = FuzzLedger.Start("host admission");
        for (var round = 0; round < FuzzMutator.Iterations(10_000); round++, run.Inputs++)
        {
            var locked = fuzz.Chance(50);
            var policy = new HostPolicy(new BanList(), locked ? passwordKey : null, content, () => round * 20_000_000L);
            var body = fuzz.Chance(25) ? fuzz.Pick(corpus) : fuzz.Mutate(fuzz.Pick(corpus), 128);
            var decision = policy.Evaluate(new ProviderEngine.JoinRequestContext(
                link,
                WireText.ToHex(fuzz.Bytes(32)),
                new ProviderEngine.JoinRequestMessage(TopiaForge.Mods.MultiplayerModule.ProtocolVersion, "Guest", Array.Empty<ProviderEngine.ContractEntry>(), body),
                Array.Empty<ProviderEngine.ContractEntry>(),
                1,
                8));
            var readable = JoinContent.TryDecode(body, out var sentProof, out var sentContent);
            var proved = !locked || (sentProof != null && PasswordProof.Verify(passwordKey, link.ChannelBinding, sentProof));
            var admitted = readable && proved ? content.Admit(sentContent) : null;
            var expected = admitted != null && admitted.IsAdmitted;
            That(decision.Accepted == expected, "the host admits exactly what the rules admit (" + decision.Message + "): " + Convert.ToHexString(body));
            if (decision.Accepted) continue;

            // Every refusal carries a readable report, a content refusal included, and that one names its problems.
            That(AdmissionReport.TryDecode(decision.Report, out var code, out var problems), "and every refusal carries a readable report: " + Convert.ToHexString(body));
            if (admitted != null) That(code == admitted.Code && problems.SequenceEqual(admitted.Problems), "a content refusal reports the content's code and problems");
        }
    }

    /// <summary>
    /// The descriptor endpoint's request line, however a connection's bytes are split, read against an independent
    /// reading of the rules: the line before the first newline within 128 bytes, which must be one of two exact forms.
    /// </summary>
    internal static void DescriptorRequestsReadTheSameHoweverTheyArrive()
    {
        var fuzz = new FuzzMutator(157);
        var sha = new string('a', 64);
        var corpus = new[] { "TFD1 descriptor\n", "TFD1 package " + sha + "\n", "TFD1 package " + sha.ToUpperInvariant() + "\n", "TFD1 descriptor\r\n", "HELLO\n", new string('x', 140) }
            .Select(Encoding.ASCII.GetBytes).ToList();
        var seen = new HashSet<DescriptorRequestKind>();
        using var run = FuzzLedger.Start("descriptor requests");
        for (var round = 0; round < FuzzMutator.Iterations(20_000); round++, run.Inputs++)
        {
            var sent = fuzz.Chance(10) ? fuzz.Bytes(fuzz.Random.Next(200)) : fuzz.Mutate(fuzz.Pick(corpus), 220);
            var buffer = new byte[DescriptorRequest.MaximumBytes];
            var length = 0;
            var offset = 0;
            DescriptorRequest request;
            do
            {
                // A connection reads what has arrived into the request's 128 bytes, then looks for the line.
                var take = Math.Min(Math.Min(sent.Length - offset, buffer.Length - length), 1 + fuzz.Random.Next(48));
                Buffer.BlockCopy(sent, offset, buffer, length, take);
                offset += take;
                length += take;
                request = DescriptorRequest.Read(buffer, length);
                var expected = Expected(buffer.AsSpan(0, length).ToArray());
                That(request.Kind == expected.Kind && request.Sha256 == expected.Sha256,
                    "a request reads as the rules say after " + length + " bytes (" + request.Kind + " for " + expected.Kind + "): " + Convert.ToHexString(sent));
            }
            while (request.Kind == DescriptorRequestKind.Incomplete && offset < sent.Length && length < buffer.Length);

            seen.Add(request.Kind);
            if (corpus.Count < 512 && fuzz.Chance(5)) corpus.Add(sent);
        }

        That(seen.Count == 5, "the fuzzing reaches every kind of request (" + string.Join(", ", seen) + ")");
    }

    private static (DescriptorRequestKind Kind, string? Sha256) Expected(byte[] received)
    {
        var newline = Array.IndexOf(received, (byte)'\n');
        if (newline < 0) return (received.Length >= DescriptorRequest.MaximumBytes ? DescriptorRequestKind.Overlong : DescriptorRequestKind.Incomplete, null);
        var line = received.AsSpan(0, newline);
        if (line.SequenceEqual(Encoding.ASCII.GetBytes("TFD1 descriptor"))) return (DescriptorRequestKind.Descriptor, null);
        var prefix = Encoding.ASCII.GetBytes("TFD1 package ");
        if (line.Length == prefix.Length + 64 && line[..prefix.Length].SequenceEqual(prefix))
        {
            var digest = line[prefix.Length..].ToArray();
            if (digest.All(value => (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))) return (DescriptorRequestKind.Package, Encoding.ASCII.GetString(digest));
        }

        return (DescriptorRequestKind.Malformed, null);
    }

    /// <summary>Join content as a player writes it.</summary>
    private static byte[] Encoded(FuzzMutator fuzz) =>
        JoinContent.Encode(fuzz.Chance(50) ? fuzz.Bytes(32) : null, fuzz.Bytes(fuzz.Chance(5) ? fuzz.Random.Next(JoinContent.MaximumContentBytes + 1) : fuzz.Random.Next(64)));

    /// <summary>
    /// Join content assembled field by field, as a hostile player might write it. The length field is the body's own
    /// length, in its one minimal spelling or padded with a needless zero byte, or that length plus a multiple of 2^32,
    /// which a reader that keeps the length in 32 bits cannot tell from the length itself.
    /// </summary>
    private static byte[] Assembled(FuzzMutator fuzz)
    {
        var bytes = new List<byte> { (byte)(fuzz.Chance(90) ? 1 : fuzz.Random.Next(4)), (byte)(fuzz.Chance(90) ? fuzz.Random.Next(2) : fuzz.Random.Next(256)) };
        if ((bytes[1] & 1) != 0 || fuzz.Chance(5)) bytes.AddRange(fuzz.Bytes(fuzz.Chance(90) ? 32 : fuzz.Random.Next(40)));
        var body = fuzz.Bytes(fuzz.Random.Next(64));
        var roll = fuzz.Random.Next(10);
        var length = fuzz.Varint(roll < 8 ? (ulong)body.Length : (ulong)body.Length + ((ulong)fuzz.Random.Next(1, 8) << 32), minimal: true);
        if (roll == 7)
        {
            length[^1] |= 0x80;
            length = length.Append((byte)0).ToArray();
        }

        bytes.AddRange(length);
        bytes.AddRange(body);
        return bytes.ToArray();
    }
}
