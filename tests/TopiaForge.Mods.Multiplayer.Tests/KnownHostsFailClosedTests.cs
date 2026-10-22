extern alias provider;

using System.Text;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The known-hosts record fails closed. A file that cannot be read is never taken for an empty record or written over:
/// typed addresses are refused with the reason until it reads, and trying again then recovers. A damaged file is
/// salvaged as before, and its bytes are kept aside before anything replaces it.
/// </summary>
internal static class KnownHostsFailClosedTests
{
    private const string Pinned = "192.168.1.20";
    private const string Fresh = "192.168.1.30";
    private const string Later = "192.168.1.40";

    internal static void AnUnreadableRecordRefusesAndWritesNothing()
    {
        var (first, second) = Servers();
        var files = new MemoryDataFiles();
        var original = Record((Pinned, first.Descriptor.ServerId));
        files.Contents[KnownHosts.FilePath] = original;

        // Another program holds the file, so it cannot be read.
        files.FailReadsOf.Add(KnownHosts.FilePath);
        var store = new KnownHostsStore(files);
        var serving = second;
        var resolver = Resolver(store, () => serving);

        var impostor = Resolve(resolver, Pinned);
        That(impostor.Failure == LiveFailure.KnownHostsUnreadable && impostor.ServedServerId == second.Descriptor.ServerId &&
             impostor.Detail.Contains("could not be read", StringComparison.Ordinal),
            "while the record cannot be read, another server at a pinned address is refused with the reason (" + impostor.Failure + ")");
        That((int)LiveFailure.KnownHostsUnreadable == 38 && ((InternalLiveFailure)38).ToString() == nameof(LiveFailure.KnownHostsUnreadable),
            "and the internal control reports it under the same code");
        serving = first;
        That(Resolve(resolver, Pinned).Failure == LiveFailure.KnownHostsUnreadable, "so is the server the record pins: nothing can be checked");
        That(Resolve(resolver, Fresh).Failure == LiveFailure.KnownHostsUnreadable, "and a first server at an address the record may not hold");
        That(store.Record.Count == 0 && !store.IsRead, "nothing is pinned while the record is unread");
        That(ReferenceEquals(store.SaveAfter(Task.CompletedTask), Task.CompletedTask) && Holds(files, KnownHosts.FilePath, original),
            "and nothing is saved over the file");

        // The player may still trust the one server an address serves now, for this join; that is not recorded either.
        serving = second;
        That(Resolve(resolver, Pinned, trusted: second.Descriptor.ServerId).Failure == LiveFailure.None && store.Record.Count == 0,
            "a server the player trusts for this join goes on, unrecorded");
        That(Resolve(resolver, Pinned, trusted: first.Descriptor.ServerId).Failure == LiveFailure.KnownHostsUnreadable,
            "trusting some other server lets nothing in");

        // Once the file reads again, trying again recovers the pin it holds.
        files.FailReadsOf.Clear();
        var again = Resolve(resolver, Pinned);
        That(again.Failure == LiveFailure.HostIdentityChanged && again.ServedServerId == second.Descriptor.ServerId && store.IsRead,
            "once the record reads, another server at the pinned address is a changed identity again (" + again.Failure + ")");
        serving = first;
        That(Resolve(resolver, Pinned).Failure == LiveFailure.None && Resolve(resolver, Fresh).Failure == LiveFailure.None,
            "the pinned server joins, and a new address is trusted on first use");
        store.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        var saved = KnownHosts.Parse(files.Contents[KnownHosts.FilePath]);
        That(saved.Count == 2 && saved.Check(Pinned, 27250, first.Descriptor.ServerId) == KnownHostCheck.Match &&
             saved.Check(Fresh, 27250, first.Descriptor.ServerId) == KnownHostCheck.Match && !files.Contents.ContainsKey(KnownHostsStore.DamagedPath),
            "and the next save keeps the file's pin beside the new one");
    }

    internal static void ClosingWritesNothingOverAnUnreadableRecord()
    {
        var (first, _) = Servers();
        using var rig = new LiveRig(187);
        var guest = rig.AddClient("guest");
        var files = rig.ClientFiles(guest);
        var original = Record((Pinned, first.Descriptor.ServerId));
        lock (files.Contents) files.Contents[KnownHosts.FilePath] = original;
        files.FailReadsOf.Add(KnownHosts.FilePath);
        guest.Live!.PrepareAsync();
        That(rig.Run(() => guest.Live.IsReady), "the identity loads although the known hosts cannot be read");
        That(!guest.Live.KnownHostsFile.IsRead && guest.Live.KnownHosts.Count == 0, "the record stays unread, not empty");

        // Something marks the record changed, and the game closes: the file it could not read is left as it is.
        guest.Live.MarkKnownHostsChanged();
        guest.Dispose();
        lock (files.Contents)
        {
            That(Holds(files, KnownHosts.FilePath, original) && !files.Contents.ContainsKey(KnownHostsStore.DamagedPath),
                "closing writes nothing over a record it could not read");
        }

        // A provider that starts once the file reads again finds every pin.
        var later = rig.AddClient("later");
        var laterFiles = rig.ClientFiles(later);
        lock (laterFiles.Contents) laterFiles.Contents[KnownHosts.FilePath] = original;
        later.Live!.PrepareAsync();
        That(rig.Run(() => later.Live.IsReady) && later.Live.KnownHostsFile.IsRead &&
             later.Live.KnownHosts.Check(Pinned, 27250, first.Descriptor.ServerId) == KnownHostCheck.Match,
            "the pins come back as soon as the file reads");
    }

    internal static void ADamagedRecordIsSalvagedAndKeptAside()
    {
        var (first, second) = Servers();

        // A corrupt record holds nothing that reads, as before, so a first server is trusted.
        var files = new MemoryDataFiles();
        var corrupt = Encoding.UTF8.GetBytes("{\"hosts\":[{\"seenAt\":1,");
        files.Contents[KnownHosts.FilePath] = corrupt;
        var store = new KnownHostsStore(files);
        var serving = first;
        var resolver = Resolver(store, () => serving);
        That(Resolve(resolver, Pinned).Failure == LiveFailure.None && store.IsRead, "a corrupt record reads as no pins, so the first server is trusted");

        // Its bytes are copied aside before anything replaces them, and while the copy cannot be written, nothing does.
        files.FailWritesOf.Add(KnownHostsStore.DamagedPath);
        That(Faults(store.SaveAfter(Task.CompletedTask)) && Holds(files, KnownHosts.FilePath, corrupt) && !files.Contents.ContainsKey(KnownHostsStore.DamagedPath),
            "without its copy, the damaged file stays where it is");
        files.FailWritesOf.Clear();
        var order = new List<string>();
        files.BeforeWrite = path =>
        {
            lock (order) order.Add(path);
            return Task.CompletedTask;
        };
        That(Resolve(resolver, Fresh).Failure == LiveFailure.None, "a later change is recorded");
        store.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        That(order.SequenceEqual(new[] { KnownHostsStore.DamagedPath, KnownHosts.FilePath }) && Holds(files, KnownHostsStore.DamagedPath, corrupt),
            "the next save writes the copy first, byte for byte, then the record (" + string.Join(", ", order) + ")");
        var saved = KnownHosts.Parse(files.Contents[KnownHosts.FilePath]);
        That(saved.Count == 2 && saved.Check(Pinned, 27250, first.Descriptor.ServerId) == KnownHostCheck.Match, "the record holds what was trusted since");
        order.Clear();
        That(Resolve(resolver, Later).Failure == LiveFailure.None, "another change is recorded");
        store.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        That(order.SequenceEqual(new[] { KnownHosts.FilePath }) && Holds(files, KnownHostsStore.DamagedPath, corrupt), "the copy is made once");

        // A partly damaged record keeps what reads: a pin that reads still pins, the entry that does not is dropped, and
        // the whole file is kept aside first.
        var partly = Record((Pinned, first.Descriptor.ServerId), ("192.168.1.99", "not a server id"), (Fresh, second.Descriptor.ServerId));
        var damagedFiles = new MemoryDataFiles();
        damagedFiles.Contents[KnownHosts.FilePath] = partly;
        var damagedStore = new KnownHostsStore(damagedFiles);
        serving = second;
        var salvaging = Resolver(damagedStore, () => serving);
        var changed = Resolve(salvaging, Pinned);
        That(changed.Failure == LiveFailure.HostIdentityChanged && changed.ServedServerId == second.Descriptor.ServerId,
            "a pin that reads still pins: another server there is a changed identity (" + changed.Failure + ")");
        That(Resolve(salvaging, Fresh).Failure == LiveFailure.None && damagedStore.Record.Count == 2, "and so does every other one, while the damaged entry is dropped");
        That(Resolve(salvaging, Later).Failure == LiveFailure.None, "a new address is trusted on first use");
        damagedStore.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        var salvaged = KnownHosts.Parse(damagedFiles.Contents[KnownHosts.FilePath]);
        That(Holds(damagedFiles, KnownHostsStore.DamagedPath, partly) && salvaged.Count == 3 &&
             salvaged.Check(Pinned, 27250, first.Descriptor.ServerId) == KnownHostCheck.Match &&
             salvaged.Check("192.168.1.99", 27250, second.Descriptor.ServerId) == KnownHostCheck.Unknown,
            "the save keeps the whole file aside, then writes what reads and what was added");

        // A record of another schema is set aside the same way, never guessed at.
        var future = Encoding.UTF8.GetBytes("{\"hosts\":[],\"schema\":\"topiaforge.known-hosts/2\"}");
        var futureFiles = new MemoryDataFiles();
        futureFiles.Contents[KnownHosts.FilePath] = future;
        var futureStore = new KnownHostsStore(futureFiles);
        serving = first;
        That(Resolve(Resolver(futureStore, () => serving), Pinned).Failure == LiveFailure.None, "a record of another schema holds no pins this game reads");
        futureStore.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        That(Holds(futureFiles, KnownHostsStore.DamagedPath, future), "and is kept aside before this game's record replaces it");

        // An intact record is never copied.
        var intactFiles = new MemoryDataFiles();
        intactFiles.Contents[KnownHosts.FilePath] = Record((Pinned, first.Descriptor.ServerId));
        var intactStore = new KnownHostsStore(intactFiles);
        That(Resolve(Resolver(intactStore, () => serving), Fresh).Failure == LiveFailure.None, "an intact record is used as it is");
        intactStore.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        That(!intactFiles.Contents.ContainsKey(KnownHostsStore.DamagedPath) && KnownHosts.Parse(intactFiles.Contents[KnownHosts.FilePath]).Count == 2,
            "and saved without a copy");
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

    private static ResolvedTarget Resolve(NetworkTargetResolver resolver, string host, string? trusted = null) =>
        resolver.ResolveAsync(JoinTarget.Direct(host, 27250), false, trusted, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>A record file as the game writes it, canonical, with an entry per pin; a pin may name an invalid server id.</summary>
    private static byte[] Record(params (string Host, string ServerId)[] pins)
    {
        var items = new List<JsonNode>();
        var seenAt = 1;
        foreach (var (host, serverId) in pins)
        {
            items.Add(new JsonObjectNode().Add("target", KnownHosts.TargetKey(host, 27250)).Add("serverId", serverId).Add("seenAt", seenAt++));
        }

        return CanonicalJson.Serialize(new JsonObjectNode().Add("schema", KnownHosts.Schema).Add("hosts", new JsonArrayNode(items)));
    }

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
