extern alias provider;

using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The provider's last saves land as the game closes, composed as the game composes it. TopiaForge's runtime detaches as
/// shutdown begins, then stops every mod, and a stopping mod's files refuse every operation before the provider itself
/// closes. So a save that failed shortly before quitting, and a change no frame has saved yet, are written as the runtime
/// detaches, the ban list's and the known-hosts record's alike.
/// </summary>
internal static class SaveOnCloseTests
{
    private const string Pinned = "192.168.1.20";
    private static readonly string ServerId = new('c', 64);

    internal static void TheLastSavesLandBeforeTheModsFilesStop()
    {
        var seed = 251;
        var lost = new List<string>();
        foreach (var record in Records())
            foreach (var mode in new[] { "failed", "unsaved" })
            {
                var label = record.Name + ", " + mode;
                using var rig = new ControlRig(seed++);
                var host = rig.Host;
                var live = host.Provider.Live!;
                record.Seed(host.Files);
                live.PrepareAsync();
                That(rig.Run(() => live.IsReady && live.BansFile.IsRead && live.KnownHostsFile.IsRead), label + ": the records read");
                var attempts = CountAttempts(host.Files, record.Path);
                if (mode == "failed")
                {
                    // The save fails, the disk recovers, and the game quits before the five-second pause is over.
                    host.Files.FailWritesOf.Add(record.Path);
                    record.Change(live);
                    That(rig.Run(() => attempts() == 1) && !record.Landed(host.Files), label + ": the save fails");
                    host.Files.FailWritesOf.Clear();
                    rig.Run(() => false, 1_000_000);
                }
                else
                {
                    // A change no frame has saved yet, as a pin recorded off the main thread just before quitting.
                    record.ChangeUnsaved(live);
                }

                // TopiaForge's shutdown: the runtime detaches; every mod stops, and its files refuse every operation, as the
                // SDK's do once a mod stops; then the mod's lifetime closes the control and, last, the provider.
                host.Composition.Disconnect();
                host.Files.FailReads = true;
                host.Files.FailWritesOf.Add(BanList.FilePath);
                host.Files.FailWritesOf.Add(KnownHosts.FilePath);
                host.Control.Dispose();
                host.Provider.Dispose();
                if (!record.Landed(host.Files)) lost.Add(label + " (" + attempts() + " tries)");
            }

        That(lost.Count == 0, "every change reaches the disk before the files stop; lost: " + string.Join(", ", lost));
    }

    private static Func<int> CountAttempts(MemoryDataFiles files, string path)
    {
        var attempts = 0;
        files.BeforeWrite = written =>
        {
            if (written == path) Interlocked.Increment(ref attempts);
            return Task.CompletedTask;
        };
        return () => Volatile.Read(ref attempts);
    }

    private static IEnumerable<SavedRecord> Records()
    {
        yield return new SavedRecord(
            "the ban list",
            BanList.FilePath,
            files =>
            {
                var list = new BanList();
                list.Add(Key(1), "First", 100);
                list.Add(Key(2), "Second", 101);
                files.Contents[BanList.FilePath] = list.Serialize();
            },
            live => That(live.Unban(Key(1)), "a ban is lifted"),
            live =>
            {
                // Lifting a ban saves at once; a change marked and not yet saved is one only closing can save.
                That(live.BansFile.Record.Remove(Key(1)), "a ban is lifted");
                live.BansFile.MarkChanged();
            },
            files =>
            {
                lock (files.Contents) return files.Contents.TryGetValue(BanList.FilePath, out var bytes) && !BanList.Parse(bytes).IsBanned(Key(1));
            });
        yield return new SavedRecord(
            "the known-hosts record",
            KnownHosts.FilePath,
            _ => { },
            Remember,
            Remember,
            files =>
            {
                lock (files.Contents)
                {
                    return files.Contents.TryGetValue(KnownHosts.FilePath, out var bytes) &&
                           KnownHosts.Parse(bytes).Check(Pinned, 27250, ServerId) == KnownHostCheck.Match;
                }
            });
    }

    private static void Remember(LiveSessions live)
    {
        lock (live.KnownHosts) live.KnownHosts.Remember(Pinned, 27250, ServerId, 100);
        live.MarkKnownHostsChanged();
    }

    /// <summary>A transport key in hexadecimal, made from <paramref name="seed"/>.</summary>
    private static string Key(int seed) => seed.ToString("x8", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, 'a');

    /// <summary>One of the provider's records: how a test seeds it, changes it with or without a frame's save, and sees it on disk.</summary>
    private sealed record SavedRecord(
        string Name,
        string Path,
        Action<MemoryDataFiles> Seed,
        Action<LiveSessions> Change,
        Action<LiveSessions> ChangeUnsaved,
        Func<MemoryDataFiles, bool> Landed);
}
