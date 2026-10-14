extern alias provider;

using System.Text;
using System.Text.Json;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Seeded fuzzing of the formats the game shares with the launcher and the control plane. For each, nothing a peer sends
/// throws, and whatever is accepted has exactly one spelling: writing back what was read gives the same bytes.
/// </summary>
internal static class WireFormatFuzzTests
{
    private const int MaximumJson = 64 * 1024;

    internal static void CanonicalJsonAcceptsExactlyWhatItWrites()
    {
        var fuzz = new FuzzMutator(150);
        var corpus = new List<byte[]>();
        foreach (var fixture in new[] { "descriptor/cases.json", "lan/cases.json", "links/cases.json" })
        {
            corpus.Add(Encoding.UTF8.GetBytes(NetworkFixtures.Load(fixture).GetRawText()));
        }

        var accepted = 0;
        using var run = FuzzLedger.Start("canonical JSON");
        for (var round = 0; round < FuzzMutator.Iterations(10_000); round++, run.Inputs += 2)
        {
            var written = CanonicalJson.Serialize(RandomNode(fuzz, 0));
            That(CanonicalJson.TryParse(written, MaximumJson, out var read, out var error) && CanonicalJson.Serialize(read).SequenceEqual(written),
                "what the writer writes, the reader accepts and writes back unchanged (" + error + ")");
            if (corpus.Count < 512) corpus.Add(written);

            var candidate = fuzz.Chance(10) ? fuzz.Bytes(fuzz.Random.Next(64)) : fuzz.Mutate(fuzz.Pick(corpus), MaximumJson);
            var ok = CanonicalJson.TryParse(candidate, MaximumJson, out var node, out _);
            if (ok)
            {
                accepted++;
                That(CanonicalJson.Serialize(node).SequenceEqual(candidate), "an accepted document is already canonical");
            }

            That(ok == Differential(candidate), "the reader agrees with an independent JSON reader on " + Encoding.UTF8.GetString(candidate));
        }

        That(accepted > 0, "the mutations reach accepted documents too");
        // The root is at depth 0, as in the launcher's and the control plane's readers, so 17 nested arrays reach depth 16.
        var deepest = Encoding.ASCII.GetBytes(new string('[', 17) + new string(']', 17));
        var deeper = Encoding.ASCII.GetBytes(new string('[', 18) + new string(']', 18));
        That(CanonicalJson.TryParse(deepest, MaximumJson, out _, out _) && !CanonicalJson.TryParse(deeper, MaximumJson, out _, out _),
            "a value sixteen levels below the root is the deepest a document holds");
    }

    internal static void SignedDescriptorsVerifyOnlyTheirOwnBytes()
    {
        var fuzz = new FuzzMutator(151);
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        var valid = fixture.GetProperty("valid").EnumerateArray().Select(item => NetworkFixtures.Utf8(item.Text("signedBytes"))).ToList();
        var ids = fixture.GetProperty("valid").EnumerateArray().Select(item => item.Text("serverId")).ToList();
        var corpus = valid.Concat(fixture.GetProperty("invalid").EnumerateArray().Select(item =>
            item.TryGetProperty("bytesHex", out var hex) ? NetworkFixtures.Hex(hex.GetString()!) : NetworkFixtures.Utf8(item.Text("bytes")))).ToList();
        using var run = FuzzLedger.Start("signed descriptors");
        for (var round = 0; round < FuzzMutator.Iterations(5_000); round++, run.Inputs++)
        {
            var candidate = fuzz.Chance(15)
                ? fuzz.Splice(fuzz.Pick(corpus), fuzz.Pick(corpus), SignedServerDescriptor.MaximumBytes)
                : fuzz.Mutate(fuzz.Pick(corpus), SignedServerDescriptor.MaximumBytes);
            var expected = fuzz.Chance(50) ? null : fuzz.Pick(ids);
            if (!SignedServerDescriptor.TryVerify(candidate, expected, out var verified, out _)) continue;
            That(valid.Any(bytes => bytes.SequenceEqual(candidate)), "only bytes a host signed verify: " + Encoding.UTF8.GetString(candidate));
            That(verified!.Bytes.SequenceEqual(candidate) && (expected == null || verified.Descriptor.ServerId == expected), "and they verify as themselves");
        }
    }

    internal static void LinksInviteCodesAndHostsHaveOneSpelling()
    {
        var fuzz = new FuzzMutator(152);
        var fixture = NetworkFixtures.Load("links/cases.json");
        var corpus = fixture.GetProperty("valid").EnumerateArray().Select(item => item.Text("input"))
            .Concat(fixture.GetProperty("invalid").EnumerateArray().Select(item => item.Text("input")))
            .Concat(fixture.GetProperty("inviteCodes").EnumerateArray().Select(item => item.Text("input")))
            .Concat(new[] { "play.example.org", "192.168.0.10", "fe80::1", "::ffff:10.0.0.1", "2001:db8::42" }).ToList();
        var alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~:/?#%@!$&'()*+,;= " +
            Characters(0x09, 0xE9, 0x17F, 0x130, 0x212A, 0xFF21, 0x00, 0x7F);
        var links = 0;
        using var run = FuzzLedger.Start("links, invite codes and hosts");
        for (var round = 0; round < FuzzMutator.Iterations(20_000); round++, run.Inputs++)
        {
            var text = fuzz.MutateText(fuzz.Pick(corpus), alphabet, SessionLink.MaximumLength + 16);
            if (SessionLink.TryParse(text, out var link))
            {
                links++;
                var canonical = link!.ToString();
                That(canonical.Length <= SessionLink.MaximumLength && canonical.All(character => character > ' ' && character <= '~'),
                    "a link's canonical form is printable ASCII within the bound: " + canonical);
                That(SessionLink.TryParse(canonical, out var again) && again!.ToString() == canonical && again.Kind == link.Kind &&
                     again.InviteCode == link.InviteCode && again.ServerId == link.ServerId && again.Host == link.Host && again.Port == link.Port,
                    "and reads back as the same link: " + text);
            }

            if (InviteCode.TryNormalize(text, out var code))
            {
                That(InviteCode.IsCanonical(code) && InviteCode.TryNormalize(InviteCode.Display(code), out var shown) && shown == code,
                    "an invite code has one canonical spelling, which its display form reads back as: " + text);
            }

            if (SessionLink.TryNormalizeHost(text, out var host))
            {
                That(host.All(character => character > ' ' && character <= '~') && SessionLink.TryNormalizeHost(host, out var twice) && twice == host,
                    "a normalized host is printable ASCII and normalizing it again changes nothing: " + text);
            }

            if (corpus.Count < 1_024 && fuzz.Chance(5)) corpus.Add(text);
        }

        That(links > 0, "the mutations reach links that parse");
    }

    internal static void LanBeaconsAndSessionInfoReadBackExactly()
    {
        var fuzz = new FuzzMutator(153);
        var fixture = NetworkFixtures.Load("lan/cases.json");
        var beacons = fixture.GetProperty("beacons").GetProperty("valid").EnumerateArray().Select(item => NetworkFixtures.Hex(item.Text("hex")))
            .Concat(fixture.GetProperty("beacons").GetProperty("invalid").EnumerateArray().Select(item => NetworkFixtures.Hex(item.Text("hex")))).ToList();
        var infos = fixture.GetProperty("sessionInfo").GetProperty("valid").EnumerateArray().Select(item => NetworkFixtures.Utf8(item.Text("canonical")))
            .Concat(fixture.GetProperty("sessionInfo").GetProperty("invalid").EnumerateArray().Select(item => NetworkFixtures.Utf8(item.Text("text")))).ToList();
        using var run = FuzzLedger.Start("LAN beacons and session info");
        for (var round = 0; round < FuzzMutator.Iterations(10_000); round++, run.Inputs += 2)
        {
            var beacon = fuzz.Chance(10) ? fuzz.Bytes(fuzz.Random.Next(72)) : fuzz.Mutate(fuzz.Pick(beacons), 72);
            var padding = fuzz.Random.Next(4);
            var framed = fuzz.Bytes(padding).Concat(beacon).Concat(fuzz.Bytes(fuzz.Random.Next(4))).ToArray();
            var expected = beacon.Length == LanBeacon.Size && beacon[0] == LanBeacon.PacketType && beacon[1] == LanBeacon.Version && (beacon[34] | beacon[35]) != 0;
            var ok = LanBeacon.TryDecode(framed, padding, beacon.Length, out var decoded);
            That(ok == expected, "a beacon is exactly 36 bytes of the right type and version with a port: " + Convert.ToHexString(beacon));
            if (ok) That(decoded.Encode().SequenceEqual(beacon), "and an accepted beacon writes back as itself");

            var candidate = fuzz.Chance(5) ? RandomInfo(fuzz) : fuzz.Mutate(fuzz.Pick(infos), SessionInfo.MaximumBytes + 32);
            if (SessionInfo.TryDecode(candidate, out var info, out _))
            {
                That(info!.Encode().SequenceEqual(candidate), "accepted session info writes back as itself: " + Encoding.UTF8.GetString(candidate));
                if (infos.Count < 512) infos.Add(candidate);
            }
        }
    }

    private static byte[] RandomInfo(FuzzMutator fuzz)
    {
        var maxPlayers = fuzz.Random.Next(2, 17);
        return new SessionInfo(Hex(fuzz, 32), Hex(fuzz, 32), DisplayText(fuzz, 48), "2545", "0.2.0", "1.0.0", fuzz.Chance(50) ? null : DisplayText(fuzz, 48),
            fuzz.Chance(50) ? null : DisplayText(fuzz, 48), fuzz.Random.Next(maxPlayers + 1), maxPlayers, fuzz.Chance(50), fuzz.Chance(50)).Encode();
    }

    /// <summary>Agrees with an independent reader: valid JSON, unique keys, letters-and-digits keys, safe integers only, no whitespace, sorted keys.</summary>
    private static bool Differential(byte[] candidate)
    {
        if (candidate.Length == 0) return false;
        try
        {
            using var document = JsonDocument.Parse(candidate, new JsonDocumentOptions { MaxDepth = 64 });
            if (!Canonical(document.RootElement, 0)) return false;
            return Rewrite(document.RootElement).SequenceEqual(candidate);
        }
        catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException || exception is ArgumentException)
        {
            return false;
        }
    }

    private static bool Canonical(JsonElement element, int depth)
    {
        if (depth > CanonicalJson.MaximumDepth) return false;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var keys = new List<string>();
                foreach (var property in element.EnumerateObject())
                {
                    if (!CanonicalJson.IsKey(property.Name) || keys.Contains(property.Name) || !Canonical(property.Value, depth + 1)) return false;
                    keys.Add(property.Name);
                }

                return keys.SequenceEqual(keys.OrderBy(key => key, StringComparer.Ordinal));
            case JsonValueKind.Array:
                return element.EnumerateArray().All(item => Canonical(item, depth + 1));
            case JsonValueKind.Number:
                return element.TryGetInt64(out var value) && Math.Abs(value) <= CanonicalJson.MaximumSafeInteger;
            case JsonValueKind.String:
                var text = element.GetString()!;
                for (var index = 0; index < text.Length; index++)
                {
                    if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) index++;
                    else if (char.IsSurrogate(text[index])) return false;
                }

                return true;
            default:
                return true;
        }
    }

    /// <summary>Writes the canonical form of a document an independent reader parsed, without the code under test.</summary>
    private static byte[] Rewrite(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(builder, element);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void Write(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!first) builder.Append(',');
                    first = false;
                    Quote(builder, property.Name);
                    builder.Append(':');
                    Write(builder, property.Value);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[').AppendJoin(',', element.EnumerateArray().Select(item =>
                {
                    var nested = new StringBuilder();
                    Write(nested, item);
                    return nested.ToString();
                })).Append(']');
                break;
            case JsonValueKind.String:
                Quote(builder, element.GetString()!);
                break;
            case JsonValueKind.Number:
                builder.Append(element.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                builder.Append(element.GetRawText());
                break;
        }
    }

    private static void Quote(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var character in text)
        {
            if (character == '"' || character == '\\') builder.Append('\\').Append(character);
            else if (character == '\n') builder.Append("\\n");
            else if (character == '\r') builder.Append("\\r");
            else if (character == '\t') builder.Append("\\t");
            else if (character == '\b') builder.Append("\\b");
            else if (character == '\f') builder.Append("\\f");
            else if (character < 0x20) builder.Append("\\u00").Append(((int)character).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            else builder.Append(character);
        }

        builder.Append('"');
    }

    private static JsonNode RandomNode(FuzzMutator fuzz, int depth)
    {
        var kind = depth >= CanonicalJson.MaximumDepth ? fuzz.Random.Next(3, 7) : fuzz.Random.Next(depth < 3 ? 0 : 2, 7);
        switch (kind)
        {
            case 0:
            case 1:
                var node = new JsonObjectNode();
                for (var count = fuzz.Random.Next(4); count > 0; count--)
                {
                    var key = Key(fuzz);
                    if (!node.TryGet(key, out _)) node.Add(key, RandomNode(fuzz, depth + 1));
                }

                return node;
            case 2:
                return new JsonArrayNode(Enumerable.Range(0, fuzz.Random.Next(4)).Select(_ => RandomNode(fuzz, depth + 1)).ToList());
            case 3:
                return new JsonStringNode(Text(fuzz));
            case 4:
                var magnitude = fuzz.Random.Next(4) switch
                {
                    0 => fuzz.Random.Next(10),
                    1 => CanonicalJson.MaximumSafeInteger,
                    2 => fuzz.Random.NextInt64(CanonicalJson.MaximumSafeInteger),
                    _ => fuzz.Random.Next(1_000_000)
                };
                return new JsonIntegerNode(fuzz.Chance(30) ? -magnitude : magnitude);
            case 5:
                return fuzz.Chance(50) ? JsonBooleanNode.True : JsonBooleanNode.False;
            default:
                return JsonNullNode.Instance;
        }
    }

    private static string Key(FuzzMutator fuzz)
    {
        const string Characters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, fuzz.Random.Next(1, fuzz.Chance(5) ? 65 : 9)).Select(_ => Characters[fuzz.Random.Next(Characters.Length)]).ToArray());
    }

    private static string Text(FuzzMutator fuzz)
    {
        var pieces = new[] { "a", "Z", "0", " ", "\"", "\\", "/", "\n", "\t" }
            .Concat(new[] { 0x01, 0x1F, 0x7F, 0xE9, 0x20AC, 0x1F600, 0xFFFF, 0x2028 }.Select(char.ConvertFromUtf32)).ToArray();
        return string.Concat(Enumerable.Range(0, fuzz.Random.Next(8)).Select(_ => pieces[fuzz.Random.Next(pieces.Length)]));
    }

    /// <summary>Builds a string from code points, so the source stays ASCII.</summary>
    private static string Characters(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    internal static string DisplayText(FuzzMutator fuzz, int maximum)
    {
        var pieces = new[] { "a", "Z", "0", " ", "-", "'" }.Concat(new[] { 0xE9, 0x4E16, 0x1F600 }.Select(char.ConvertFromUtf32)).ToArray();
        string text;
        do
        {
            text = string.Concat(Enumerable.Range(0, fuzz.Random.Next(1, 10)).Select(_ => pieces[fuzz.Random.Next(pieces.Length)])).Trim();
        }
        while (!WireText.IsDisplayText(text, maximum));

        return text;
    }

    internal static string Hex(FuzzMutator fuzz, int bytes) => WireText.ToHex(fuzz.Bytes(bytes));
}
