extern alias provider;

using System.Text;
using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ProviderEngine = provider::TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A host's admission pieces on their own: the join content a player presents, the report a refusal carries, the ban
/// list, the order the host's policy checks things in, and the known-hosts record a joining player keeps.
/// </summary>
internal static class LivePolicyTests
{
    internal static void JoinContentRoundTripsAndRefusesMalformedBytes()
    {
        var proof = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var content = Encoding.UTF8.GetBytes("content");
        That(JoinContent.TryDecode(JoinContent.Encode(proof, content), out var readProof, out var readContent) &&
             readProof!.SequenceEqual(proof) && readContent.SequenceEqual(content), "content with a proof round-trips");
        That(JoinContent.TryDecode(JoinContent.Encode(null, Array.Empty<byte>()), out var none, out var empty) && none == null && empty.Length == 0,
            "and so does empty content without one");
        That(JoinContent.TryDecode(Array.Empty<byte>(), out none, out empty) && none == null && empty.Length == 0,
            "no bytes at all is a player with nothing to present");
        // The engine bounds the whole field at 64 KiB, so the largest content fits there with the largest header.
        var largest = new byte[JoinContent.MaximumContentBytes];
        var encodedLargest = JoinContent.Encode(proof, largest);
        That(encodedLargest.Length <= 64 * 1024 && JoinContent.TryDecode(encodedLargest, out _, out var read) && read.Length == largest.Length,
            "the largest content fits the engine's field with a proof (" + encodedLargest.Length + " bytes)");
        Throws<ArgumentException>(() => JoinContent.Encode(null, new byte[JoinContent.MaximumContentBytes + 1]), "more does not");
        Throws<ArgumentException>(() => JoinContent.Encode(new byte[31], content), "and a proof is 32 bytes");

        var good = JoinContent.Encode(proof, content);
        foreach (var (bytes, about) in new (byte[], string)[]
                 {
                     (Patched(good, 0, 2), "another version"),
                     (Patched(good, 1, 3), "an unknown flag"),
                     (good.Take(20).ToArray(), "a truncated proof"),
                     (new byte[] { 1, 0 }, "no length"),
                     (new byte[] { 1, 0, 0x80, 0x00 }, "a length with a needless continuation byte"),
                     (new byte[] { 1, 0, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }, "a length longer than five bytes"),
                     (new byte[] { 1, 0, 0x81, 0x80, 0x04 }, "a length past 64 KiB"),
                     (good.Concat(new byte[] { 9 }).ToArray(), "trailing bytes"),
                     (good.Take(good.Length - 1).ToArray(), "a short body"),
                 })
        {
            That(!JoinContent.TryDecode(bytes, out _, out _), "join content refuses " + about);
        }
    }

    internal static void AdmissionReportsAreCanonicalAndBounded()
    {
        var report = AdmissionReport.Encode(AdmissionCode.Content, new[] { "Zombies 0.2.0 is missing.", " padded", "bad\u0007", new string('x', 257) });
        That(Encoding.UTF8.GetString(report) ==
             "{\"code\":\"content\",\"problems\":[\"Zombies 0.2.0 is missing.\"],\"schema\":\"topiaforge.admission-report/1\"}",
            "a report is canonical JSON, and problems that are not display text are left out");
        That(AdmissionReport.TryDecode(report, out var code, out var problems) && code == AdmissionCode.Content && problems.Single() == "Zombies 0.2.0 is missing.",
            "and it reads back");
        foreach (var value in Enum.GetValues<AdmissionCode>())
        {
            That(AdmissionReport.TryDecode(AdmissionReport.Encode(value), out var back, out var nothing) && back == value && nothing.Count == 0,
                "the " + value + " code round-trips");
        }

        var many = Enumerable.Range(0, 70).Select(index => "Problem " + index).ToArray();
        That(AdmissionReport.TryDecode(AdmissionReport.Encode(AdmissionCode.Content, many), out _, out var kept) && kept.Count == 64,
            "at most 64 problems are kept");

        // The longest problems would not all fit the reader's bound: the report keeps as many as fit, in order, and stays
        // readable rather than becoming a report nobody can read.
        var longest = Enumerable.Range(0, 64).Select(index => index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 253)).ToArray();
        var bounded = AdmissionReport.Encode(AdmissionCode.Content, longest);
        var readable = AdmissionReport.TryDecode(bounded, out _, out var fitted);
        That(bounded.Length <= AdmissionReport.MaximumBytes && readable && fitted.Count > 0 && fitted.Count < 64 && fitted.SequenceEqual(longest.Take(fitted.Count)),
            "a report of the longest problems keeps those that fit, in order (" + fitted.Count + ", " + bounded.Length + " bytes)");
        foreach (var (text, about) in new[]
                 {
                     ("{\"code\":\"full\",\"problems\":[],\"schema\":\"topiaforge.admission-report/2\"}", "another schema"),
                     ("{\"code\":\"lucky\",\"problems\":[],\"schema\":\"topiaforge.admission-report/1\"}", "an unknown code"),
                     ("{\"code\":\"full\",\"extra\":1,\"problems\":[],\"schema\":\"topiaforge.admission-report/1\"}", "an unknown member"),
                     ("{\"code\":\"full\",\"schema\":\"topiaforge.admission-report/1\"}", "a missing member"),
                     ("{\"schema\":\"topiaforge.admission-report/1\",\"code\":\"full\",\"problems\":[]}", "members out of order"),
                     ("{\"code\":\"full\",\"problems\":[1],\"schema\":\"topiaforge.admission-report/1\"}", "a problem that is not text"),
                     ("", "nothing"),
                 })
        {
            That(!AdmissionReport.TryDecode(Encoding.UTF8.GetBytes(text), out _, out _), "a report refuses " + about);
        }
    }

    internal static void BanListsPersistAndStayBounded()
    {
        var bans = new BanList();
        var first = Identity(1);
        That(bans.Add(first, "First", 100) && !bans.Add(first, "Again", 200) && bans.IsBanned(first) && bans.Count == 1, "a ban is recorded once");
        That(bans.Add(Identity(2), "bad\u0007name", -5), "a ban takes any name");
        var saved = Encoding.UTF8.GetString(bans.Serialize());
        That(saved.Contains("\"name\":\"Player\"", StringComparison.Ordinal) && saved.Contains("\"bannedAt\":0", StringComparison.Ordinal),
            "a name that is not display text is saved as Player, and a time before 1970 as 0");
        var read = BanList.Parse(bans.Serialize());
        That(read.Count == 2 && read.IsBanned(first) && read.IsBanned(Identity(2)), "bans survive a save and a load");
        That(bans.Remove(first) && !bans.IsBanned(first) && !bans.Remove(first), "a ban is lifted once");
        Throws<ArgumentException>(() => bans.Add("not-a-key", "Someone", 0), "an identity is a transport key in hexadecimal");

        var full = new BanList();
        for (var index = 0; index <= BanList.MaximumEntries; index++) full.Add(Identity(1_000 + index), "Player " + index, index);
        That(full.Count == BanList.MaximumEntries && !full.IsBanned(Identity(1_000)) && full.IsBanned(Identity(1_000 + BanList.MaximumEntries)),
            "past the cap the oldest ban gives way");

        // A full list with the longest names, every character three bytes in UTF-8, still reads back whole.
        var widest = new BanList();
        for (var index = 0; index < BanList.MaximumEntries; index++) widest.Add(Identity(5_000 + index), new string('東', 48), 9_007_199_254_740_991);
        That(BanList.Parse(widest.Serialize()).Count == BanList.MaximumEntries,
            "a full list of the widest names survives a save and a load (" + widest.Serialize().Length + " bytes)");

        foreach (var damaged in new[] { null, Array.Empty<byte>(), Encoding.UTF8.GetBytes("not json"), Encoding.UTF8.GetBytes("{\"bans\":[],\"schema\":\"topiaforge.bans/2\"}") })
        {
            That(BanList.Parse(damaged).Count == 0, "a damaged or foreign file holds no bans");
        }

        var mixed = "{\"bans\":[{\"bannedAt\":1,\"identity\":\"" + Identity(5) + "\",\"name\":\"Kept\"},{\"bannedAt\":1,\"identity\":\"XYZ\",\"name\":\"Dropped\"},7]," +
                    "\"schema\":\"topiaforge.bans/1\"}";
        var partial = BanList.Parse(Encoding.UTF8.GetBytes(mixed));
        That(partial.Count == 1 && partial.IsBanned(Identity(5)), "entries that do not read are skipped and the rest kept");
    }

    internal static void TheHostChecksBansCapacityPasswordThenContent()
    {
        var now = 0L;
        var passwordKey = PasswordProof.DeriveKey("open sesame", Enumerable.Repeat((byte)3, 32).ToArray());
        var bans = new BanList();
        var content = new TestContent();
        var policy = new HostPolicy(bans, passwordKey, content, () => now);
        var link = Link(1);
        var right = PasswordProof.Prove(passwordKey, link.ChannelBinding);
        var wrong = PasswordProof.Prove(PasswordProof.DeriveKey("guess", Enumerable.Repeat((byte)3, 32).ToArray()), link.ChannelBinding);

        ProviderEngine.JoinDecision Ask(byte[]? proof, string? identity = null, int connected = 1, string protocol = TopiaForge.Mods.MultiplayerModule.ProtocolVersion, byte[]? body = null, NetworkLink? over = null) =>
            policy.Evaluate(new ProviderEngine.JoinRequestContext(over ?? link, identity ?? Identity(1),
                new ProviderEngine.JoinRequestMessage(protocol, "Guest", Array.Empty<ProviderEngine.ContractEntry>(), body ?? JoinContent.Encode(proof, content.ClientContent())),
                Array.Empty<ProviderEngine.ContractEntry>(), connected, 8));

        That(Ask(right).Accepted, "the right proof for this link is admitted");
        That(Code(Ask(null)) == AdmissionCode.PasswordRequired, "a player without a proof is told a password is needed");
        That(Code(Ask(right, over: Link(2))) == AdmissionCode.PasswordWrong, "a proof made for another link is wrong on this one");
        for (var attempt = 2; attempt <= HostPolicy.AttemptsPerMinute; attempt++) That(Code(Ask(wrong)) == AdmissionCode.PasswordWrong, "wrong guess " + attempt);
        That(Code(Ask(right)) == AdmissionCode.TooManyAttempts, "after five wrong proofs in a minute even the right one waits");
        That(Ask(right, identity: Identity(2)).Accepted, "another player is not held back");
        now += 60_000_000;
        That(Ask(right).Accepted, "and a minute later the first may try again");

        bans.Add(Identity(3), "Banned", 0);
        var banned = Ask(right, identity: Identity(3), protocol: "1.0.0", connected: 8);
        That(Code(banned) == AdmissionCode.Banned && banned.Reason == MultiplayerSessionEndReason.Banned, "a ban is checked before anything else");
        var protocol = Ask(right, protocol: "1.0.0");
        That(Code(protocol) == AdmissionCode.Protocol && protocol.Reason == MultiplayerSessionEndReason.ProtocolMismatch, "then the protocol");
        That(Code(Ask(null, connected: 8)) == AdmissionCode.Full, "then capacity, before the password");
        That(Code(Ask(right, body: new byte[] { 9, 9, 9 })) == AdmissionCode.Protocol, "then the join content's own form");
        That(Code(Ask(null, body: JoinContent.Encode(null, new byte[] { 4 }))) == AdmissionCode.PasswordRequired, "then the password, before content");
        var refused = Ask(right, body: JoinContent.Encode(right, new byte[] { 4 }));
        That(Code(refused) == AdmissionCode.Content && AdmissionReport.TryDecode(refused.Report, out _, out var problems) && problems.SequenceEqual(new[] { "Unknown content." }),
            "and the runtime's content admission decides last, its problems in the report");

        var open = new HostPolicy(bans, null, content, () => now);
        var openContext = new ProviderEngine.JoinRequestContext(link, Identity(4),
            new ProviderEngine.JoinRequestMessage(TopiaForge.Mods.MultiplayerModule.ProtocolVersion, "Guest", Array.Empty<ProviderEngine.ContractEntry>(), JoinContent.Encode(wrong, content.ClientContent())),
            Array.Empty<ProviderEngine.ContractEntry>(), 1, 8);
        That(open.Evaluate(openContext).Accepted, "a session without a password ignores any proof");
    }

    internal static void KnownHostsKeepNoAddressAndMergeInOrder()
    {
        var known = new KnownHosts();
        var first = Identity(1);
        That(known.Check("Play.Example.org", 27250, first) == KnownHostCheck.Unknown, "an address never seen is unknown");
        known.Remember("Play.Example.org", 27250, first, 10);
        That(known.Check("play.example.org", 27250, first) == KnownHostCheck.Match && known.Check("play.example.org", 27250, Identity(2)) == KnownHostCheck.Mismatch &&
             known.Check("play.example.org", 27251, first) == KnownHostCheck.Unknown, "a remembered address matches its server, by normalized host and port");
        That(!Encoding.UTF8.GetString(known.Serialize()).Contains("example", StringComparison.OrdinalIgnoreCase),
            "the record holds a hash of the address, never the address");

        // Another process saved its own record meanwhile; merging keeps both, with this record's entries the newest.
        var disk = new KnownHosts();
        disk.Remember("a.example", 1, Identity(11), 1);
        disk.Remember("b.example", 1, Identity(12), 2);
        disk.Remember("play.example.org", 27250, Identity(13), 3);
        known.MergeFrom(disk);
        That(known.Check("play.example.org", 27250, first) == KnownHostCheck.Match, "this record's entry wins where both name a target");
        That(known.ServerIdsOldestFirst().SequenceEqual(new[] { Identity(11), Identity(12), first }),
            "and the merged ones come before it, in their own order (" + string.Join(", ", known.ServerIdsOldestFirst().Select(id => id.Substring(60))) + ")");

        var full = new KnownHosts();
        for (var index = 0; index < KnownHosts.MaximumEntries; index++) full.Remember("host" + index + ".example", 1, Identity(100 + index), index);
        var older = new KnownHosts();
        older.Remember("old.example", 1, Identity(99), 0);
        full.MergeFrom(older);
        That(full.Count == KnownHosts.MaximumEntries && full.Check("old.example", 1, Identity(99)) == KnownHostCheck.Unknown &&
             full.Check("host0.example", 1, Identity(100)) == KnownHostCheck.Match, "past the cap the oldest merged entry goes first");
        That(KnownHosts.Parse(full.Serialize()).Count == KnownHosts.MaximumEntries, "and the record survives a save");
    }

    private static AdmissionCode? Code(ProviderEngine.JoinDecision decision) =>
        !decision.Accepted && AdmissionReport.TryDecode(decision.Report, out var code, out _) ? code : null;

    private static string Identity(int number) => ((long)number).ToString("x64", System.Globalization.CultureInfo.InvariantCulture);

    private static NetworkLink Link(byte number)
    {
        var key = new byte[32];
        key[0] = number;
        return new NetworkLink(number, key, default, Enumerable.Repeat(number, 32).ToArray());
    }

    private static byte[] Patched(byte[] bytes, int index, byte value)
    {
        var copy = (byte[])bytes.Clone();
        copy[index] = value;
        return copy;
    }
}
