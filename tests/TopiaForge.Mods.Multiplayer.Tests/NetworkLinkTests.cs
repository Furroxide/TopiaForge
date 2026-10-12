extern alias provider;

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
            That(link.ToString() == testCase.Text("canonical"), input + " formats as " + testCase.Text("canonical") + ", not " + link);
            That(SessionLink.TryParse(link.ToString(), out var again) && again!.ToString() == link.ToString(), input + " round-trips");
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
