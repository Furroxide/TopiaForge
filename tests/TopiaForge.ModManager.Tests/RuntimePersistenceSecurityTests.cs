using System;
using System.IO;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class RuntimePersistenceSecurityTests
    {
        public static void Run(string root)
        {
            TestSiblingPrefixTraversalRejected(root);
            TestTamperedSerializedIdsCannotDeleteOutsidePackages(root);
            TestTamperedSelectedVersionCannotPrunePackages(root);
            TestAtomicStateRecoveryAndBounds(root);
        }

        private static void TestSiblingPrefixTraversalRejected(string root)
        {
            var parent = Path.Combine(root, "path-containment");
            var allowed = Path.Combine(parent, "mod");
            var sibling = Path.Combine(parent, "mod-sibling");
            Directory.CreateDirectory(allowed);
            Directory.CreateDirectory(sibling);

            var siblingPayload = Path.Combine(sibling, "payload.txt");
            var relativeSibling = Path.GetRelativePath(allowed, siblingPayload);
            Throws<InvalidOperationException>(
                () => PathSafety.CombineRelativeChild(allowed, relativeSibling),
                "a sibling whose name starts with the root name must not pass containment");

            var child = PathSafety.CombineRelativeChild(allowed, Path.Combine("nested", "payload.txt"));
            Assert(PathSafety.IsSameOrChild(allowed, child), "an ordinary child path should remain available");
        }

        private static void TestTamperedSerializedIdsCannotDeleteOutsidePackages(string root)
        {
            TestScanRejectsTamperedId(root);
            TestImmediateUninstallRejectsTamperedId(root);
            TestPendingUninstallRejectsTamperedId(root);
        }

        private static void TestScanRejectsTamperedId(string root)
        {
            var paths = NewPaths(root, "tampered-scan");
            var unsafeId = "../scan-outside";
            var outside = CreateSentinel(paths.Root, "scan-outside");
            var state = RoundTripState(paths.StateFile, TamperedState(unsafeId, uninstallPending: false));

            var packageDirectory = paths.GetPackagePath("innocent.mod", "1.0.0");
            Directory.CreateDirectory(packageDirectory);
            File.WriteAllText(
                Path.Combine(packageDirectory, "topiaforge.mod.json"),
                JsonUtil.Serialize(new ModManifest
                {
                    SchemaVersion = 3,
                    Id = unsafeId,
                    Name = "Tampered",
                    Version = "1.0.0",
                    EntryAssembly = "Tampered.dll",
                    EntryType = "Tampered.Entry"
                }));

            var packages = new ModRegistry().Scan(paths, state);

            Assert(File.Exists(outside), "scan must not touch a sibling selected by a tampered state id");
            Assert(state.Find(unsafeId) == null, "scan should remove unsafe serialized ids from runtime state");
            Assert(packages.Count == 1 && !packages[0].IsValid,
                "an on-disk manifest with an unsafe id should remain visible as invalid without recreating state");
        }

        private static void TestImmediateUninstallRejectsTamperedId(string root)
        {
            var paths = NewPaths(root, "tampered-immediate-uninstall");
            var unsafeId = "../immediate-outside";
            var outside = CreateSentinel(paths.Root, "immediate-outside");
            var state = RoundTripState(paths.StateFile, TamperedState(unsafeId, uninstallPending: false));

            var removed = new ModRegistry().RemoveInstalledPackage(paths, state, unsafeId);

            Assert(removed, "the unsafe state record should be removable");
            Assert(File.Exists(outside), "immediate uninstall must not delete outside the package root");
            Assert(state.Find(unsafeId) == null, "immediate uninstall should discard the unsafe state record");
        }

        private static void TestPendingUninstallRejectsTamperedId(string root)
        {
            var paths = NewPaths(root, "tampered-pending-uninstall");
            var unsafeId = "../pending-outside";
            var outside = CreateSentinel(paths.Root, "pending-outside");
            var state = RoundTripState(paths.StateFile, TamperedState(unsafeId, uninstallPending: true));

            new ModRegistry().ApplyPendingUninstalls(paths, state);

            Assert(File.Exists(outside), "pending uninstall must not delete outside the package root");
            Assert(state.Find(unsafeId) == null, "pending uninstall should discard the unsafe state record");
        }

        private static void TestTamperedSelectedVersionCannotPrunePackages(string root)
        {
            var paths = NewPaths(root, "tampered-prune-version");
            var package = paths.GetPackagePath("safe.mod", "1.0.0");
            Directory.CreateDirectory(package);
            var outside = CreateSentinel(paths.Root, "prune-outside");
            var state = new ManagerState();
            state.Mods.Add(new InstalledModState
            {
                Id = "safe.mod",
                Name = "Safe",
                Version = "../../prune-outside",
                Enabled = true
            });
            state = RoundTripState(paths.StateFile, state);

            new ModRegistry().PruneSupersededVersions(paths, state);

            Assert(Directory.Exists(package), "an unsafe selected version must not cause valid versions to be pruned");
            Assert(File.Exists(outside), "pruning must not touch a path selected by tampered state");
        }

        private static void TestAtomicStateRecoveryAndBounds(string root)
        {
            var directory = Path.Combine(root, "json-persistence");
            Directory.CreateDirectory(directory);
            var statePath = Path.Combine(directory, "state.json");
            var first = State("persist.mod", "First");
            var current = State("persist.mod", "Current");
            var latest = State("persist.mod", "Latest");

            JsonUtil.SaveFile(statePath, first);
            JsonUtil.SaveFile(statePath, current);
            JsonUtil.SaveFile(statePath, latest);
            var backupPath = statePath + JsonUtil.BackupSuffix;
            Assert(File.Exists(backupPath), "replacing state should retain the previous document as a backup");
            Assert(JsonUtil.LoadFile(backupPath, new ManagerState()).Find("persist.mod")?.Name == "Current",
                "the backup should contain the previous complete state");

            File.WriteAllText(statePath, "{\"mods\":[");
            var recovered = JsonUtil.LoadPersistentFile(statePath, new ManagerState());
            Assert(recovered.Find("persist.mod")?.Name == "Current",
                "a truncated primary state should recover from the last-known-good backup");

            File.Delete(statePath);
            var interruptedTemp = statePath + ".tmp-interrupted";
            File.WriteAllText(interruptedTemp, "partial");
            recovered = JsonUtil.LoadPersistentFile(statePath, new ManagerState());
            Assert(recovered.Find("persist.mod")?.Name == "Current",
                "a missing primary plus an interrupted temp file should recover from the backup");
            File.Delete(interruptedTemp);

            JsonUtil.SaveFile(statePath, latest);
            var oversized = State(
                "persist.mod",
                new string('x', checked((int)JsonUtil.MaxPersistedFileBytes + 1024)));
            Throws<InvalidDataException>(
                () => JsonUtil.SaveFile(statePath, oversized),
                "oversized serialized state should be rejected before replacing the current file");
            Assert(JsonUtil.LoadPersistentFile(statePath, new ManagerState()).Find("persist.mod")?.Name == "Latest",
                "a failed oversized save must preserve the current complete state");
            Assert(!Directory.GetFiles(directory, "state.json.tmp-*", SearchOption.TopDirectoryOnly).Any(),
                "failed state writes should not leave active temp files behind");

            var oversizedPath = Path.Combine(directory, "oversized.json");
            using (var stream = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(JsonUtil.MaxPersistedFileBytes + 1);
                stream.Flush(flushToDisk: true);
            }

            Throws<InvalidDataException>(
                () => JsonUtil.LoadFile(oversizedPath, new ManagerState()),
                "persisted JSON reads should be bounded before deserialization");
        }

        private static ManagerPaths NewPaths(string root, string name)
        {
            var paths = new ManagerPaths(Path.Combine(root, name, "BepInEx"));
            paths.EnsureCreated();
            return paths;
        }

        private static string CreateSentinel(string root, string name)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, "keep.txt");
            File.WriteAllText(marker, "keep");
            return marker;
        }

        private static ManagerState RoundTripState(string path, ManagerState state)
        {
            JsonUtil.SaveFile(path, state);
            return JsonUtil.LoadPersistentFile(path, new ManagerState());
        }

        private static ManagerState TamperedState(string id, bool uninstallPending)
        {
            var state = new ManagerState();
            state.Mods.Add(new InstalledModState
            {
                Id = id,
                Name = "Tampered",
                Version = "1.0.0",
                Enabled = false,
                UninstallPending = uninstallPending
            });
            return state;
        }

        private static ManagerState State(string id, string name)
        {
            var state = new ManagerState();
            state.Mods.Add(new InstalledModState
            {
                Id = id,
                Name = name,
                Version = "1.0.0",
                Enabled = true
            });
            return state;
        }

        private static void Throws<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
