extern alias provider;

using System.Text;
using System.Text.Json;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The provider's own records on disk, the ban list and the identity, read in any valid layout, as the known-hosts record
/// does: a file another program rewrote with white space, a byte-order mark or its members in another order keeps every
/// ban and the same identity. It still counts as damage, so its bytes are kept aside before this game's own canonical form
/// replaces it, and the identity's keys pass every check they passed before. Nothing about them reaches a notice.
/// </summary>
internal static class LocalRecordsLayoutTests
{
    private const int ListBytes = 512 * 1024;
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();

    internal static void ABanListInAnyLayoutKeepsEveryBan()
    {
        var canonical = List((Key(1), "Sam"), (Key(2), "Griefer"));
        That(Relaid(canonical, string.Empty, string.Empty, reversed: false).SequenceEqual(canonical), "the layouts are built from the list's own canonical bytes");
        foreach (var (layout, bytes) in Layouts(canonical))
        {
            That(!bytes.SequenceEqual(canonical), layout + " differs from the canonical bytes");
            var parsed = BanList.Parse(bytes);
            That(parsed.Count == 2 && parsed.IsBanned(Key(1)) && parsed.IsBanned(Key(2)) && parsed.Serialize().SequenceEqual(canonical),
                "a ban list with " + layout + " keeps every ban (" + parsed.Count + ")");

            // Through the store: the list reads whole, and the next change keeps the file aside as it was, then writes the
            // list in canonical form, every ban kept.
            var files = new MemoryDataFiles();
            files.Contents[BanList.FilePath] = bytes;
            var store = new BanStore(files);
            That(store.EnsureReadAsync().GetAwaiter().GetResult() && store.Record.IsBanned(Key(1)) && store.Record.IsBanned(Key(2)),
                layout + ": the store reads every ban");
            store.Record.Add(Key(3), "Newcomer", 300);
            store.MarkChanged();
            var order = new List<string>();
            files.BeforeWrite = path =>
            {
                lock (order) order.Add(path);
                return Task.CompletedTask;
            };
            store.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
            var saved = files.Contents[BanList.FilePath];
            That(order.SequenceEqual(new[] { BanStore.DamagedPaths[0], BanList.FilePath }) && Holds(files, BanStore.DamagedPaths[0], bytes) &&
                 CanonicalJson.TryParse(saved, ListBytes, out _, out _) && BanList.Parse(saved).Count == 3,
                layout + ": the file is copied aside byte for byte, then rewritten canonically with every ban (" + string.Join(", ", order) + ")");
        }

        // A full list of the longest names in a wide layout still fits the list's bound, so every ban of it reads.
        var longest = string.Concat(Enumerable.Repeat("\u4e2d", 48));
        var full = new BanList();
        for (var index = 0; index < BanList.MaximumEntries; index++) full.Add(Key(1_000 + index), longest, 1_760_000_000L + index);
        var wide = Bom(Relaid(full.Serialize(), "    ", "\r\n", reversed: true));
        That(wide.Length <= ListBytes && BanList.Parse(wide).Count == BanList.MaximumEntries,
            "a full list of the longest names with four-space indentation, CR LF, a byte-order mark and its members reversed reads whole (" + wide.Length + " bytes)");

        // What is not JSON, or not the list, still holds no bans, as before.
        foreach (var (what, bytes) in new[]
                 {
                     ("a truncated list", canonical.Take(canonical.Length - 3).ToArray()),
                     ("text that is not JSON", Encoding.UTF8.GetBytes("not json")),
                     ("a repeated member", Encoding.UTF8.GetBytes("{\"bans\": [], \"bans\": [], \"schema\": \"" + BanList.Schema + "\"}")),
                     ("another schema", Encoding.UTF8.GetBytes("{ \"bans\": [], \"schema\": \"topiaforge.bans/2\" }")),
                     ("a byte-order mark only", new byte[] { 0xEF, 0xBB, 0xBF }),
                     ("white space only", Encoding.UTF8.GetBytes(" \r\n\t ")),
                 })
        {
            That(BanList.Parse(bytes).Count == 0, what + " holds no bans");
        }

        // A host whose list another program re-laid keeps out every player it bans.
        using var rig = new LiveRig(193);
        Register(rig.Host);
        var banned = rig.AddClient("banned");
        LiveRig.SeedIdentity(rig.ClientFiles(banned), 1931);
        var relaid = Bom(Relaid(List((FingerprintOf(rig.ClientFiles(banned)), "Banned"), (Key(7), "Other")), "  ", "\n", reversed: true));
        rig.HostFiles.Contents[BanList.FilePath] = relaid;
        rig.StartHosting(new HostSettings("Careful") { Port = 0 });
        var live = rig.Host.Live!;
        That(live.HostPhase == HostPhase.Hosting && live.Bans.Count == 2, "a host whose list is in another layout opens with every ban (" + live.Bans.Count + ")");
        Register(banned);
        var refused = rig.Join(banned, new JoinSettings("Banned"));
        That(rig.Run(() => refused.Phase == JoinPhase.Ended) && refused.Failure == LiveFailure.Banned && refused.RejectionCode == AdmissionCode.Banned,
            "and a player that list bans is refused (" + refused.Failure + ")");
        var guest = rig.AddClient("guest");
        Register(guest);
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "while anyone else joins (" + join.Failure + ")");
    }

    internal static void AnIdentityInAnyLayoutKeepsTheSameIdentity()
    {
        var seeded = new MemoryDataFiles();
        LiveRig.SeedIdentity(seeded, 1941);
        var canonical = seeded.Contents[IdentityStore.IdentityPath];
        That(LocalIdentity.TryParse(canonical.ToArray(), out var original, out var isCanonical, out _) && isCanonical, "the identity as this game writes it reads as canonical");
        using var source = original!;
        foreach (var (layout, bytes) in Layouts(canonical))
        {
            That(!bytes.SequenceEqual(canonical), layout + " differs from the canonical bytes");
            That(LocalIdentity.TryParse(bytes.ToArray(), out var parsed, out var parsedCanonical, out var error) && !parsedCanonical && Same(parsed!, source),
                "an identity with " + layout + " reads as the same identity, every key the same, and not as canonical (" + error + ")");
            parsed!.Dispose();

            // Through the store: the same identity, its file kept aside as it was, then rewritten canonically. Every
            // buffer that held its keys is zeroed, and no notice is raised.
            var files = new TrackingFiles();
            files.Contents[IdentityStore.IdentityPath] = bytes.ToArray();
            var store = new IdentityStore(files, () => 1_900_000_000);
            var notices = new List<string>();
            store.IdentityReplaced += notices.Add;
            using var loaded = store.LoadOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult();
            That(Same(loaded, source) && notices.Count == 0 && !files.Contents.ContainsKey(IdentityStore.UnreadablePath),
                layout + ": the store loads the same identity, sets nothing aside as unreadable, and says nothing");
            That(files.Writes.SequenceEqual(new[] { IdentityStore.DamagedPath, IdentityStore.IdentityPath }) &&
                 Holds(files, IdentityStore.DamagedPath, bytes) && Holds(files, IdentityStore.IdentityPath, canonical),
                layout + ": the file is copied aside byte for byte, then rewritten as this game writes it (" + string.Join(", ", files.Writes) + ")");
            That(files.Handed.All(buffer => buffer.All(value => value == 0)),
                layout + ": every buffer that held the identity's keys is zeroed once it is read or written");
        }

        // Every check on the keys holds in any layout: a key of the wrong length, a key in another spelling, a member
        // missing or unknown, or another schema is refused as before, and set aside as unreadable beside a new identity.
        var text = Encoding.UTF8.GetString(canonical);
        string signingSeed;
        using (var parsedDocument = JsonDocument.Parse(canonical)) signingSeed = parsedDocument.RootElement.GetProperty("signingSeed").GetString()!;

        // The last character of a 32-byte key carries two unused bits, which must be zero: setting one spells the same bytes otherwise.
        var otherSpelling = signingSeed[..^1] + Base64UrlAlphabet[Base64UrlAlphabet.IndexOf(signingSeed[^1], StringComparison.Ordinal) ^ 1];
        foreach (var (what, document) in new[]
                 {
                     ("a key cut short", text.Replace(signingSeed, signingSeed[..^2])),
                     ("a key in another spelling of the same bytes", text.Replace(signingSeed, otherSpelling)),
                     ("a key missing", text.Replace(",\"signingSeed\":\"" + signingSeed + "\"", string.Empty)),
                     ("an unknown member", text.Replace("{", "{\"extra\":1,")),
                     ("another schema", text.Replace("identity/1", "identity/2")),
                 })
        {
            foreach (var (layout, bytes) in Layouts(Encoding.UTF8.GetBytes(document)).Take(3))
            {
                That(!LocalIdentity.TryParse(bytes.ToArray(), out _, out _, out var error) && error.Length > 0 && !error.Contains(signingSeed[..8], StringComparison.Ordinal),
                    "an identity with " + what + " is refused with " + layout + ", naming no key (" + error + ")");
                var files = new TrackingFiles();
                files.Contents[IdentityStore.IdentityPath] = bytes.ToArray();
                var store = new IdentityStore(files, () => 1_900_000_000);
                var notices = new List<string>();
                store.IdentityReplaced += notices.Add;
                using var fresh = store.LoadOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult();
                That(!Same(fresh, source) && Holds(files, IdentityStore.UnreadablePath, bytes) && !files.Contents.ContainsKey(IdentityStore.DamagedPath) &&
                     notices.Count == 1 && !notices[0].Contains(signingSeed[..8], StringComparison.Ordinal) && !notices[0].Contains(source.Fingerprint, StringComparison.Ordinal),
                    what + " with " + layout + ": the file is set aside as unreadable beside a new identity, and the notice names no key");
            }
        }

        // A copy kept before is never written over: a later re-laid file is rewritten without a copy, and the first stays.
        var twice = new TrackingFiles();
        var first = Layouts(canonical).First().Bytes;
        var second = Layouts(canonical).Skip(2).First().Bytes;
        twice.Contents[IdentityStore.IdentityPath] = first.ToArray();
        LoadOnce(twice);
        twice.Contents[IdentityStore.IdentityPath] = second.ToArray();
        LoadOnce(twice);
        That(Holds(twice, IdentityStore.DamagedPath, first) && Holds(twice, IdentityStore.IdentityPath, canonical),
            "a second re-laid file is rewritten without a copy, and the first copy stays as it was");

        // While the copy cannot be written, the file stays as it is, and still reads; once it can, it is kept, then rewritten.
        var held = new TrackingFiles();
        held.Contents[IdentityStore.IdentityPath] = first.ToArray();
        held.FailWritesOf.Add(IdentityStore.DamagedPath);
        using (var loaded = new IdentityStore(held, () => 1_900_000_000).LoadOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult())
        {
            That(Same(loaded, source) && Holds(held, IdentityStore.IdentityPath, first) && !held.Contents.ContainsKey(IdentityStore.DamagedPath),
                "while its copy cannot be written, the re-laid file stays as it is, and the same identity loads");
        }

        held.FailWritesOf.Clear();
        LoadOnce(held);
        That(Holds(held, IdentityStore.DamagedPath, first) && Holds(held, IdentityStore.IdentityPath, canonical), "once it can, the copy is kept and the file rewritten");
    }

    private static void LoadOnce(TrackingFiles files)
    {
        using var loaded = new IdentityStore(files, () => 1_900_000_000).LoadOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Whether two identities hold the same keys, compared through what they derive and their private keys.</summary>
    private static bool Same(LocalIdentity left, LocalIdentity right) =>
        left.Fingerprint == right.Fingerprint && left.ServerId == right.ServerId && left.CreatedAt == right.CreatedAt &&
        left.CopyTransportPrivateKey().SequenceEqual(right.CopyTransportPrivateKey()) && left.CopySigningSeed().SequenceEqual(right.CopySigningSeed()) &&
        left.PlayerKeyFor(new byte[32]) == right.PlayerKeyFor(new byte[32]);

    private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    /// <summary>The same document in each layout another program may leave it in, by name.</summary>
    internal static IEnumerable<(string Layout, byte[] Bytes)> Layouts(byte[] canonical)
    {
        yield return ("a trailing newline", canonical.Concat(new[] { (byte)'\n' }).ToArray());
        yield return ("a trailing CR LF", canonical.Concat(new[] { (byte)'\r', (byte)'\n' }).ToArray());
        yield return ("a byte-order mark", Bom(canonical));
        yield return ("two-space indentation", Relaid(canonical, "  ", "\n", reversed: false).Concat(new[] { (byte)'\n' }).ToArray());
        yield return ("tabs and CR LF", Relaid(canonical, "\t", "\r\n", reversed: false));
        yield return ("its members in another order", Relaid(canonical, string.Empty, string.Empty, reversed: true));
        yield return ("all of them at once", Bom(Relaid(canonical, "    ", "\r\n", reversed: true).Concat(new[] { (byte)'\r', (byte)'\n' }).ToArray()));
    }

    /// <summary>
    /// A canonical document as another writer may lay it out: <paramref name="indent"/> per level and
    /// <paramref name="newline"/> between lines, a space after each colon when indented, and every object's members
    /// reversed when asked. Strings and numbers keep their own text. With neither, it is the canonical document.
    /// </summary>
    internal static byte[] Relaid(byte[] canonical, string indent, string newline, bool reversed)
    {
        using var document = JsonDocument.Parse(canonical);
        return Encoding.UTF8.GetBytes(Write(document.RootElement, 0));

        string Write(JsonElement element, int depth)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var members = element.EnumerateObject()
                        .Select(member => "\"" + member.Name + "\":" + (indent.Length > 0 ? " " : string.Empty) + Write(member.Value, depth + 1))
                        .ToList();
                    if (reversed) members.Reverse();
                    return Block('{', '}', members, depth);
                case JsonValueKind.Array:
                    return Block('[', ']', element.EnumerateArray().Select(item => Write(item, depth + 1)).ToList(), depth);
                default:
                    return element.GetRawText();
            }
        }

        string Block(char open, char close, List<string> items, int depth)
        {
            if (items.Count == 0) return open.ToString() + close;
            var inner = newline + string.Concat(Enumerable.Repeat(indent, depth + 1));
            var outer = newline + string.Concat(Enumerable.Repeat(indent, depth));
            return open + inner + string.Join("," + inner, items) + outer + close;
        }
    }

    internal static byte[] Bom(byte[] bytes) => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();

    /// <summary>A list as the game writes it, canonical, with a ban per key, oldest first.</summary>
    private static byte[] List(params (string Identity, string Name)[] bans)
    {
        var list = new BanList();
        var at = 100;
        foreach (var (identity, name) in bans) list.Add(identity, name, at++);
        return list.Serialize();
    }

    /// <summary>A transport key in hexadecimal, made from <paramref name="seed"/>.</summary>
    private static string Key(int seed) => seed.ToString("x8", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, 'a');

    /// <summary>The key a provider whose data folder holds a seeded identity proves, which a host bans.</summary>
    private static string FingerprintOf(MemoryDataFiles files)
    {
        That(LocalIdentity.TryParse(files.Contents[IdentityStore.IdentityPath].ToArray(), out var identity, out _, out _), "the seeded identity reads");
        using (identity) return identity!.Fingerprint;
    }

    private static bool Holds(MemoryDataFiles files, string path, byte[] expected) =>
        files.Contents.TryGetValue(path, out var bytes) && bytes.SequenceEqual(expected);

    private static bool Holds(TrackingFiles files, string path, byte[] expected) =>
        files.Contents.TryGetValue(path, out var bytes) && bytes.SequenceEqual(expected);

    private static void Register(IMultiplayerSession session)
    {
        if (!session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)).TryGetValue(out _))
        {
            throw new InvalidOperationException("Assertion failed: the score registers");
        }
    }

    /// <summary>
    /// An in-memory data folder that keeps every buffer it hands the store or the store hands it, so a test can check that
    /// the store zeroes each one that held the identity's keys, and the order of the writes.
    /// </summary>
    private sealed class TrackingFiles : INetworkDataFiles
    {
        internal Dictionary<string, byte[]> Contents { get; } = new();

        internal HashSet<string> FailWritesOf { get; } = new();

        /// <summary>Gets the paths written, in order.</summary>
        internal List<string> Writes { get; } = new();

        /// <summary>Gets every buffer that held an identity file, read or written.</summary>
        internal List<byte[]> Handed { get; } = new();

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken)
        {
            if (!Contents.TryGetValue(relativePath, out var bytes)) return Task.FromResult<byte[]?>(null);
            var copy = bytes.ToArray();
            Handed.Add(copy);
            return Task.FromResult<byte[]?>(copy);
        }

        public Task WriteAsync(string relativePath, byte[] content, CancellationToken cancellationToken)
        {
            if (FailWritesOf.Contains(relativePath)) return Task.FromException(new IOException("The disk is full."));
            Contents[relativePath] = content.ToArray();
            Writes.Add(relativePath);
            Handed.Add(content);
            return Task.CompletedTask;
        }
    }
}
