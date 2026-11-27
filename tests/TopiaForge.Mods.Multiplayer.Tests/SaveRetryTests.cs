extern alias provider;

using System.Runtime.CompilerServices;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A save of the provider's own records that failed is tried again, the ban list's and the known-hosts record's alike: at
/// the first frame once a pause of five seconds is over, never every frame, and once more as the game closes. A frame that
/// waits out the pause costs nothing.
/// </summary>
internal static class SaveRetryTests
{
    private const string Pinned = "192.168.1.20";
    private static readonly string ServerId = new('c', 64);

    internal static void AFailedSaveIsTriedAgainAfterAPause()
    {
        foreach (var record in Records())
        {
            using var rig = Ready(record, 195);
            var live = rig.Host.Live!;
            var attempts = CountAttempts(rig, record.Path);

            // The disk refuses the save, then recovers: nothing is tried again before the pause is over, and then the change lands.
            rig.HostFiles.FailWritesOf.Add(record.Path);
            record.Change(live);
            That(rig.Run(() => attempts() == 1) && !record.Landed(rig.HostFiles), record.Name + ": the save fails");
            rig.HostFiles.FailWritesOf.Clear();
            That(!rig.Run(() => attempts() > 1, BanStore.RetryMicros - 200_000) && !record.Landed(rig.HostFiles),
                record.Name + ": a failed save is not tried again before the pause is over (" + attempts() + " tries)");
            That(rig.Run(() => record.Landed(rig.HostFiles), 1_000_000) && attempts() == 2,
                record.Name + ": once it is over, the next frame tries again, and the change lands (" + attempts() + " tries)");

            // A disk that keeps failing is tried once a pause, not every frame: 20 s at a frame every 5 ms is 4,000 frames.
            rig.HostFiles.FailWritesOf.Add(record.Path);
            var before = attempts();
            record.ChangeAgain(live);
            rig.Run(() => false, 20_000_000);
            var tries = attempts() - before;
            That(tries >= 4 && tries <= 5, record.Name + ": a disk that keeps failing is tried once every five seconds (" + tries + " tries in 20 s)");

            // While the next try waits, a frame allocates nothing.
            var allocated = AllocationPasses.Fewest(() => Frames(rig.Host, 1_000));
            That(allocated == 0, record.Name + ": a frame that waits to try a failed save again allocates nothing (" + allocated + " bytes in 1,000 frames)");
        }
    }

    internal static void AFailedSaveIsTriedOnceMoreAtClose()
    {
        foreach (var record in Records())
        {
            // The save fails, the disk recovers, and the game closes before the pause is over: closing tries it once more.
            using (var rig = Ready(record, 196))
            {
                var attempts = CountAttempts(rig, record.Path);
                rig.HostFiles.FailWritesOf.Add(record.Path);
                record.Change(rig.Host.Live!);
                That(rig.Run(() => attempts() == 1) && !record.Landed(rig.HostFiles), record.Name + ": the save fails");
                rig.HostFiles.FailWritesOf.Clear();
                rig.Run(() => false, 1_000_000);
                rig.Host.Dispose();
                That(record.Landed(rig.HostFiles) && attempts() == 2, record.Name + ": closing tries a failed save once more, and it lands (" + attempts() + " tries)");
            }

            // A disk that keeps failing is tried once more at close, and no more.
            using (var rig = Ready(record, 197))
            {
                var attempts = CountAttempts(rig, record.Path);
                rig.HostFiles.FailWritesOf.Add(record.Path);
                record.Change(rig.Host.Live!);
                That(rig.Run(() => attempts() == 1), record.Name + ": the save fails");
                rig.Host.Dispose();
                That(attempts() == 2 && !record.Landed(rig.HostFiles), record.Name + ": a disk that keeps failing is tried once more at close (" + attempts() + " tries)");
            }
        }
    }

    /// <summary>Runs <paramref name="count"/> frames at one moment and returns what this thread allocated meanwhile.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Frames(Provider host, int count)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++) host.Pump();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>A host provider whose identity, ban list and known-hosts record have been read.</summary>
    private static LiveRig Ready(SavedRecord record, int seed)
    {
        var rig = new LiveRig(seed);
        record.Seed(rig.HostFiles);
        rig.Host.Live!.PrepareAsync();
        That(rig.Run(() => rig.Host.Live.IsReady) && rig.Host.Live.BansFile.IsRead && rig.Host.Live.KnownHostsFile.IsRead, record.Name + ": the records read");
        return rig;
    }

    private static Func<int> CountAttempts(LiveRig rig, string path)
    {
        var attempts = 0;
        rig.HostFiles.BeforeWrite = written =>
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
            live => That(live.Unban(Key(2)), "another ban is lifted"),
            files =>
            {
                lock (files.Contents) return files.Contents.TryGetValue(BanList.FilePath, out var bytes) && !BanList.Parse(bytes).IsBanned(Key(1));
            });
        yield return new SavedRecord(
            "the known-hosts record",
            KnownHosts.FilePath,
            _ => { },
            live => Remember(live, Pinned),
            live => Remember(live, "192.168.1.30"),
            files =>
            {
                lock (files.Contents)
                {
                    return files.Contents.TryGetValue(KnownHosts.FilePath, out var bytes) &&
                           KnownHosts.Parse(bytes).Check(Pinned, 27250, ServerId) == KnownHostCheck.Match;
                }
            });
    }

    private static void Remember(LiveSessions live, string host)
    {
        lock (live.KnownHosts) live.KnownHosts.Remember(host, 27250, ServerId, 100);
        live.MarkKnownHostsChanged();
    }

    /// <summary>A transport key in hexadecimal, made from <paramref name="seed"/>.</summary>
    private static string Key(int seed) => seed.ToString("x8", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, 'a');

    /// <summary>One of the provider's records, and how a test changes it and sees the change on disk.</summary>
    private sealed record SavedRecord(
        string Name,
        string Path,
        Action<MemoryDataFiles> Seed,
        Action<LiveSessions> Change,
        Action<LiveSessions> ChangeAgain,
        Func<MemoryDataFiles, bool> Landed);
}
