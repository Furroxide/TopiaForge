using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A game started without the launcher selects versions from the manager state. The record of server content the
    /// launcher writes keeps what joins installed for the player's own mods out of that selection, and a record that
    /// cannot be used freezes it at what loaded before, so a server's version never becomes the player's own play.
    /// </summary>
    internal static class ServerContentSelectionTests
    {
        private const string ModId = "server.content.mod";
        private const string FixtureAssembly = "TopiaForge.ValidTestMod.dll";
        private const string FixtureType = "TopiaForge.ValidTestMod.ValidMod";

        public static void Run(string root)
        {
            var archives = Path.Combine(root, "server-content-archives");
            Directory.CreateDirectory(archives);
            var cases = new (string Name, Action<ManagerPaths, string> Test)[]
            {
                ("a server's hidden version is not the player's", HiddenVersionIsNotSelected),
                ("an exact pin selects a hidden version", PinSelectsHiddenVersion),
                ("a version installed again is the player's", ReinstalledVersionIsSelectable),
                ("a withheld version is not the player's", WithheldVersionIsNotSelected),
                ("a mod with only server versions offers nothing", ServerOnlyModIsNotOffered),
                ("a mod a join added starts disabled", AddedModStartsDisabled),
                ("a damaged record keeps what loaded before", DamagedRecordFreezesSelection),
                ("a newer record keeps what loaded before", NewerRecordFreezesSelection),
                ("a folder in the record's place keeps what loaded before", FolderRecordFreezesSelection),
                ("an unreadable record keeps what loaded before", UnreadableRecordFreezesSelection),
                ("a backup a stopped write left is read", BackupRecordIsRead),
                ("a damaged backup keeps what loaded before", DamagedBackupFreezesSelection),
                ("a record set aside as damaged, with none beside it, keeps what loaded before", SetAsideRecordFreezesSelection),
                ("a copy only the player named is no damaged record", ForeignCopyDoesNotFreezeSelection),
                ("an unreadable receipt withholds a version the record names", UnreadableReceiptWithholds),
            };
            var index = 0;
            foreach (var (name, test) in cases)
            {
                var paths = new ManagerPaths(Path.Combine(root, "server-content-" + index++, "BepInEx"));
                paths.EnsureCreated();
                try
                {
                    test(paths, archives);
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException("Server content selection test '" + name + "' failed: " + error.Message, error);
                }
            }
        }

        private static void HiddenVersionIsNotSelected(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            Assert(Selected(paths, state, ServerContentSelection.None) == "2.0.0",
                "without a record the highest version is selected, as before");
            state.Find(ModId)!.Version = "1.0.0";
            WriteRecord(paths, Versions(Entry("2.0.0", Receipt(paths, "2.0.0"), hidden: true)));
            var registry = new ModRegistry();
            var package = registry.Scan(paths, state).Single();
            Assert(package.IsValid && package.Manifest!.Version == "1.0.0",
                "the server's newer version stays out of the player's own play");
            Assert(state.Find(ModId)!.Version == "1.0.0", "the manager state keeps the player's version");
            Assert(!registry.LastServerContent.Frozen, "a valid record does not freeze selection");
            Assert(Directory.Exists(paths.GetPackagePath(ModId, "2.0.0")), "the server's version stays installed");
        }

        private static void PinSelectsHiddenVersion(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            WriteRecord(paths, Versions(Entry("2.0.0", Receipt(paths, "2.0.0"), hidden: true)));
            var mod = state.Find(ModId)!;
            mod.VersionPinned = true;
            mod.Version = "2.0.0";
            var package = new ModRegistry().Scan(paths, state).Single();
            Assert(package.IsValid && package.Manifest!.Version == "2.0.0",
                "an exact pin, as a server profile's launch writes, selects the hidden version");
        }

        private static void ReinstalledVersionIsSelectable(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            state.Find(ModId)!.Version = "1.0.0";
            WriteRecord(paths, Versions(Entry("2.0.0", new string('e', 64), hidden: true)));
            Assert(Selected(paths, state) == "2.0.0",
                "a version whose receipt is not the join's was installed again by the player, so it is theirs");
        }

        private static void WithheldVersionIsNotSelected(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            state.Find(ModId)!.Version = "1.0.0";
            WriteRecord(paths, Record(withheld: new[] { Withheld("2.0.0", Receipt(paths, "2.0.0")) }));
            Assert(Selected(paths, state) == "1.0.0", "a withheld version stays out of the player's own play");
        }

        private static void ServerOnlyModIsNotOffered(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "2.0.0");
            WriteRecord(paths, Versions(Entry("2.0.0", Receipt(paths, "2.0.0"), hidden: true)));
            Assert(new ModRegistry().Scan(paths, state).Count == 0,
                "a mod whose every installed version a server keeps offers nothing to the player's own play");
            state.Find(ModId)!.VersionPinned = true;
            state.Find(ModId)!.Version = "2.0.0";
            Assert(Selected(paths, state) == "2.0.0", "an exact pin still selects it");
        }

        private static void AddedModStartsDisabled(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0");
            state.Remove(ModId);
            WriteRecord(paths, Record(new[] { Entry("1.0.0", Receipt(paths, "1.0.0"), hidden: false) }, added: new[] { ModId }));
            var package = new ModRegistry().Scan(paths, state).Single();
            Assert(package.IsValid && package.Manifest!.Version == "1.0.0", "a mod new to the player stays visible");
            Assert(!package.IsEnabled && !state.Find(ModId)!.Enabled,
                "a mod a join added starts disabled even when its manager record was lost");

            var control = new ManagerState();
            var controlPackage = new ModRegistry().Scan(paths, control, ManifestValidationContext.Current, ServerContentSelection.None).Single();
            Assert(controlPackage.IsEnabled, "without the record the same mod starts enabled, as before");
        }

        private static void DamagedRecordFreezesSelection(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            state.Find(ModId)!.Version = "1.0.0";
            File.WriteAllText(RecordPath(paths), "{\"schemaVersion\":1,\"versions\":[");
            AssertFrozenAtOldVersion(paths, state);
        }

        private static void NewerRecordFreezesSelection(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            state.Find(ModId)!.Version = "1.0.0";
            File.WriteAllText(RecordPath(paths), "{\"schemaVersion\":2}");
            AssertFrozenAtOldVersion(paths, state);
        }

        private static void FolderRecordFreezesSelection(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            state.Find(ModId)!.Version = "1.0.0";
            Directory.CreateDirectory(RecordPath(paths));
            AssertFrozenAtOldVersion(paths, state);
        }

        private static void UnreadableRecordFreezesSelection(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            state.Find(ModId)!.Version = "1.0.0";
            WriteRecord(paths, Record());
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            // Windows refuses every other open of a file held without sharing, as another program can hold it.
            using (new FileStream(RecordPath(paths), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                AssertFrozenAtOldVersion(paths, state);
                Assert(new ModRegistry().Scan(paths, state).Count == 1, "frozen selection still offers the mod");
            }
        }

        private static void BackupRecordIsRead(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            state.Find(ModId)!.Version = "1.0.0";
            File.WriteAllText(Path.Combine(paths.Root, ServerContentRecord.FileName + ".4242-1760745600000000-7.bak"),
                Versions(Entry("2.0.0", Receipt(paths, "2.0.0"), hidden: true)));
            Assert(Selected(paths, state) == "1.0.0",
                "a launcher stopped between the two moves of a write left the record as a backup, which still withholds");
        }

        private static void DamagedBackupFreezesSelection(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            state.Find(ModId)!.Version = "1.0.0";
            File.WriteAllText(Path.Combine(paths.Root, ServerContentRecord.FileName + ".4242--5-7.bak"), "{");
            AssertFrozenAtOldVersion(paths, state);
        }

        private static void SetAsideRecordFreezesSelection(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            state.Find(ModId)!.Version = "1.0.0";
            // A repair moved a folder in the record's place aside, then could not write the record that replaces it,
            // as on a full disk, or the launcher stopped there.
            var folder = Path.Combine(paths.Root, "server-content.damaged-20261019T120000000000Z.json");
            Directory.CreateDirectory(folder);
            AssertFrozenAtOldVersion(paths, state);
            Directory.Delete(folder);
            File.WriteAllText(Path.Combine(paths.Root, "server-content.damaged-20261019T120000000000Z-2.json"), "x");
            AssertFrozenAtOldVersion(paths, state);
        }

        private static void ForeignCopyDoesNotFreezeSelection(ManagerPaths paths, string archives)
        {
            var state = Install(paths, archives, "1.0.0", "2.0.0");
            File.WriteAllText(Path.Combine(paths.Root, "server-content.damaged-copy.json"), "the player keeps this");
            var registry = new ModRegistry();
            var package = registry.Scan(paths, state).Single();
            Assert(!registry.LastServerContent.Frozen && package.Manifest!.Version == "2.0.0",
                "only a damaged copy the launcher names keeps a missing record damaged");
        }

        private static void UnreadableReceiptWithholds(ManagerPaths paths, string archives)
        {
            Install(paths, archives, "2.0.0");
            var version = paths.GetPackagePath(ModId, "2.0.0");
            var record = ServerContentRecord.Read(System.Text.Encoding.UTF8.GetBytes(
                Versions(Entry("2.0.0", new string('d', 64), hidden: true))));
            var selection = ServerContentSelection.For(record);
            Assert(!selection.Withholds(ModId, "2.0.0", version), "a receipt that is not the join's is the player's");
            var receipt = Path.Combine(version, PackageInstallReceipt.FileName);
            File.Delete(receipt);
            Directory.CreateDirectory(receipt);
            Assert(ServerContentSelection.For(record).Withholds(ModId, "2.0.0", version),
                "a receipt that cannot be read withholds the version the record names, failing closed");
            Assert(!ServerContentSelection.For(record).Withholds(ModId, "1.0.0", version),
                "a version the record does not name is never withheld");
        }

        private static void AssertFrozenAtOldVersion(ManagerPaths paths, ManagerState state)
        {
            var registry = new ModRegistry();
            var packages = registry.Scan(paths, state);
            Assert(registry.LastServerContent.Frozen && registry.LastServerContent.Problem.Length > 0,
                "a record that cannot be used freezes selection and says why");
            Assert(packages.Count == 1 && packages[0].IsValid && packages[0].Manifest!.Version == "1.0.0",
                "frozen selection takes the version that loaded before, never a newer one the record may withhold");
            Assert(state.Find(ModId)!.Version == "1.0.0", "the manager state keeps the player's version");

            var unrecorded = new ManagerState();
            var package = registry.Scan(paths, unrecorded).Single();
            Assert(!package.IsValid && package.Errors.Single().Contains("did not load before", StringComparison.Ordinal),
                "while frozen, a mod with no manager record does not load, and says why and what to do");
            Assert(unrecorded.Find(ModId) == null, "frozen selection adds no manager record");

            var disabled = new ManagerState();
            disabled.Mods.Add(new InstalledModState { Id = ModId, Version = "9.9.9", Enabled = false });
            Assert(registry.Scan(paths, disabled).Count == 0,
                "while frozen, a disabled mod whose version is gone stays quietly off");

            var pinned = new ManagerState();
            pinned.Mods.Add(new InstalledModState { Id = ModId, Version = "2.0.0", VersionPinned = true, Enabled = true });
            Assert(registry.Scan(paths, pinned).Single().Manifest!.Version == "2.0.0",
                "an exact pin, as every launcher start writes, is unaffected");
        }

        private static string Selected(ManagerPaths paths, ManagerState state, ServerContentSelection? content = null)
        {
            var package = new ModRegistry().Scan(paths, state, ManifestValidationContext.Current, content).Single();
            Assert(package.IsValid, "the selected package is valid: " + string.Join("; ", package.Errors));
            return package.Manifest!.Version;
        }

        private static ManagerState Install(ManagerPaths paths, string archives, params string[] versions)
        {
            var state = new ManagerState();
            var installer = new PackageInstaller();
            foreach (var version in versions)
            {
                var archive = Path.Combine(archives, ModId + "-" + version + ".topiaforgemod");
                if (!File.Exists(archive))
                {
                    WritePackageArchive(archive, version);
                }

                var result = installer.Install(archive, paths, state, restartRequired: false);
                Assert(result.Ok, "version " + version + " should install: " + string.Join("; ", result.Errors));
            }

            return state;
        }

        private static string RecordPath(ManagerPaths paths) => Path.Combine(paths.Root, ServerContentRecord.FileName);

        private static void WriteRecord(ManagerPaths paths, string text) => File.WriteAllText(RecordPath(paths), text);

        private static string Receipt(ManagerPaths paths, string version)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = File.ReadAllBytes(Path.Combine(paths.GetPackagePath(ModId, version), PackageInstallReceipt.FileName));
                return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
            }
        }

        private static string Versions(params string[] entries) => Record(entries);

        private static string Record(
            IEnumerable<string>? versions = null,
            IEnumerable<string>? withheld = null,
            IEnumerable<string>? added = null) =>
            "{\"addedRecords\":[" + string.Join(",", (added ?? Array.Empty<string>()).Select(id => "\"" + id + "\""))
            + "],\"schemaVersion\":1,\"versions\":[" + string.Join(",", versions ?? Array.Empty<string>())
            + "],\"withheld\":[" + string.Join(",", withheld ?? Array.Empty<string>()) + "]}";

        private static string Entry(string version, string receipt, bool hidden) =>
            "{\"hidden\":" + (hidden ? "true" : "false") + ",\"id\":\"" + ModId + "\",\"installedAt\":1760745600,\"receipt\":\""
            + receipt + "\",\"sha256\":\"" + new string('a', 64) + "\",\"version\":\"" + version + "\"}";

        private static string Withheld(string version, string receipt) =>
            "{\"id\":\"" + ModId + "\",\"receipt\":\"" + receipt + "\",\"version\":\"" + version + "\"}";

        private static void WritePackageArchive(string archivePath, string version)
        {
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var manifest = archive.CreateEntry("topiaforge.mod.json");
                using (var writer = new StreamWriter(manifest.Open()))
                {
                    writer.Write(JsonUtil.Serialize(new ModManifest
                    {
                        SchemaVersion = 6,
                        Id = ModId,
                        Name = "Server Content",
                        Author = new ModAuthor { Name = "TopiaForge" },
                        Version = version,
                        EntryAssembly = FixtureAssembly,
                        EntryType = FixtureType,
                        SupportedGameVersionRange = "*",
                        SupportedLoaderVersionRange = ">=0.2.0 <0.3.0",
                        SupportedSdkVersionRange = ">=0.2.0 <0.3.0"
                    }));
                }

                var entry = archive.CreateEntry(FixtureAssembly);
                using (var output = entry.Open())
                using (var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, FixtureAssembly)))
                {
                    input.CopyTo(output);
                }
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
