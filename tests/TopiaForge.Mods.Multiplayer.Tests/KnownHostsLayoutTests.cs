extern alias provider;

using System.Text;
using System.Text.RegularExpressions;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The known-hosts record keeps its pins in any valid layout. A record another program rewrote, with white space, a
/// byte-order mark or its members in another order, reads whole; it is still copied aside before the canonical rewrite
/// replaces it, and a copy is never written over. Every other document, signed or shared, is still read in canonical form
/// only.
/// </summary>
internal static class KnownHostsLayoutTests
{
    private const string Pinned = "192.168.1.20";
    private const string Other = "192.168.1.30";
    private const string Fresh = "192.168.1.40";
    private const int RecordBytes = 64 * 1024;

    internal static void ARecordInAnyLayoutKeepsItsPins()
    {
        var (first, second) = Servers();
        var pins = new[] { (Pinned, first.Descriptor.ServerId), (Other, second.Descriptor.ServerId) };
        var canonical = Record(pins);
        That(Laid(pins, string.Empty, string.Empty, reversed: false).SequenceEqual(canonical), "the layouts are built from the record's own canonical bytes");
        foreach (var (layout, bytes) in Layouts(pins))
        {
            That(!bytes.SequenceEqual(canonical), layout + " differs from the canonical bytes");
            var parsed = KnownHosts.Parse(bytes);
            That(parsed.Count == 2 && parsed.Check(Pinned, 27250, first.Descriptor.ServerId) == KnownHostCheck.Match &&
                 parsed.Check(Other, 27250, second.Descriptor.ServerId) == KnownHostCheck.Match && parsed.Serialize().SequenceEqual(canonical),
                "a record with " + layout + " keeps every pin (" + parsed.Count + ")");

            // Through the store: another server at a pinned address is a changed identity, never a first use.
            var files = new MemoryDataFiles();
            files.Contents[KnownHosts.FilePath] = bytes;
            var store = new KnownHostsStore(files);
            var changed = Resolve(Resolver(store, () => second), Pinned);
            That(changed.Failure == LiveFailure.HostIdentityChanged && changed.ServedServerId == second.Descriptor.ServerId,
                layout + ": another server at a pinned address is a changed identity (" + changed.Failure + ")");

            // The next change copies the file aside as it was, then the record replaces it in canonical form, every pin kept.
            That(Resolve(Resolver(store, () => first), Fresh).Failure == LiveFailure.None, layout + ": a new address is trusted on first use");
            var order = new List<string>();
            files.BeforeWrite = path =>
            {
                lock (order) order.Add(path);
                return Task.CompletedTask;
            };
            store.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
            var saved = files.Contents[KnownHosts.FilePath];
            That(order.SequenceEqual(new[] { KnownHostsStore.DamagedPath, KnownHosts.FilePath }) && Holds(files, KnownHostsStore.DamagedPath, bytes) &&
                 CanonicalJson.TryParse(saved, RecordBytes, out _, out _) && KnownHosts.Parse(saved).Count == 3,
                layout + ": the file is copied aside byte for byte, then rewritten canonically with every pin (" + string.Join(", ", order) + ")");
        }

        // A full record in a wide layout still fits the record's bound, so every pin of it reads.
        var full = new List<(string Host, string ServerId)>();
        for (var index = 0; index < KnownHosts.MaximumEntries; index++) full.Add(("host" + index + ".example", index % 2 == 0 ? first.Descriptor.ServerId : second.Descriptor.ServerId));
        var wide = Laid(full.ToArray(), "    ", "\r\n", reversed: true);
        That(wide.Length <= RecordBytes && KnownHosts.Parse(wide).Count == KnownHosts.MaximumEntries,
            "a full record with four-space indentation, CR LF and its members reversed reads whole (" + wide.Length + " bytes)");

        // What is not JSON, or not the record, still holds no pins, as before.
        foreach (var (what, bytes) in new[]
                 {
                     ("a truncated record", canonical.Take(canonical.Length - 3).ToArray()),
                     ("text that is not JSON", Encoding.UTF8.GetBytes("not json")),
                     ("a repeated member", Encoding.UTF8.GetBytes("{\"hosts\": [], \"hosts\": [], \"schema\": \"" + KnownHosts.Schema + "\"}")),
                     ("another schema", Encoding.UTF8.GetBytes("{ \"hosts\": [], \"schema\": \"topiaforge.known-hosts/2\" }")),
                     ("a byte-order mark only", new byte[] { 0xEF, 0xBB, 0xBF }),
                     ("white space only", Encoding.UTF8.GetBytes(" \r\n\t ")),
                 })
        {
            That(KnownHosts.Parse(bytes).Count == 0, what + " holds no pins");
        }
    }

    internal static void ASecondDamageKeepsTheFirstCopy()
    {
        var (first, second) = Servers();
        var files = new MemoryDataFiles();
        var original = Laid(new[] { (Pinned, first.Descriptor.ServerId) }, "  ", "\n", reversed: false);
        files.Contents[KnownHosts.FilePath] = original;
        var copies = new List<byte[]>();
        var hosts = new[] { Other, Fresh, "192.168.1.50", "192.168.1.60" };
        for (var round = 0; round < hosts.Length; round++)
        {
            // Another program rewrote the record this game wrote last; each time, every pin made so far still reads.
            var damaged = files.Contents[KnownHosts.FilePath];
            var store = new KnownHostsStore(files);
            That(Resolve(Resolver(store, () => second), Pinned).Failure == LiveFailure.HostIdentityChanged,
                "after damage " + (round + 1) + ", the first pin still holds");
            That(Resolve(Resolver(store, () => first), hosts[round]).Failure == LiveFailure.None, "damage " + (round + 1) + ": a new address is recorded");
            store.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
            if (round < KnownHostsStore.DamagedPaths.Count) copies.Add(damaged);
            for (var place = 0; place < copies.Count; place++)
            {
                That(Holds(files, KnownHostsStore.DamagedPaths[place], copies[place]),
                    "after damage " + (round + 1) + ", copy " + (place + 1) + " still holds damage " + (place + 1) + ", byte for byte");
            }

            var saved = files.Contents[KnownHosts.FilePath];
            That(CanonicalJson.TryParse(saved, RecordBytes, out _, out _) && KnownHosts.Parse(saved).Count == round + 2,
                "damage " + (round + 1) + " is rewritten canonically with every pin (" + KnownHosts.Parse(saved).Count + ")");

            // The next program to touch the file adds a newline, or a byte-order mark, as editors do.
            files.Contents[KnownHosts.FilePath] = round % 2 == 0 ? saved.Concat(new[] { (byte)'\n' }).ToArray() : Bom(saved);
        }

        That(!files.Contents.Where(pair => pair.Key != KnownHosts.FilePath).Any(pair => KnownHostsStore.DamagedPaths.All(path => path != pair.Key)),
            "once every place holds a copy, a later damage is rewritten without one, and no other file appears");

        // A place that cannot be read just now may hold a copy, so it is passed over, never written.
        var held = new MemoryDataFiles();
        var firstCopy = Encoding.UTF8.GetBytes("first damage");
        held.Contents[KnownHostsStore.DamagedPath] = firstCopy;
        held.FailReadsOf.Add(KnownHostsStore.DamagedPath);
        var reordered = Laid(new[] { (Pinned, first.Descriptor.ServerId) }, string.Empty, string.Empty, reversed: true);
        held.Contents[KnownHosts.FilePath] = reordered;
        var heldStore = new KnownHostsStore(held);
        That(Resolve(Resolver(heldStore, () => first), Fresh).Failure == LiveFailure.None, "a change is recorded");
        heldStore.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        That(Holds(held, KnownHostsStore.DamagedPath, firstCopy) && Holds(held, KnownHostsStore.DamagedPaths[1], reordered),
            "a copy that cannot be read is kept, and the damage takes the next place");
    }

    internal static void SignedAndSharedDocumentsStayCanonical()
    {
        // The shared reader still takes canonical bytes alone, from every other caller, in every layout the record now reads.
        var (first, second) = Servers();
        var pins = new[] { (Pinned, first.Descriptor.ServerId), (Other, second.Descriptor.ServerId) };
        That(CanonicalJson.TryParse(Record(pins), RecordBytes, out _, out _), "the canonical reader takes the canonical record");
        foreach (var (layout, bytes) in Layouts(pins))
        {
            That(!CanonicalJson.TryParse(bytes, RecordBytes, out _, out var error) && error.Length > 0, "the canonical reader refuses " + layout);
        }

        // A signed descriptor verifies only as served: the same bytes in another layout are refused, as before.
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        var served = Encoding.UTF8.GetBytes(fixture.GetProperty("valid")[0].Text("signedBytes"));
        That(SignedServerDescriptor.TryVerify(served, null, out _, out _), "a descriptor verifies as served");
        foreach (var (layout, bytes) in new[]
                 {
                     ("a trailing newline", served.Concat(new[] { (byte)'\n' }).ToArray()),
                     ("a byte-order mark", Bom(served)),
                     ("spaces after its separators", Spaced(served)),
                 })
        {
            That(!SignedServerDescriptor.TryVerify(bytes, null, out _, out _), "a descriptor with " + layout + " is refused");

            // The any-layout reader changes only what is accepted, never what is read: it reads the same document.
            That(CanonicalJson.TryParseAnyLayout(bytes, SignedServerDescriptor.MaximumBytes, out var node, out _) && CanonicalJson.Serialize(node).SequenceEqual(served),
                "the any-layout reader reads a descriptor with " + layout + " as the served document");
        }

        // So is this game's identity, which never takes the known hosts' leniency.
        var files = new MemoryDataFiles();
        LiveRig.SeedIdentity(files, 1901);
        var identity = files.Contents[IdentityStore.IdentityPath];
        That(LocalIdentity.TryParse(identity.ToArray(), out var read, out _), "an identity reads as written");
        read!.Dispose();
        That(!LocalIdentity.TryParse(Spaced(identity), out _, out _) && !LocalIdentity.TryParse(Bom(identity), out _, out _),
            "an identity in another layout is refused");

        // By construction: in the provider only the known-hosts record's reader, which the tests above show reading any
        // layout, calls the any-layout reader. Every other document, the ban list included, keeps the canonical one.
        var provider = Path.Combine(NetworkFixtures.RepositoryRoot(), "mods", "TopiaForge.Multiplayer");
        var callers = new List<string>();
        foreach (var file in Directory.EnumerateFiles(provider, "*.cs", SearchOption.AllDirectories))
        {
            var top = Path.GetRelativePath(provider, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (top == "bin" || top == "obj" || Path.GetFileName(file) == "CanonicalJson.cs") continue;
            var calls = Regex.Matches(File.ReadAllText(file), @"\bTryParseAnyLayout\s*\(").Count;
            for (var call = 0; call < calls; call++) callers.Add(Path.GetFileName(file));
        }

        That(callers.SequenceEqual(new[] { "IdentityStore.cs" }),
            "the known-hosts record is the one document the provider reads in any layout (" + string.Join(", ", callers) + ")");
    }

    internal static void ASaveQueuedBehindAFailedCopyKeepsTheDamagedRecord()
    {
        // The copy of a damaged record fails while another save is already queued behind it. That save tries the copy again
        // first, so the canonical rewrite never replaces the damaged file before its bytes are kept aside.
        var (first, second) = Servers();
        var files = new MemoryDataFiles();
        var damaged = Laid(new[] { (Pinned, first.Descriptor.ServerId) }, "  ", "\n", reversed: false);
        files.Contents[KnownHosts.FilePath] = damaged;
        var store = new KnownHostsStore(files);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeWrite = path => path == KnownHostsStore.DamagedPath ? held.Task : Task.CompletedTask;
        files.FailWritesOf.Add(KnownHostsStore.DamagedPath);
        That(Resolve(Resolver(store, () => first), Other).Failure == LiveFailure.None, "a new address is recorded");
        var save = store.SaveAfter(Task.CompletedTask);
        That(SpinWait.SpinUntil(() => files.HeldWrites == 1, 5_000), "the copy is being written");
        That(Resolve(Resolver(store, () => second), Fresh).Failure == LiveFailure.None, "and another while it is");
        var next = store.SaveAfter(save);
        held.SetResult();
        That(Faults(save) && Faults(next) && Holds(files, KnownHosts.FilePath, damaged) && !KnownHostsStore.DamagedPaths.Any(files.Contents.ContainsKey),
            "while the copy cannot be written, no save rewrites the damaged record, a save queued meanwhile included");

        // Once the copy can be written, the next save keeps the damage aside first, then writes every pin.
        files.FailWritesOf.Clear();
        files.BeforeWrite = null;
        store.MarkChanged();
        store.SaveAfter(next).GetAwaiter().GetResult();
        That(Holds(files, KnownHostsStore.DamagedPath, damaged) && KnownHosts.Parse(files.Contents[KnownHosts.FilePath]).Count == 3,
            "then the damaged record is copied aside, and rewritten with every pin");
    }

    /// <summary>The same record in each layout another program may leave it in, by name.</summary>
    private static IEnumerable<(string Layout, byte[] Bytes)> Layouts((string Host, string ServerId)[] pins)
    {
        var canonical = Record(pins);
        yield return ("a trailing newline", canonical.Concat(new[] { (byte)'\n' }).ToArray());
        yield return ("a trailing CR LF", canonical.Concat(new[] { (byte)'\r', (byte)'\n' }).ToArray());
        yield return ("a byte-order mark", Bom(canonical));
        yield return ("two-space indentation", Laid(pins, "  ", "\n", reversed: false).Concat(new[] { (byte)'\n' }).ToArray());
        yield return ("tabs and CR LF", Laid(pins, "\t", "\r\n", reversed: false));
        yield return ("its members in another order", Laid(pins, string.Empty, string.Empty, reversed: true));
        yield return ("all of them at once", Bom(Laid(pins, "    ", "\r\n", reversed: true).Concat(new[] { (byte)'\r', (byte)'\n' }).ToArray()));
    }

    /// <summary>
    /// The record as another writer may lay it out: <paramref name="indent"/> per level and <paramref name="newline"/>
    /// between lines, a space after each colon when indented, and every object's members reversed when asked.
    /// With neither, it is the canonical record.
    /// </summary>
    private static byte[] Laid((string Host, string ServerId)[] pins, string indent, string newline, bool reversed)
    {
        string Member(string key, string value) => "\"" + key + "\":" + (indent.Length > 0 ? " " : string.Empty) + value;

        string Block(char open, char close, string[] items, int depth)
        {
            if (items.Length == 0) return open.ToString() + close;
            var inner = newline + string.Concat(Enumerable.Repeat(indent, depth + 1));
            var outer = newline + string.Concat(Enumerable.Repeat(indent, depth));
            return open + inner + string.Join("," + inner, items) + outer + close;
        }

        string[] Ordered(params string[] members)
        {
            if (reversed) Array.Reverse(members);
            return members;
        }

        var seenAt = 1_760_000_000L;
        var entries = pins.Select(pin => Block('{', '}', Ordered(
            Member("seenAt", (seenAt++).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Member("serverId", "\"" + pin.ServerId + "\""),
            Member("target", "\"" + KnownHosts.TargetKey(pin.Host, 27250) + "\"")), 2)).ToArray();
        return Encoding.UTF8.GetBytes(Block('{', '}', Ordered(Member("hosts", Block('[', ']', entries, 1)), Member("schema", "\"" + KnownHosts.Schema + "\"")), 0));
    }

    /// <summary>The record as the game writes it, canonical, with an entry per pin.</summary>
    private static byte[] Record((string Host, string ServerId)[] pins) => Laid(pins, string.Empty, string.Empty, reversed: false);

    private static byte[] Bom(byte[] bytes) => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();

    /// <summary>The same document with a space after every colon and comma outside its strings.</summary>
    private static byte[] Spaced(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var builder = new StringBuilder(text.Length * 2);
        var inString = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            builder.Append(character);
            if (inString)
            {
                if (character == '\\') builder.Append(text[++index]);
                else if (character == '"') inString = false;
            }
            else if (character == '"')
            {
                inString = true;
            }
            else if (character == ':' || character == ',')
            {
                builder.Append(' ');
            }
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>The two servers of the shared descriptor fixtures, signed: one pinned at an address, one that may take its place.</summary>
    private static (SignedServerDescriptor First, SignedServerDescriptor Second) Servers()
    {
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        SignedServerDescriptor Signed(int index) => SignedServerDescriptor.Sign(
            NetworkDescriptorTests.Build(fixture.GetProperty("valid")[index].GetProperty("descriptor")),
            NetworkFixtures.Hex(fixture.GetProperty("keys")[index].Text("signingSeed")));
        return (Signed(0), Signed(1));
    }

    /// <summary>A resolver over <paramref name="store"/> whose every fetch finds the server <paramref name="serving"/> names.</summary>
    private static NetworkTargetResolver Resolver(KnownHostsStore store, Func<SignedServerDescriptor> serving) =>
        new NetworkTargetResolver(() => null, store, () => 1_000, (_, _, _) =>
            Task.FromResult(new DescriptorFetch(DescriptorFetchOutcome.Fetched, serving(), string.Empty)));

    private static ResolvedTarget Resolve(NetworkTargetResolver resolver, string host) =>
        resolver.ResolveAsync(JoinTarget.Direct(host, 27250), false, null, CancellationToken.None).GetAwaiter().GetResult();

    private static bool Holds(MemoryDataFiles files, string path, byte[] expected) =>
        files.Contents.TryGetValue(path, out var bytes) && bytes.SequenceEqual(expected);

    private static bool Faults(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
