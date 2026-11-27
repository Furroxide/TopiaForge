extern alias provider;

using System.Text;
using System.Text.Json;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Canonical JSON and signed server descriptors, against the shared fixtures.</summary>
internal static class NetworkDescriptorTests
{
    internal static void SignedDescriptorsMatchTheSharedFixtures()
    {
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        var keys = fixture.GetProperty("keys").EnumerateArray().ToDictionary(key => key.Text("id"));
        That(fixture.Text("context") == "TopiaForge server descriptor v1\n", "the fixtures sign under the same context label");
        var count = 0;
        foreach (var testCase in fixture.GetProperty("valid").EnumerateArray())
        {
            var id = testCase.Text("id");
            var key = keys[testCase.Text("key")];
            var descriptor = Build(testCase.GetProperty("descriptor"));
            That(descriptor.ServerId == key.Text("serverId") && descriptor.ServerId == testCase.Text("serverId"), id + ": the server id is the signing key's hash");
            That(descriptor.PackageDigest == testCase.Text("packageDigest"), id + ": the package digest matches");
            That(Encoding.UTF8.GetString(CanonicalJson.Serialize(descriptor.ToJson())) == testCase.Text("canonicalDescriptor"), id + ": the canonical descriptor matches byte for byte");
            var signed = SignedServerDescriptor.Sign(descriptor, NetworkFixtures.Hex(key.Text("signingSeed")));
            That(Encoding.UTF8.GetString(signed.Bytes) == testCase.Text("signedBytes"), id + ": signing reproduces the served bytes");
            That(WireText.ToBase64Url(signed.Signature) == testCase.Text("signature") && signed.Sha256 == testCase.Text("descriptorSha256"),
                id + ": the signature and the descriptor hash match");

            var bytes = NetworkFixtures.Utf8(testCase.Text("signedBytes"));
            That(SignedServerDescriptor.TryVerify(bytes, testCase.Text("serverId"), out var verified, out var error), id + ": verifies: " + error);
            That(verified!.Descriptor.Name == descriptor.Name && verified.Descriptor.Packages.Count == descriptor.Packages.Count &&
                 verified.Descriptor.TransportKey.SequenceEqual(descriptor.TransportKey) && verified.Sha256 == testCase.Text("descriptorSha256"),
                id + ": verification yields the same descriptor");
            That(SignedServerDescriptor.TryVerify(bytes, null, out _, out _), id + ": verifies without an expected server id");
            count++;
        }

        That(count >= 3, "the valid cases ran");
        var refused = 0;
        foreach (var testCase in fixture.GetProperty("invalid").EnumerateArray())
        {
            var bytes = testCase.TryGetProperty("bytesHex", out var hex)
                ? NetworkFixtures.Hex(hex.GetString()!)
                : NetworkFixtures.Utf8(testCase.Text("bytes"));
            That(!SignedServerDescriptor.TryVerify(bytes, testCase.OptionalText("expectedServerId"), out var verified, out var error) && verified == null && error.Length > 0,
                testCase.Text("id") + " is refused: " + testCase.Text("about"));
            refused++;
        }

        That(refused >= 30, "every invalid case ran (" + refused + ")");
    }

    internal static void DescriptorsRefuseWhatTheyCannotRepresent()
    {
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        var key = fixture.GetProperty("keys")[0];
        var seed = NetworkFixtures.Hex(key.Text("signingSeed"));
        var baseline = Build(fixture.GetProperty("valid")[0].GetProperty("descriptor"));
        Throws<ArgumentException>(() => SignedServerDescriptor.Sign(baseline, NetworkFixtures.Hex(fixture.GetProperty("keys")[1].Text("signingSeed"))),
            "a descriptor is signed only by the key it names");
        Throws<ArgumentException>(() => Rebuild(baseline, packages: new[] { Package("b.mod"), Package("a.mod") }), "packages must be sorted");
        Throws<ArgumentException>(() => Rebuild(baseline, packages: new[] { Package("a.mod"), Package("a.mod") }), "packages appear once");
        Throws<ArgumentException>(() => Rebuild(baseline, name: "line\nbreak"), "names hold no control characters");
        Throws<ArgumentException>(() => Rebuild(baseline, name: "\ud800 lone"), "names hold no unpaired surrogates");
        Throws<ArgumentException>(() => Rebuild(baseline, gamemode: new string('g', 49)), "target names are bounded");
        Throws<ArgumentException>(() => new DescriptorPackage("a.mod", "1.0", new string('0', 64), DescriptorPackageRole.Implicit), "versions are semantic");
        Throws<ArgumentException>(() => new DescriptorPackage("a", "1.0.0", new string('0', 64), DescriptorPackageRole.Implicit), "ids are manifest mod ids");
        Throws<ArgumentException>(() => new DescriptorPackage("a.mod", "1.0.0", new string('A', 64), DescriptorPackageRole.Implicit), "digests are lowercase");

        // The largest list a descriptor may hold still signs within the size bound, and one more is refused.
        var most = Enumerable.Range(0, ServerDescriptor.MaximumPackages).Select(index => Package("mod." + index.ToString("D4"))).ToArray();
        var full = SignedServerDescriptor.Sign(Rebuild(baseline, packages: most), seed);
        That(full.Bytes.Length <= SignedServerDescriptor.MaximumBytes &&
             SignedServerDescriptor.TryVerify(full.Bytes, baseline.ServerId, out _, out _), "512 packages fit within 256 KiB (" + full.Bytes.Length + " bytes)");
        Throws<ArgumentException>(() => Rebuild(baseline, packages: most.Append(Package("mod.9999")).ToArray()), "513 packages do not");
        var oversized = new byte[SignedServerDescriptor.MaximumBytes + 1];
        Array.Fill(oversized, (byte)' ');
        That(!SignedServerDescriptor.TryVerify(oversized, null, out _, out var oversizedError) && oversizedError.Contains("larger"), "an oversized document is refused before parsing");
    }

    internal static void SignedValuesCannotBeChangedFromOutside()
    {
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        var seed = NetworkFixtures.Hex(fixture.GetProperty("keys")[0].Text("signingSeed"));
        var baseline = Build(fixture.GetProperty("valid")[0].GetProperty("descriptor"));
        var packages = baseline.Packages.ToList();
        var descriptor = Rebuild(baseline, packages: packages);
        var digest = descriptor.PackageDigest;
        packages.Add(Package("zzz.added.later"));
        packages.RemoveAt(0);
        That(descriptor.Packages.Count == baseline.Packages.Count && descriptor.Packages[0].Id == baseline.Packages[0].Id &&
             CanonicalJson.Serialize(descriptor.ToJson()).SequenceEqual(CanonicalJson.Serialize(baseline.ToJson())) && descriptor.PackageDigest == digest,
            "changing the caller's list after construction changes nothing the descriptor signs");

        var key = descriptor.TransportKey;
        key[0] ^= 0xFF;
        That(!descriptor.TransportKey.SequenceEqual(key) && descriptor.SigningKey.SequenceEqual(baseline.SigningKey), "returned keys are copies");

        var signed = SignedServerDescriptor.Sign(descriptor, seed);
        var served = signed.Bytes;
        served[0] = (byte)'[';
        var signature = signed.Signature;
        signature[0] ^= 0xFF;
        That(signed.Bytes[0] == (byte)'{' && WireText.ToHex(WireText.Sha256(signed.Bytes)) == signed.Sha256 && signed.Length == served.Length &&
             SignedServerDescriptor.TryVerify(signed.Bytes, descriptor.ServerId, out _, out _) && !signed.Signature.SequenceEqual(signature),
            "the served bytes and the signature are copies, so the pinned hash always matches what is served");
    }

    internal static void CanonicalJsonAcceptsOnlyOneSpelling()
    {
        var value = new JsonObjectNode()
            .Add("b", "tab\tquote\"slash/backslash\\bell\u0007del\u007fé😀")
            .Add("a", -9_007_199_254_740_991)
            .Add("c", new JsonArrayNode(new JsonNode[] { JsonBooleanNode.True, JsonNullNode.Instance, new JsonIntegerNode(0) }));
        var bytes = CanonicalJson.Serialize(value);
        var expected = "{\"a\":-9007199254740991,\"b\":\"tab\\tquote\\\"slash/backslash\\\\bell\\u0007del\u007fé😀\",\"c\":[true,null,0]}";
        That(Encoding.UTF8.GetString(bytes) == expected, "keys sort, only control characters escape, and the rest stays UTF-8");
        That(CanonicalJson.TryParse(bytes, 1024, out var parsed, out _) && CanonicalJson.Serialize(parsed).SequenceEqual(bytes), "a canonical document parses back to itself");
        var independent = JsonSerializer.Serialize(JsonDocument.Parse(bytes).RootElement);
        That(JsonDocument.Parse(independent).RootElement.GetProperty("b").GetString() == "tab\tquote\"slash/backslash\\bell\u0007del\u007fé😀",
            "a general JSON reader reads the same string");

        foreach (var (text, why) in new[]
                 {
                     ("{\"a\":1 }", "whitespace"),
                     ("{\"b\":1,\"a\":2}", "unsorted keys"),
                     ("{\"a\":1,\"a\":1}", "a repeated key"),
                     ("{\"a-b\":1}", "a key outside letters and digits"),
                     ("{\"a\":1.5}", "a fraction"),
                     ("{\"a\":1e3}", "an exponent"),
                     ("{\"a\":01}", "a leading zero"),
                     ("{\"a\":-0}", "negative zero"),
                     ("{\"a\":9007199254740992}", "an integer past 2^53 - 1"),
                     ("{\"a\":\"\\u0041\"}", "an escape for a printable character"),
                     ("{\"a\":\"\\u001F\"}", "an uppercase escape"),
                     ("{\"a\":\"\\/\"}", "an escaped slash"),
                     ("{\"a\":\"\\ud83d\"}", "an unpaired surrogate"),
                     ("{\"a\":\"\u0001\"}", "a raw control character"),
                     ("{\"a\":true} ", "trailing whitespace"),
                     ("\ufeff{\"a\":1}", "a byte-order mark"),
                     ("[1,]", "a trailing comma"),
                     ("{\"a\":tru}", "a broken literal"),
                 })
        {
            That(!CanonicalJson.TryParse(Encoding.UTF8.GetBytes(text), 1024, out _, out _), "refuses " + why);
        }

        var deep = new StringBuilder();
        for (var depth = 0; depth <= CanonicalJson.MaximumDepth + 1; depth++) deep.Append('[');
        for (var depth = 0; depth <= CanonicalJson.MaximumDepth + 1; depth++) deep.Append(']');
        That(!CanonicalJson.TryParse(Encoding.UTF8.GetBytes(deep.ToString()), 1024, out _, out _), "refuses nesting past the depth limit");
        That(!CanonicalJson.TryParse(new byte[] { 0x22, 0xC3, 0x28, 0x22 }, 1024, out _, out _), "refuses malformed UTF-8");
        Throws<ArgumentException>(() => new JsonObjectNode().Add("bad key", 1L), "the writer refuses keys the reader would");
        Throws<ArgumentOutOfRangeException>(() => new JsonIntegerNode(9_007_199_254_740_992), "the writer refuses integers the reader would");
    }

    internal static void EncodingsHaveOneSpellingPerValue()
    {
        var bytes = Enumerable.Range(0, 32).Select(index => (byte)(index * 7)).ToArray();
        var text = WireText.ToBase64Url(bytes);
        That(text == Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'), "base64url matches the standard alphabet, unpadded");
        That(WireText.TryParseBase64Url(text, 32, out var parsed) && parsed.SequenceEqual(bytes), "base64url round-trips");
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        foreach (var size in new[] { 1, 2, 31, 32, 64 })
        {
            var value = WireText.ToBase64Url(Enumerable.Repeat((byte)0xA5, size).ToArray());
            var unusedBits = (6 - (size * 8 % 6)) % 6;
            if (unusedBits == 0) continue;
            var last = Alphabet.IndexOf(value[^1]);
            var noisy = value[..^1] + Alphabet[last | 1];
            That(!WireText.TryParseBase64Url(noisy, size, out _), "a set unused bit is refused for " + size + " bytes");
        }

        That(!WireText.TryParseBase64Url(text + "=", 32, out _) && !WireText.TryParseBase64Url(text[..^1], 32, out _) &&
             !WireText.TryParseBase64Url(text.Replace(text[0], '+'), 32, out _), "padding, truncation and the standard alphabet are refused");
        That(WireText.ToHex(bytes) == Convert.ToHexString(bytes).ToLowerInvariant() && WireText.IsHex(WireText.ToHex(bytes), 32), "hex is lowercase");
        That(!WireText.IsHex(Convert.ToHexString(bytes), 32) && !WireText.IsHex(WireText.ToHex(bytes)[..^2], 32), "uppercase or short hex is refused");
    }

    internal static void VersionsAreCheckedInLinearTime()
    {
        // Pre-release identifiers made only of hyphens once sent the pattern into exponential backtracking: at the
        // 64-character bound one check took tens of milliseconds, and a descriptor holds 514 versions.
        var pathological = "0.0.0-0." + string.Concat(Enumerable.Repeat("--.", 18)) + "!";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < 514; index++) That(!ServerDescriptor.IsSemanticVersion(pathological), "a malformed version is refused");
        That(watch.ElapsedMilliseconds < 2_000, "a descriptor's worth of hostile versions is checked quickly (" + watch.ElapsedMilliseconds + " ms)");

        foreach (var valid in new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-0.3.7", "1.0.0-x.7.z.92", "1.0.0-x-y-z.--", "1.0.0-1a", "1.0.0-a1+b.2", "1.0.0--" })
        {
            That(ServerDescriptor.IsSemanticVersion(valid), valid + " is a semantic version");
        }

        foreach (var invalid in new[] { "1.0.0-01", "1.0.0-", "1.0.0-a..b", "1.0.0+", "01.0.0", "1.0.0-a_b" })
        {
            That(!ServerDescriptor.IsSemanticVersion(invalid), invalid + " is not");
        }
    }

    internal static void DescriptorMembersMustAgree()
    {
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        var text = fixture.GetProperty("valid")[0].Text("canonicalDescriptor");
        That(CanonicalJson.TryParse(NetworkFixtures.Utf8(text), SignedServerDescriptor.MaximumBytes, out var node, out _), "the fixture descriptor parses");
        That(ServerDescriptor.FromJson(node).ServerId == fixture.GetProperty("valid")[0].Text("serverId"), "an agreeing descriptor reads");
        var otherServer = fixture.GetProperty("keys")[1].Text("serverId");
        var digest = fixture.GetProperty("valid")[0].Text("packageDigest");
        foreach (var (changed, expected) in new[]
                 {
                     (text.Replace(fixture.GetProperty("valid")[0].Text("serverId"), otherServer), "server id"),
                     (text.Replace(digest, new string('0', 64)), "package digest"),
                 })
        {
            That(CanonicalJson.TryParse(NetworkFixtures.Utf8(changed), SignedServerDescriptor.MaximumBytes, out var mismatched, out _), "the altered descriptor is still canonical");
            try
            {
                ServerDescriptor.FromJson(mismatched);
                That(false, "a descriptor whose " + expected + " disagrees is refused");
            }
            catch (FormatException exception)
            {
                That(exception.Message.Contains(expected), "the refusal names the " + expected + ": " + exception.Message);
            }
        }
    }

    internal static ServerDescriptor Build(JsonElement value) =>
        new(
            Base64Url(value.Text("signingKey")),
            Base64Url(value.Text("transportKey")),
            value.GetProperty("issuedAt").GetInt64(),
            value.Text("name"),
            value.Text("gameBuild"),
            value.Text("runtimeVersion"),
            value.Text("protocolVersion"),
            value.GetProperty("maxPlayers").GetInt32(),
            value.GetProperty("passwordRequired").GetBoolean(),
            value.GetProperty("voice").GetBoolean(),
            value.Text("visibility") switch { "public" => ServerVisibility.Public, "invite" => ServerVisibility.Invite, _ => ServerVisibility.Lan },
            value.GetProperty("hostServesPackages").GetBoolean(),
            value.OptionalText("gamemode"),
            value.OptionalText("world"),
            value.GetProperty("packages").EnumerateArray().Select(item => new DescriptorPackage(
                item.Text("id"),
                item.Text("version"),
                item.Text("sha256"),
                item.Text("role") switch
                {
                    "session-required" => DescriptorPackageRole.SessionRequired,
                    "session-optional" => DescriptorPackageRole.SessionOptional,
                    _ => DescriptorPackageRole.Implicit
                })).ToList());

    private static ServerDescriptor Rebuild(ServerDescriptor source, string? name = null, string? gamemode = null, IReadOnlyList<DescriptorPackage>? packages = null) =>
        new(source.SigningKey, source.TransportKey, source.IssuedAt, name ?? source.Name, source.GameBuild, source.RuntimeVersion,
            source.ProtocolVersion, source.MaxPlayers, source.PasswordRequired, source.Voice, source.Visibility, source.HostServesPackages,
            gamemode ?? source.Gamemode, source.World, packages ?? source.Packages);

    private static DescriptorPackage Package(string id) =>
        new(id, "1.0.0", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant(), DescriptorPackageRole.Implicit);

    private static byte[] Base64Url(string text)
    {
        That(WireText.TryParseBase64Url(text, 32, out var bytes), "a fixture key is canonical base64url");
        return bytes;
    }
}
