extern alias provider;

using System.Globalization;
using System.Text;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Session links, invite codes, LAN beacons and session info, against the shared fixtures.</summary>
internal static class NetworkLinkTests
{
    internal static void LinksMatchTheSharedFixtures()
    {
        var fixture = NetworkFixtures.Load("links/cases.json");
        That(fixture.Text("alphabet") == InviteCode.Alphabet, "the invite alphabet is Crockford base32");
        foreach (var testCase in fixture.GetProperty("valid").EnumerateArray())
        {
            var input = testCase.Text("input");
            That(SessionLink.TryParse(input, out var link), "parses " + input);
            var kind = testCase.Text("kind") switch { "invite" => SessionLinkKind.Invite, "server" => SessionLinkKind.Server, _ => SessionLinkKind.Direct };
            That(link!.Kind == kind && link.InviteCode == testCase.OptionalText("inviteCode") && link.ServerId == testCase.OptionalText("serverId") &&
                 link.Host == testCase.OptionalText("host") && link.Port == (testCase.TryGetProperty("port", out var port) ? port.GetInt32() : 0),
                input + " yields its fields");
            That(link.Canonical == testCase.Text("canonical"), input + " formats as " + testCase.Text("canonical") + ", not " + link.Canonical);
            That(SessionLink.TryParse(link.Canonical, out var again) && again!.Canonical == link.Canonical, input + " round-trips");
        }

        foreach (var testCase in fixture.GetProperty("invalid").EnumerateArray())
        {
            That(!SessionLink.TryParse(testCase.Text("input"), out var link) && link == null, "refuses " + testCase.Text("about"));
        }

        foreach (var testCase in fixture.GetProperty("inviteCodes").EnumerateArray())
        {
            var expected = testCase.OptionalText("canonical");
            var normalized = InviteCode.TryNormalize(testCase.Text("input"), out var code);
            That(normalized == (expected != null) && (expected == null || (code == expected && InviteCode.Display(code) == testCase.Text("display"))),
                "normalizes the invite code " + testCase.Text("input"));
        }

        That(SessionLink.TryParse(null, out _) == false && SessionLink.TryParse("", out _) == false, "nothing is not a link");
        Throws<ArgumentException>(() => InviteCode.Display("abcde12345"), "only canonical codes are displayed");
        Throws<ArgumentOutOfRangeException>(() => SessionLink.ForDirect(new string('a', 64), "example.org", 0), "direct links need a port");
    }

    /// <summary>
    /// A link prints its kind alone, in a join target's words, however it is turned into text: no spelling of an invite's
    /// code or of a direct link's host or port reaches it, in any case. <see cref="SessionLink.Canonical"/> still spells the
    /// link, and reads back as the same link, for every kind.
    /// </summary>
    internal static void ALinkKeepsItsCodeAndAddressOutOfToString()
    {
        const string prefix = "://join/";
        var serverId = new string('c', 64);
        var cases = new List<(SessionLink Link, string Typed)>();
        foreach (var testCase in NetworkFixtures.Load("links/cases.json").GetProperty("valid").EnumerateArray())
        {
            var input = testCase.Text("input");
            That(SessionLink.TryParse(input, out var parsed), "parses " + input);
            var segments = input.Substring(input.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length).Split('/');
            cases.Add((parsed!, parsed!.Kind == SessionLinkKind.Direct ? segments[2] : segments[segments.Length - 1]));
        }

        foreach (var typed in new[] { "RJ9JM-V1V09", "abcde-12345", "f80bh rs9yw" }) cases.Add((SessionLink.ForInvite(typed), typed));
        foreach (var (host, port) in new[] { ("203.0.113.5", 27260), ("2001:DB8::7", 40123), ("Play.Example.ORG", 27262) })
        {
            cases.Add((SessionLink.ForDirect(serverId, host, port), host));
        }

        cases.Add((SessionLink.ForServer(serverId), serverId));
        var kinds = new HashSet<SessionLinkKind>();
        foreach (var (link, typed) in cases)
        {
            kinds.Add(link.Kind);
            var texts = new[] { link.ToString(), $"{link}", string.Format(CultureInfo.InvariantCulture, "{0}", link), string.Empty + link };
            foreach (var secret in Secrets(link, typed))
            {
                That(texts.All(text => text.IndexOf(secret, StringComparison.OrdinalIgnoreCase) < 0),
                    "a link holds no spelling of its code, host or port in its text ('" + secret + "' in '" + link + "')");
            }

            That(texts.All(text => text == Printed(link.Kind)), "every formatting form prints the link's kind alone, as a join target does");
            That(SessionLink.TryParse(link.Canonical, out var again) && again!.Kind == link.Kind && again.InviteCode == link.InviteCode &&
                 again.ServerId == link.ServerId && again.Host == link.Host && again.Port == link.Port && again.Canonical == link.Canonical,
                "the canonical form reads back as the same " + Printed(link.Kind) + " link");
        }

        That(kinds.Count == 3, "every kind of link is checked");
    }

    /// <summary>Gets the words a link of <paramref name="kind"/> prints: those of the join target it becomes.</summary>
    internal static string Printed(SessionLinkKind kind) =>
        kind == SessionLinkKind.Invite ? "invite" : kind == SessionLinkKind.Server ? "listed server" : "direct session";

    /// <summary>
    /// Gets every spelling of what a link must never print: an invite's code canonical, displayed, as typed and in its
    /// halves, and a direct link's host as read and as typed, its port, and the two joined. A server link names only its
    /// public server id, so it has none. Each is compared in any case.
    /// </summary>
    private static IEnumerable<string> Secrets(SessionLink link, string typed)
    {
        if (link.Kind == SessionLinkKind.Invite)
        {
            var code = link.InviteCode!;
            return new[] { code, InviteCode.Display(code), typed, code.Substring(0, 5), code.Substring(5) };
        }

        if (link.Kind != SessionLinkKind.Direct) return Array.Empty<string>();
        var port = link.Port.ToString(CultureInfo.InvariantCulture);
        return new[] { link.Host!, typed, port, link.Host + ":" + port, "[" + link.Host + "]:" + port };
    }

    internal static void IPv6AddressesFollowRfc5952()
    {
        foreach (var (input, expected) in new[]
                 {
                     ("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1"),
                     ("2001:db8:0:1:1:1:1:1", "2001:db8:0:1:1:1:1:1"),
                     ("2001:db8:0:0:1:0:0:1", "2001:db8::1:0:0:1"),
                     ("2001:0:0:1:0:0:0:1", "2001:0:0:1::1"),
                     ("0:0:0:0:0:0:0:0", "::"),
                     ("0:0:0:0:0:0:0:1", "::1"),
                     ("1:0:0:0:0:0:0:0", "1::"),
                     ("FE80::ABCD", "fe80::abcd"),
                     ("::ffff:192.0.2.1", "192.0.2.1"),
                 })
        {
            That(SessionLink.TryNormalizeHost(input, out var normalized) && normalized == expected, input + " normalizes to " + expected);
        }

        foreach (var input in new[] { "fe80::1%eth0", "[::1]", "1:2:3:4:5:6:7:8:9", "::g", "gggg::1" })
        {
            That(!SessionLink.TryNormalizeHost(input, out _), "refuses the host " + input);
        }
    }

    internal static void LanFormatsMatchTheSharedFixtures()
    {
        var fixture = NetworkFixtures.Load("lan/cases.json");
        foreach (var testCase in fixture.GetProperty("beacons").GetProperty("valid").EnumerateArray())
        {
            var beacon = new LanBeacon(testCase.Text("serverId"), testCase.GetProperty("gamePort").GetInt32());
            var bytes = NetworkFixtures.Hex(testCase.Text("hex"));
            That(beacon.Encode().SequenceEqual(bytes), "a beacon encodes to its fixture bytes");
            var padded = new byte[bytes.Length + 4];
            Buffer.BlockCopy(bytes, 0, padded, 2, bytes.Length);
            That(LanBeacon.TryDecode(padded, 2, bytes.Length, out var decoded) && decoded.ServerId == beacon.ServerId && decoded.GamePort == beacon.GamePort,
                "and decodes from inside a larger buffer");
        }

        foreach (var testCase in fixture.GetProperty("beacons").GetProperty("invalid").EnumerateArray())
        {
            var bytes = NetworkFixtures.Hex(testCase.Text("hex"));
            That(!LanBeacon.TryDecode(bytes, 0, bytes.Length, out _), "refuses a beacon: " + testCase.Text("about"));
        }

        var info = fixture.GetProperty("sessionInfo");
        That(info.GetProperty("maximumBytes").GetInt32() == SessionInfo.MaximumBytes, "the info limit matches the info query's");
        foreach (var testCase in info.GetProperty("valid").EnumerateArray())
        {
            var bytes = NetworkFixtures.Utf8(testCase.Text("canonical"));
            That(SessionInfo.TryDecode(bytes, out var decoded, out var error), testCase.Text("id") + " decodes: " + error);
            That(decoded!.Encode().SequenceEqual(bytes) && bytes.Length <= SessionInfo.MaximumBytes, testCase.Text("id") + " encodes back to the same bytes");
            var fields = testCase.GetProperty("info");
            That(decoded.Name == fields.Text("name") && decoded.Players == fields.GetProperty("players").GetInt32() &&
                 decoded.Gamemode == fields.OptionalText("gamemode") && decoded.DescriptorSha256 == fields.Text("descriptorSha256"),
                testCase.Text("id") + " keeps its fields");
        }

        foreach (var testCase in info.GetProperty("invalid").EnumerateArray())
        {
            That(!SessionInfo.TryDecode(NetworkFixtures.Utf8(testCase.Text("text")), out var decoded, out _) && decoded == null, "refuses session info: " + testCase.Text("about"));
        }

        Throws<ArgumentOutOfRangeException>(
            () => new SessionInfo(new string('a', 64), new string('b', 64), "Name", "2545", "0.2.0", "1.0.0", null, null, 3, 2, false, false),
            "a session cannot hold more players than its maximum");
        That(Encoding.UTF8.GetString(new SessionInfo(new string('a', 64), new string('b', 64), "Name", "2545", "0.2.0", "1.0.0", null, null, 1, 2, false, false).Encode())
                .IndexOf("host", StringComparison.OrdinalIgnoreCase) < 0,
            "session info has no member that could carry an address");
    }
}
