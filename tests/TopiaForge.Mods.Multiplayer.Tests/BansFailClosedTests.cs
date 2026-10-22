extern alias provider;

using System.Reflection;
using System.Text;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A host's ban list fails closed. A file that cannot be read is never taken for an empty list or written over: hosting
/// does not start until it reads, so no banned player is ever admitted unchecked, and hosting again then recovers. A ban
/// made meanwhile is held, and joins the file's bans once they read. A damaged list is salvaged, and its bytes are kept
/// aside before anything replaces them, never over a copy kept before.
/// </summary>
internal static class BansFailClosedTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();

    internal static void AnUnreadableListOpensNoSessionAndWritesNothing()
    {
        using var rig = new LiveRig(188);
        Register(rig.Host);
        var banned = rig.AddClient("banned");
        LiveRig.SeedIdentity(rig.ClientFiles(banned), 1881);
        var original = List((FingerprintOf(rig.ClientFiles(banned)), "Banned"), (Key(7), "Other"));
        rig.HostFiles.Contents[BanList.FilePath] = original;
        var reads = 0;
        rig.HostFiles.BeforeRead = path =>
        {
            if (path == BanList.FilePath) Interlocked.Increment(ref reads);
            return Task.CompletedTask;
        };

        // Another program holds the list, so it cannot be read: the session does not open, and nothing touches the file.
        rig.HostFiles.FailReadsOf.Add(BanList.FilePath);
        rig.StartHosting(new HostSettings("Careful") { Port = 0 });
        var live = rig.Host.Live!;
        That(live.HostPhase == HostPhase.Failed && live.HostFailure == LiveFailure.BansUnreadable && rig.HostFactory.Networks.Count == 0,
            "while the ban list cannot be read, hosting does not start and nothing binds (" + live.HostPhase + ", " + live.HostFailure + ")");
        That((int)LiveFailure.BansUnreadable == 40 &&
             provider::TopiaForge.Multiplayer.InternalControl.ToFailure(LiveFailure.BansUnreadable) == InternalLiveFailure.BansUnreadable,
            "and the internal control reports it under the same code, 40");
        That(!live.BansFile.IsRead && live.Bans.Count == 0 && Holds(rig.HostFiles, BanList.FilePath, original) && !HoldsAnyCopy(rig.HostFiles),
            "the list stays unread, not empty, and nothing is written over it or copied");

        // Hosting again reads the list again, and while it still cannot be read the session still does not open.
        var before = Volatile.Read(ref reads);
        rig.StartHosting(new HostSettings("Careful") { Port = 0 });
        That(live.HostFailure == LiveFailure.BansUnreadable && Volatile.Read(ref reads) > before && rig.HostFactory.Networks.Count == 0,
            "hosting again reads the list again (" + before + " reads before, " + Volatile.Read(ref reads) + " after)");

        // Once it reads, the session opens with every ban, and the players it bans stay out.
        rig.HostFiles.FailReadsOf.Clear();
        rig.StartHosting(new HostSettings("Careful") { Port = 0 });
        That(live.HostPhase == HostPhase.Hosting && live.BansFile.IsRead && live.Bans.Count == 2, "once the list reads, hosting opens with every ban");
        Register(banned);
        var refused = rig.Join(banned, new JoinSettings("Banned"));
        That(rig.Run(() => refused.Phase == JoinPhase.Ended) && refused.Failure == LiveFailure.Banned && refused.RejectionCode == AdmissionCode.Banned,
            "a player the list bans is refused once it reads (" + refused.Failure + ")");
        var guest = rig.AddClient("guest");
        Register(guest);
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "and anyone else joins (" + join.Failure + ")");

        // Nothing changed, so closing writes nothing.
        rig.Host.Dispose();
        lock (rig.HostFiles.Contents)
        {
            That(Holds(rig.HostFiles, BanList.FilePath, original) && !HoldsAnyCopy(rig.HostFiles), "and closing leaves the list as it was");
        }
    }

    internal static void ABanMadeWhileTheListIsUnreadIsHeldThenMerged()
    {
        var files = new MemoryDataFiles();
        var original = List((Key(1), "First"), (Key(2), "Second"));
        files.Contents[BanList.FilePath] = original;
        files.FailReadsOf.Add(BanList.FilePath);
        var store = new BanStore(files);
        That(!store.EnsureReadAsync().GetAwaiter().GetResult() && !store.IsRead && store.Record.Count == 0, "a list that cannot be read stays unread");

        // A ban made while the list is unread counts at once, but is held: nothing is saved over the file it could not read.
        That(store.Record.Add(Key(3), "Held", 300), "a ban is made while the list is unread");
        store.MarkChanged();
        That(store.Record.IsBanned(Key(3)), "and counts at once");
        That(ReferenceEquals(store.SaveAfter(Task.CompletedTask), Task.CompletedTask) && Holds(files, BanList.FilePath, original),
            "but nothing is saved over the list while it is unread");
        That(ReferenceEquals(store.SaveAfter(Task.CompletedTask), Task.CompletedTask) && Holds(files, BanList.FilePath, original), "however often a save is asked for");

        // Once the file reads, the held ban joins its bans, as the newest, and the next save keeps them all.
        files.FailReadsOf.Clear();
        That(store.EnsureReadAsync().GetAwaiter().GetResult() && store.IsRead, "the list reads once the file is free");
        var merged = store.Record.Records();
        That(merged.Select(record => record.Identity).SequenceEqual(new[] { Key(1), Key(2), Key(3) }),
            "the held ban joins the file's bans, as the newest (" + merged.Count + " bans)");
        store.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        var saved = BanList.Parse(files.Contents[BanList.FilePath]).Records();
        That(saved.Select(record => record.Identity).SequenceEqual(new[] { Key(1), Key(2), Key(3) }) && saved[2].Name == "Held" && !HoldsAnyCopy(files),
            "and the next save keeps every ban the file held beside the held one");

        // Where the file bans the same player, the held ban is kept; past the cap, the oldest of the file's bans gives way.
        var full = new BanList();
        for (var index = 0; index < BanList.MaximumEntries; index++) full.Add(Key(1_000 + index), "Player " + index, index);
        full.Add(Key(9), "Before", 5);
        var crowded = new MemoryDataFiles();
        crowded.Contents[BanList.FilePath] = full.Serialize();
        crowded.FailReadsOf.Add(BanList.FilePath);
        var crowdedStore = new BanStore(crowded);
        crowdedStore.EnsureReadAsync().GetAwaiter().GetResult();
        crowdedStore.Record.Add(Key(9), "After", 6);
        crowdedStore.Record.Add(Key(10), "Newest", 7);
        crowdedStore.MarkChanged();
        crowded.FailReadsOf.Clear();
        crowdedStore.EnsureReadAsync().GetAwaiter().GetResult();
        var kept = crowdedStore.Record.Records();
        That(kept.Count == BanList.MaximumEntries && kept[^2].Identity == Key(9) && kept[^2].Name == "After" && kept[^1].Identity == Key(10) &&
             !crowdedStore.Record.IsBanned(Key(1_001)) && crowdedStore.Record.IsBanned(Key(1_002)),
            "a ban held for a player the file bans too keeps the held one, and the cap drops the file's oldest bans first");
    }

    internal static void ClosingWritesNothingOverAnUnreadableList()
    {
        using var rig = new LiveRig(189);
        var original = List((Key(1), "First"));
        rig.HostFiles.Contents[BanList.FilePath] = original;
        rig.HostFiles.FailReadsOf.Add(BanList.FilePath);
        var live = rig.Host.Live!;
        live.PrepareAsync();
        That(rig.Run(() => live.IsReady), "the identity loads although the ban list cannot be read");
        That(!live.BansFile.IsRead && live.Bans.Count == 0 && !live.Unban(Key(1)), "the list stays unread, not empty, and a ban it may hold cannot be lifted");

        // A ban is held, and the game closes before the list could be read: the file it could not read is left as it is.
        live.BansFile.Record.Add(Key(2), "Held", 2);
        live.BansFile.MarkChanged();
        rig.Host.Dispose();
        lock (rig.HostFiles.Contents)
        {
            That(Holds(rig.HostFiles, BanList.FilePath, original) && !HoldsAnyCopy(rig.HostFiles), "closing writes nothing over a list it could not read");
        }

        // A provider that starts once the file reads again finds every ban.
        using var later = new LiveRig(190);
        later.HostFiles.Contents[BanList.FilePath] = original;
        var laterLive = later.Host.Live!;
        laterLive.PrepareAsync();
        That(later.Run(() => laterLive.IsReady) && laterLive.BansFile.IsRead && laterLive.Bans.Count == 1 && laterLive.Bans[0].Identity == Key(1),
            "the bans come back as soon as the file reads");
    }

    internal static void ADamagedListIsSalvagedAndKeptAside()
    {
        // A corrupt list holds nothing that reads.
        var files = new MemoryDataFiles();
        var corrupt = Encoding.UTF8.GetBytes("{\"bans\":[{\"bannedAt\":1,");
        files.Contents[BanList.FilePath] = corrupt;
        var store = new BanStore(files);
        That(store.EnsureReadAsync().GetAwaiter().GetResult() && store.IsRead && store.Record.Count == 0, "a corrupt list reads as no bans");

        // Its bytes are copied aside before anything replaces them, and while the copy cannot be written, nothing does.
        store.Record.Add(Key(1), "First", 1);
        store.MarkChanged();
        files.FailWritesOf.Add(BanStore.DamagedPaths[0]);
        That(Faults(store.SaveAfter(Task.CompletedTask)) && Holds(files, BanList.FilePath, corrupt) && !HoldsAnyCopy(files),
            "without its copy, the damaged file stays where it is");
        files.FailWritesOf.Clear();
        var order = new List<string>();
        files.BeforeWrite = path =>
        {
            lock (order) order.Add(path);
            return Task.CompletedTask;
        };
        store.Record.Add(Key(2), "Second", 2);
        store.MarkChanged();
        store.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        That(order.SequenceEqual(new[] { BanStore.DamagedPaths[0], BanList.FilePath }) && Holds(files, BanStore.DamagedPaths[0], corrupt),
            "the next save writes the copy first, byte for byte, then the list (" + string.Join(", ", order) + ")");
        That(BanList.Parse(files.Contents[BanList.FilePath]).Count == 2, "the list holds every ban made since");
        order.Clear();
        store.Record.Add(Key(3), "Third", 3);
        store.MarkChanged();
        store.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        That(order.SequenceEqual(new[] { BanList.FilePath }) && Holds(files, BanStore.DamagedPaths[0], corrupt), "the copy is made once");

        // A partly damaged list keeps what reads: a ban that reads still bans, the one that does not is dropped, and the
        // whole file is kept aside first.
        var partly = Encoding.UTF8.GetBytes(
            "{\"bans\":[{\"bannedAt\":1,\"identity\":\"" + Key(4) + "\",\"name\":\"Kept\"},{\"bannedAt\":2,\"identity\":\"XYZ\",\"name\":\"Dropped\"}," +
            "{\"bannedAt\":3,\"identity\":\"" + Key(5) + "\",\"name\":\"Also kept\"}],\"schema\":\"topiaforge.bans/1\"}");
        var damagedFiles = new MemoryDataFiles();
        damagedFiles.Contents[BanList.FilePath] = partly;
        var damagedStore = new BanStore(damagedFiles);
        damagedStore.EnsureReadAsync().GetAwaiter().GetResult();
        That(damagedStore.Record.Count == 2 && damagedStore.Record.IsBanned(Key(4)) && damagedStore.Record.IsBanned(Key(5)),
            "a partly damaged list keeps every ban that reads, and drops the one that does not");
        damagedStore.Record.Add(Key(6), "Added", 6);
        damagedStore.MarkChanged();
        damagedStore.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        var salvaged = BanList.Parse(damagedFiles.Contents[BanList.FilePath]);
        That(Holds(damagedFiles, BanStore.DamagedPaths[0], partly) && salvaged.Count == 3 && salvaged.IsBanned(Key(4)) && salvaged.IsBanned(Key(6)),
            "the save keeps the whole file aside, then writes what reads and what was added");

        // A list of another schema is set aside the same way, never guessed at; an intact one is never copied.
        var future = Encoding.UTF8.GetBytes("{\"bans\":[],\"schema\":\"topiaforge.bans/2\"}");
        var futureFiles = new MemoryDataFiles();
        futureFiles.Contents[BanList.FilePath] = future;
        var futureStore = new BanStore(futureFiles);
        futureStore.EnsureReadAsync().GetAwaiter().GetResult();
        futureStore.MarkChanged();
        futureStore.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        That(futureStore.Record.Count == 0 && Holds(futureFiles, BanStore.DamagedPaths[0], future), "a list of another schema holds no bans this game reads, and is kept aside");
        var intactFiles = new MemoryDataFiles();
        intactFiles.Contents[BanList.FilePath] = List((Key(1), "First"));
        var intactStore = new BanStore(intactFiles);
        intactStore.EnsureReadAsync().GetAwaiter().GetResult();
        intactStore.Record.Add(Key(2), "Second", 2);
        intactStore.MarkChanged();
        intactStore.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        That(!HoldsAnyCopy(intactFiles) && BanList.Parse(intactFiles.Contents[BanList.FilePath]).Count == 2, "an intact list is saved without a copy");
    }

    internal static void ACopyKeptBeforeIsNeverWrittenOver()
    {
        // Each damage takes the first place that holds no copy, so a second damage never replaces the first one's copy.
        var files = new MemoryDataFiles();
        var damages = new List<byte[]>();
        for (var round = 1; round <= BanStore.DamagedPaths.Count + 1; round++)
        {
            var damage = Encoding.UTF8.GetBytes("{\"bans\":[" + round + "],\"schema\":\"topiaforge.bans/1\"}");
            damages.Add(damage);
            files.Contents[BanList.FilePath] = damage;
            var store = new BanStore(files);
            store.EnsureReadAsync().GetAwaiter().GetResult();
            store.Record.Add(Key(round), "Player", round);
            store.MarkChanged();
            store.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
            That(BanList.Parse(files.Contents[BanList.FilePath]).IsBanned(Key(round)), "damage " + round + " is salvaged and the list saved");
        }

        for (var place = 0; place < BanStore.DamagedPaths.Count; place++)
        {
            That(Holds(files, BanStore.DamagedPaths[place], damages[place]), "copy " + (place + 1) + " still holds damage " + (place + 1) + ", byte for byte");
        }

        That(!files.Contents.Values.Any(bytes => bytes.SequenceEqual(damages[^1])),
            "once every place holds a copy, a later damage is salvaged without one, and no copy is written over");

        // A place that cannot be read just now may hold a copy, so it is passed over, never written.
        var held = new MemoryDataFiles();
        var first = Encoding.UTF8.GetBytes("first damage");
        held.Contents[BanStore.DamagedPaths[0]] = first;
        held.FailReadsOf.Add(BanStore.DamagedPaths[0]);
        var corrupt = Encoding.UTF8.GetBytes("not a list");
        held.Contents[BanList.FilePath] = corrupt;
        var heldStore = new BanStore(held);
        heldStore.EnsureReadAsync().GetAwaiter().GetResult();
        heldStore.MarkChanged();
        heldStore.SaveAfter(Task.CompletedTask).GetAwaiter().GetResult();
        That(Holds(held, BanStore.DamagedPaths[0], first) && Holds(held, BanStore.DamagedPaths[1], corrupt),
            "a copy that cannot be read is kept, and the damage takes the next place");
    }

    internal static void ASaveQueuedBehindAFailedCopyKeepsTheDamagedList()
    {
        // The copy of a damaged list fails while another save is already queued behind it. That save tries the copy again
        // first, so no save ever writes the list over the damaged file before its bytes are kept aside.
        var files = new MemoryDataFiles();
        var corrupt = Encoding.UTF8.GetBytes("not a list");
        files.Contents[BanList.FilePath] = corrupt;
        var store = new BanStore(files);
        That(store.EnsureReadAsync().GetAwaiter().GetResult() && store.Record.Count == 0, "a corrupt list reads as no bans");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeWrite = path => path == BanStore.DamagedPaths[0] ? held.Task : Task.CompletedTask;
        files.FailWritesOf.Add(BanStore.DamagedPaths[0]);
        store.Record.Add(Key(1), "First", 1);
        store.MarkChanged();
        var first = store.SaveAfter(Task.CompletedTask);
        That(SpinWait.SpinUntil(() => files.HeldWrites == 1, 5_000), "the copy is being written");
        store.Record.Add(Key(2), "Second", 2);
        store.MarkChanged();
        var second = store.SaveAfter(first);
        held.SetResult();
        That(Faults(first) && Faults(second) && Holds(files, BanList.FilePath, corrupt) && !HoldsAnyCopy(files),
            "while the copy cannot be written, no save writes the list over the damaged file, a save queued meanwhile included");

        // Once the copy can be written, the next save keeps the damage aside first, then saves every ban.
        files.FailWritesOf.Clear();
        files.BeforeWrite = null;
        store.Record.Add(Key(3), "Third", 3);
        store.MarkChanged();
        store.SaveAfter(second).GetAwaiter().GetResult();
        That(Holds(files, BanStore.DamagedPaths[0], corrupt) && BanList.Parse(files.Contents[BanList.FilePath]).Count == 3,
            "then the damaged list is copied aside, and the list saved with every ban");
    }

    internal static void AHostThatCannotReadItsListWipesItsKey()
    {
        // A password host derives its key while the list is read. Hosting that fails because the list cannot be read wipes
        // that key, as hosting that opens does once the host's policy has taken its own copy.
        using var rig = new LiveRig(191);
        Register(rig.Host);
        rig.HostFiles.FailReadsOf.Add(BanList.FilePath);
        var live = rig.Host.Live!;
        live.StartHosting(new HostSettings("Locked") { Port = 0, Password = "open sesame" });
        var refused = DerivedKey(live);
        That(refused.Any(value => value != 0), "the host derives its password key");
        That(rig.Run(() => live.HostPhase == HostPhase.Failed) && live.HostFailure == LiveFailure.BansUnreadable && rig.HostFactory.Networks.Count == 0,
            "hosting fails before anything binds (" + live.HostFailure + ")");
        That(refused.All(value => value == 0), "and the key it derived is wiped");

        // Once the list reads, hosting opens, and the key derived for it is wiped too.
        rig.HostFiles.FailReadsOf.Clear();
        live.StartHosting(new HostSettings("Locked") { Port = 0, Password = "open sesame" });
        var opened = DerivedKey(live);
        That(rig.Run(() => live.HostPhase == HostPhase.Hosting) && opened.All(value => value == 0), "hosting opens, and that key is wiped as well");
    }

    /// <summary>The password key a pending host derived, read before the host takes its own copy.</summary>
    private static byte[] DerivedKey(LiveSessions live)
    {
        var field = typeof(LiveSessions).GetField("hostKey", BindingFlags.NonPublic | BindingFlags.Instance) ??
                    throw new InvalidOperationException("Assertion failed: live sessions hold the key being derived");
        return ((Task<byte[]?>)field.GetValue(live)!).GetAwaiter().GetResult() ??
               throw new InvalidOperationException("Assertion failed: a password host derives a key");
    }

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
        That(LocalIdentity.TryParse(files.Contents[IdentityStore.IdentityPath].ToArray(), out var identity, out _), "the seeded identity reads");
        using (identity) return identity!.Fingerprint;
    }

    private static bool Holds(MemoryDataFiles files, string path, byte[] expected) =>
        files.Contents.TryGetValue(path, out var bytes) && bytes.SequenceEqual(expected);

    private static bool HoldsAnyCopy(MemoryDataFiles files) => BanStore.DamagedPaths.Any(files.Contents.ContainsKey);

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

    private static void Register(IMultiplayerSession session)
    {
        if (!session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)).TryGetValue(out _))
        {
            throw new InvalidOperationException("Assertion failed: the score registers");
        }
    }
}
