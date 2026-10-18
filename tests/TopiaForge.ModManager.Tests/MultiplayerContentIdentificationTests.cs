using System;
using System.IO;
using System.Linq;
using System.Threading;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Installed packages are identified by their receipts at the first look; packages without one are hashed off the main
    /// thread. Until that work finishes the content reports as pending, with progress in packages and bytes; reading it never
    /// hashes on the calling thread. A package that cannot be read reports as unreadable, naming the package and never its
    /// folder, and a retry identifies it again.
    /// </summary>
    internal static class MultiplayerContentIdentificationTests
    {
        internal static void Run(string root)
        {
            NothingIsHashedOnTheCallingThread(Path.Combine(root, "identify-pending"));
            ReceiptsIdentifyAPackageAtOnce(Path.Combine(root, "identify-receipt"));
            ProgressCountsPackagesAndBytes(Path.Combine(root, "identify-progress"));
            UnreadablePackagesStayUnreadableUntilRetried(Path.Combine(root, "identify-locked"));
            ChangedPackagesDropAStaleRun(Path.Combine(root, "identify-changed"));
            TheBackgroundThreadIdentifiesThePackages(Path.Combine(root, "identify-thread"));
            SettledReadinessAllocatesNothing(Path.Combine(root, "identify-alloc"));
            Console.WriteLine("MultiplayerContentIdentificationTests passed.");
        }

        private static void NothingIsHashedOnTheCallingThread(string root)
        {
            var work = new ManualWorkScheduler();
            var inventory = new MultiplayerContentInventory(work);
            inventory.Add(Manifest("io.example.devtools", "0.1.0"), Package(root, "devtools", 3, 1000));
            var readiness = inventory.Readiness;
            That(readiness.State == InternalContentState.Pending && readiness.PackagesTotal == 1 && work.Pending == 1,
                "the first look starts identifying on the scheduler and reports pending (" + readiness.State + ")");
            for (var read = 0; read < 100; read++)
            {
                Throws<InternalContentNotReadyException>(() => _ = inventory.Descriptors, "the description waits for identification");
                Throws<InternalContentNotReadyException>(() => _ = inventory.AdmissionMods, "and so does admission");
                That(inventory.Readiness.State == InternalContentState.Pending, "readiness stays pending");
            }

            That(work.Scheduled == 1 && !inventory.Packages[0].IsIdentified, "nothing ran on this thread, and only one identification was started");
            work.RunAll();
            That(inventory.Readiness.State == InternalContentState.Ready, "once the work ran, the content is ready");
            That(inventory.Descriptors.Single().PackageSha256 == MultiplayerContentInventory.PackageSha256(Path.Combine(root, "devtools")),
                "with the same hash the files always had");
        }

        private static void ReceiptsIdentifyAPackageAtOnce(string root)
        {
            var receipted = Directory.CreateDirectory(Path.Combine(root, "receipted")).FullName;
            File.WriteAllText(Path.Combine(receipted, "Mod.dll"), "code");
            JsonUtil.SaveFile(Path.Combine(receipted, PackageInstallReceipt.FileName), new PackageInstallReceipt { SourceSha256 = new string('d', 64) });
            var work = new ManualWorkScheduler();
            var inventory = new MultiplayerContentInventory(work);
            inventory.Add(Manifest("io.example.receipted", "1.0.0"), receipted);
            That(inventory.Readiness.State == InternalContentState.Ready && work.Scheduled == 0,
                "an installed package is identified by its receipt at the first look, and nothing waits for the background");
            That(inventory.Descriptors.Single().PackageSha256 == new string('d', 64), "by the archive hash its receipt records");

            inventory.Add(Manifest("io.example.copied", "0.1.0"), Package(root, "copied", 2, 100));
            var mixed = inventory.Readiness;
            That(mixed.State == InternalContentState.Pending && mixed.PackagesTotal == 1 && work.Pending == 1,
                "beside it, only the package without a receipt is left to hash (" + mixed.PackagesTotal + ")");
            work.RunAll();
            That(inventory.Readiness.State == InternalContentState.Ready && inventory.Descriptors.Count == 2, "and both are described once it is hashed");
        }

        private static void ProgressCountsPackagesAndBytes(string root)
        {
            var receipted = Directory.CreateDirectory(Path.Combine(root, "receipted")).FullName;
            File.WriteAllText(Path.Combine(receipted, "Mod.dll"), "code");
            JsonUtil.SaveFile(Path.Combine(receipted, PackageInstallReceipt.FileName), new PackageInstallReceipt { SourceSha256 = new string('c', 64) });
            var work = new ManualWorkScheduler();
            var inventory = new MultiplayerContentInventory(work);
            inventory.Add(Manifest("io.example.receipted", "1.0.0"), receipted);
            inventory.Add(Manifest("io.example.copied", "0.1.0"), Package(root, "copied", 4, 70_000));
            _ = inventory.Readiness;
            var identification = new MultiplayerContentIdentification(7, new[] { receipted, Path.Combine(root, "copied") }, new[] { "Receipted 1.0.0", "Copied 0.1.0" });
            var before = identification.Progress;
            That(before.State == InternalContentState.Pending && before.PackagesDone == 0 && before.PackagesTotal == 2 && before.BytesTotal == 0,
                "before the work runs, nothing is counted");
            identification.Run();
            var after = identification.Progress;
            That(identification.IsCompleted && identification.Failure == null && after.PackagesDone == 2 && after.BytesTotal == 280_000 && after.BytesDone == 280_000,
                "a receipt counts a package without hashing it, and listed files count their bytes as they hash (" + after.PackagesDone + ", " + after.BytesDone + "/" + after.BytesTotal + ")");
            That(identification.HashOf(0) == new string('c', 64) && identification.HashOf(1) == MultiplayerContentInventory.PackageSha256(Path.Combine(root, "copied")),
                "the receipt's hash and the files' hash are the ones the inventory always used");
            work.RunAll();
            That(inventory.Readiness.State == InternalContentState.Ready && inventory.Descriptors.Count == 2, "the inventory's own run identifies both");
        }

        private static void UnreadablePackagesStayUnreadableUntilRetried(string root)
        {
            var folder = Package(root, "devtools", 1, 4096);
            var work = new ManualWorkScheduler();
            var inventory = new MultiplayerContentInventory(work);
            inventory.Add(Manifest("io.example.devtools", "0.1.0"), folder);
            using (new FileStream(Path.Combine(folder, "file0.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                _ = inventory.Readiness;
                work.RunAll();
                That(inventory.Readiness.State == InternalContentState.Unreadable && inventory.Readiness.State == InternalContentState.Unreadable,
                    "a package that cannot be read stays unreadable however often readiness is read");
                That(work.Pending == 0, "and reading readiness does not start the work again");
                IOException? failure = null;
                try
                {
                    _ = inventory.Descriptors;
                }
                catch (IOException exception)
                {
                    failure = exception;
                }

                That(failure != null && failure.Message == "Devtools 0.1.0 could not be read to identify it for multiplayer (IOException)." &&
                     failure.InnerException is IOException && !failure.Message.Contains(root, StringComparison.OrdinalIgnoreCase),
                    "the description says which package, never its folder (" + failure?.Message + ")");
                That(inventory.Readiness.State == InternalContentState.Pending && work.Pending == 1, "and the read after the failure starts identifying again");
                work.RunAll();
                That(inventory.Readiness.State == InternalContentState.Unreadable, "which fails again while the file is held");
            }

            inventory.RetryIdentification();
            That(inventory.Readiness.State == InternalContentState.Pending && work.Pending == 1, "a retry identifies again");
            work.RunAll();
            That(inventory.Readiness.State == InternalContentState.Ready && inventory.Descriptors.Count == 1, "and succeeds once the file is free");
        }

        private static void ChangedPackagesDropAStaleRun(string root)
        {
            var work = new ManualWorkScheduler();
            var inventory = new MultiplayerContentInventory(work);
            inventory.Add(Manifest("io.example.first", "1.0.0"), Package(root, "first", 1, 10));
            _ = inventory.Readiness;
            inventory.Add(Manifest("io.example.second", "1.0.0"), Package(root, "second", 1, 20));
            work.RunAll();
            That(inventory.Readiness.State == InternalContentState.Pending, "a run that started before the packages changed is dropped, and a new one starts");
            That(!inventory.Packages.Any(package => package.IsIdentified), "the stale run's hashes are not adopted");
            work.RunAll();
            That(inventory.Readiness.State == InternalContentState.Ready && inventory.Descriptors.Select(item => item.Id).SequenceEqual(new[] { "io.example.first", "io.example.second" }),
                "the new run identifies every package");
        }

        private static void TheBackgroundThreadIdentifiesThePackages(string root)
        {
            var inventory = new MultiplayerContentInventory(MultiplayerWorkSchedulers.Background);
            inventory.Add(Manifest("io.example.threaded", "1.0.0"), Package(root, "threaded", 8, 200_000));
            _ = inventory.Readiness;
            var done = SpinWait.SpinUntil(() => inventory.Readiness.State != InternalContentState.Pending, TimeSpan.FromSeconds(30));
            That(done && inventory.Readiness.State == InternalContentState.Ready, "the production scheduler identifies the package on its own thread");
            That(inventory.Descriptors.Single().PackageSha256 == MultiplayerContentInventory.PackageSha256(Path.Combine(root, "threaded")), "with the same hash");
        }

        private static void SettledReadinessAllocatesNothing(string root)
        {
            var work = new ManualWorkScheduler();
            var inventory = new MultiplayerContentInventory(work);
            inventory.Add(Manifest("io.example.settled", "1.0.0"), Package(root, "settled", 1, 10));
            _ = inventory.Readiness;
            work.RunAll();
            _ = inventory.Readiness;

            // Measured as the other frame checks are, so the runtime's own once-only work on this thread is not counted:
            // measured in one pass, it failed about one run in twelve with 49,200 bytes while the machine was busy.
            var notReady = 0;
            var allocated = Seam.Allocated(() =>
            {
                if (inventory.Readiness.State != InternalContentState.Ready) notReady++;
            });
            That(notReady == 0 && allocated == 0, "reading settled readiness every frame allocates nothing (" + allocated + " bytes)");
        }

        private static string Package(string root, string name, int files, int bytes)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            for (var index = 0; index < files; index++) File.WriteAllBytes(Path.Combine(folder, "file" + index + ".bin"), new byte[bytes]);
            return folder;
        }

        private static ModManifest Manifest(string id, string version)
        {
            var last = id.Substring(id.LastIndexOf('.') + 1);
            return new ModManifest { SchemaVersion = 6, Id = id, Name = char.ToUpperInvariant(last[0]) + last.Substring(1), Version = version };
        }
    }
}
